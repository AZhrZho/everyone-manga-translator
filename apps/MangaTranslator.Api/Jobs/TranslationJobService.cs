using MangaTranslator.Contracts;
using MangaTranslator.Api.Settings;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public sealed class TranslationJobService(
    IJobUpdatePublisher updates,
    IEnumerable<ITranslationExecutionSource> executionSources,
    ITranslationPageRunner pageRunner,
    IJobStore jobStore,
    IApplicationSettingsStore applicationSettings,
    ILogger<TranslationJobService> logger) : BackgroundService
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly Dictionary<string, MutableTranslationJob> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DrivenExecution> executions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<ExecutionMode, ITranslationExecutionSource> sources =
        executionSources.ToDictionary(source => source.Mode);

    public async Task<JobSnapshotResponse> StartAsync(
        StartTranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        if (!sources.TryGetValue(request.ExecutionMode, out var source))
        {
            throw new NotSupportedException($"翻译执行模式 {request.ExecutionMode} 尚未配置。");
        }

        var plan = await source.CreateAsync(request, cancellationToken);
        if (plan.Settings is null)
        {
            var settings = await applicationSettings.GetAsync(cancellationToken);
            plan = plan with
            {
                Settings = new TranslationTaskSettingsResponse(
                    settings.TranslationBatchConcurrency,
                    settings.TranslationBatchSize,
                    TranslationInputResolution.Original)
            };
        }
        else
        {
            plan = plan with
            {
                Settings = plan.Settings with { InputResolution = TranslationInputResolution.Original }
            };
        }
        var job = MutableTranslationJob.Create(
            $"translation-{Guid.NewGuid():N}",
            request.ProjectId,
            plan,
            DateTimeOffset.UtcNow);
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                jobs.Add(job.JobId, job);
            }

            await jobStore.SaveAsync(job.ToPersistent(), cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }

        LaunchExecution(job.JobId);
        return GetSnapshot(job.JobId) ?? job.ToSnapshot();
    }

    public bool TryGet(string jobId, out JobSnapshotResponse? snapshot)
    {
        snapshot = GetSnapshot(jobId);
        return snapshot is not null;
    }

    public bool HasActiveForProject(string projectId)
    {
        lock (gate)
        {
            return jobs.Values.Any(job =>
                string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && job.Status is JobStatus.Pending or JobStatus.Running);
        }
    }

    public async Task<IReadOnlyList<JobSnapshotResponse>> ListForProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var snapshots = (await jobStore.ListForProjectAsync(projectId, cancellationToken))
            .Where(item => string.Equals(item.Kind, "Translation", StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
        lock (gate)
        {
            return snapshots.Select(snapshot =>
            {
                if (jobs.TryGetValue(snapshot.JobId, out var existing))
                {
                    return existing.ToSnapshot();
                }

                return MutableTranslationJob.FromPersistent(snapshot).ToSnapshot();
            }).ToArray();
        }
    }

    public async Task<JobSnapshotResponse?> StopAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        JobSnapshotResponse? snapshot;
        PersistentJobSnapshot? persistent;
        DrivenExecution? execution = null;
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job)
                    || job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Stopped)
                {
                    return null;
                }

                job.Stop(DateTimeOffset.UtcNow);
                if (executions.Remove(jobId, out var running))
                {
                    execution = running;
                }

                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            execution?.Cancel();
            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }

        return snapshot;
    }

    public async Task<JobSnapshotResponse?> ResumeAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await RestoreStoppedJobAsync(jobId, cancellationToken);
        JobSnapshotResponse? snapshot;
        PersistentJobSnapshot? persistent;
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job) || job.Status is not JobStatus.Stopped)
                {
                    return null;
                }

                job.Resume(DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }

        LaunchExecution(jobId);
        return GetSnapshot(jobId) ?? snapshot;
    }

    private async Task RestoreStoppedJobAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (jobs.ContainsKey(jobId)) return;
        }

        var persisted = await jobStore.FindAsync(jobId, cancellationToken);
        if (persisted is null
            || !string.Equals(persisted.Kind, "Translation", StringComparison.Ordinal)
            || persisted.Status is not PersistentJobStatus.Stopped)
        {
            return;
        }

        lock (gate)
        {
            jobs.TryAdd(jobId, MutableTranslationJob.FromPersistent(persisted));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        DrivenExecution[] running;
        PersistentJobSnapshot[] stopped;
        lock (gate)
        {
            running = executions.Values.ToArray();
            executions.Clear();
            var now = DateTimeOffset.UtcNow;
            stopped = jobs.Values
                .Where(job => job.Status == JobStatus.Running)
                .Select(job =>
                {
                    job.Stop(now);
                    return job.ToPersistent();
                })
                .ToArray();
        }

        foreach (var execution in running)
        {
            execution.Cancel();
        }

        foreach (var snapshot in stopped)
        {
            await jobStore.SaveAsync(snapshot, cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestoreAsync(stoppingToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var restored = await jobStore.ListActiveAsync(cancellationToken);
            var interrupted = new List<PersistentJobSnapshot>();
            lock (gate)
            {
                foreach (var snapshot in restored.Where(item =>
                             string.Equals(item.Kind, "Translation", StringComparison.Ordinal)))
                {
                    if (jobs.ContainsKey(snapshot.JobId)) continue;
                    var job = MutableTranslationJob.FromPersistent(snapshot);
                    jobs[snapshot.JobId] = job;
                    if (snapshot.Status is PersistentJobStatus.Running or PersistentJobStatus.StopRequested)
                    {
                        interrupted.Add(job.ToPersistent());
                    }
                }
            }

            foreach (var snapshot in interrupted)
            {
                await jobStore.SaveAsync(snapshot, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to restore persisted translation jobs.");
        }
    }

    private void LaunchExecution(string jobId)
    {
        ITranslationExecutionSource source;
        DrivenExecution execution;
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var job)
                || job.Status != JobStatus.Running
                || !sources.TryGetValue(job.ExecutionMode, out source!)
                || executions.ContainsKey(jobId))
            {
                return;
            }

            execution = new DrivenExecution();
            executions.Add(jobId, execution);
        }

        execution.Task = RunExecutionAsync(jobId, source, execution);
    }

    private async Task RunExecutionAsync(
        string jobId,
        ITranslationExecutionSource source,
        DrivenExecution execution)
    {
        try
        {
            TranslationTaskSettingsResponse settings;
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job)) return;
                settings = job.Settings;
            }
            var concurrency = Math.Clamp(
                settings.BatchConcurrency,
                1,
                LocalApplicationSettingsStore.MaximumTranslationBatchConcurrency);
            await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(_ => RunWorkerAsync(jobId, source, settings, execution)));
            await FinalizeExecutionAsync(
                jobId,
                execution.ExecutionId,
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested && execution.Failure is null)
        {
            logger.LogInformation("Translation job {JobId} was stopped.", jobId);
        }
        catch (Exception exception)
        {
            execution.Cancel();
            var failure = execution.Failure ?? exception;
            logger.LogError(failure, "Translation job {JobId} failed.", jobId);
            await FailAsync(
                jobId,
                execution.ExecutionId,
                failure.Message,
                CancellationToken.None);
        }
        finally
        {
            lock (gate)
            {
                if (executions.TryGetValue(jobId, out var current)
                    && current.ExecutionId == execution.ExecutionId)
                {
                    executions.Remove(jobId);
                }
            }

            execution.Dispose();
        }
    }

    private async Task RunWorkerAsync(
        string jobId,
        ITranslationExecutionSource source,
        TranslationTaskSettingsResponse settings,
        DrivenExecution execution)
    {
        try
        {
            while (!execution.Token.IsCancellationRequested)
            {
                var pageIndices = await StartNextBatchAsync(
                    jobId,
                    execution.ExecutionId,
                    settings.BatchSize,
                    execution.Token);
                if (pageIndices.Count == 0) return;

                TranslationPageExecutionContext[] contexts;
                lock (gate)
                {
                    if (!IsCurrentExecution(jobId, execution.ExecutionId, out var job)) return;
                    contexts = pageIndices.Select(pageIndex => new TranslationPageExecutionContext(
                            job.JobId,
                            job.ProjectId,
                            job.RunId,
                            pageIndex,
                            (summary, detail, _) => ReportPageProgressAsync(
                                jobId,
                                execution.ExecutionId,
                                pageIndex,
                                summary,
                                detail,
                                CancellationToken.None)))
                        .ToArray();
                }

                try
                {
                    var batchResults = await source.ExecuteBatchAsync(contexts, settings, execution.Token);
                    ValidateBatchResults(contexts, batchResults);
                    foreach (var context in contexts)
                    {
                        var result = batchResults.Single(item => item.PageIndex == context.PageIndex).Result;
                        var outcome = await pageRunner.RunAsync(context, result, execution.Token);
                        await CompletePageAsync(
                            jobId,
                            execution.ExecutionId,
                            context.PageIndex,
                            outcome,
                            CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Translation batch for job {JobId}, pages {PageIndices} failed; remaining batches will continue.",
                        jobId,
                        string.Join(",", pageIndices));
                    await FailBatchAsync(
                        jobId,
                        execution.ExecutionId,
                        pageIndices,
                        exception.Message,
                        CancellationToken.None);
                }
            }
        }
        catch (Exception exception) when (!execution.IsCancellationRequested)
        {
            execution.Fail(exception);
            throw;
        }
    }

    private async Task<IReadOnlyList<int>> StartNextBatchAsync(
        string jobId,
        string executionId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<int> pageIndices;
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return [];
                }

                pageIndices = job.StartNextBatch(batchSize, DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
            return pageIndices;
        }
        finally
        {
            updateGate.Release();
        }
    }

    private static void ValidateBatchResults(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        IReadOnlyList<TranslationBatchExecutionResult> results)
    {
        var requested = contexts.Select(item => item.PageIndex).Order().ToArray();
        var returned = results.Select(item => item.PageIndex).Order().ToArray();
        if (results.Count != contexts.Count || !requested.SequenceEqual(returned))
        {
            throw new InvalidDataException(
                $"Translation batch result pages do not match the request. Requested: {string.Join(",", requested)}; returned: {string.Join(",", returned)}.");
        }
    }

    private async Task ReportPageProgressAsync(
        string jobId,
        string executionId,
        int pageIndex,
        string summary,
        string detail,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return;
                }

                job.UpdatePage(pageIndex, summary, detail, DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task CompletePageAsync(
        string jobId,
        string executionId,
        int pageIndex,
        TranslationPagePipelineOutcome outcome,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return;
                }

                job.CompletePage(
                    pageIndex,
                    outcome.ResponseSummary,
                    outcome.Detail,
                    DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task FailBatchAsync(
        string jobId,
        string executionId,
        IReadOnlyList<int> pageIndices,
        string error,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return;
                }

                job.FailBatch(pageIndices, error, DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task FinalizeExecutionAsync(
        string jobId,
        string executionId,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return;
                }

                job.FinalizeExecution(DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task FailAsync(
        string jobId,
        string executionId,
        string error,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!IsCurrentExecution(jobId, executionId, out var job)
                    || job.Status != JobStatus.Running)
                {
                    return;
                }

                job.Fail(error, DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private bool IsCurrentExecution(
        string jobId,
        string executionId,
        out MutableTranslationJob job)
    {
        if (jobs.TryGetValue(jobId, out job!)
            && executions.TryGetValue(jobId, out var execution)
            && execution.ExecutionId == executionId)
        {
            return true;
        }

        job = null!;
        return false;
    }

    private JobSnapshotResponse? GetSnapshot(string jobId)
    {
        lock (gate)
        {
            return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
        }
    }

    private sealed class MutableTranslationJob
    {
        private MutableTranslationJob(
            string jobId,
            string projectId,
            ExecutionMode executionMode,
            string runId,
            IReadOnlyList<MutableTranslationEvent> events,
            TranslationTaskSettingsResponse settings,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            JobStatus status)
        {
            JobId = jobId;
            ProjectId = projectId;
            ExecutionMode = executionMode;
            RunId = runId;
            Events = events.ToList();
            Settings = settings;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            Status = status;
        }

        public string JobId { get; }
        public string ProjectId { get; }
        public ExecutionMode ExecutionMode { get; }
        public string RunId { get; }
        public List<MutableTranslationEvent> Events { get; }
        public TranslationTaskSettingsResponse Settings { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; private set; }
        public JobStatus Status { get; private set; }

        public static MutableTranslationJob Create(
            string jobId,
            string projectId,
            TranslationExecutionPlan plan,
            DateTimeOffset now) => new(
                jobId,
                projectId,
                plan.ExecutionMode,
                plan.RunId,
                plan.Pages.Select(page => new MutableTranslationEvent(
                    $"page-pipeline-{page.Index:0000}",
                    page.Index,
                    $"处理第 {page.Index} 页",
                    plan.ExecutionMode == ExecutionMode.Live
                        ? plan.AnnotationCount > 0
                            ? $"执行文字检测、AI 翻译、清字和嵌字；运行输入包含 {plan.AnnotationCount} 条页面批注。"
                            : "执行文字检测、AI 翻译、清字和嵌字。"
                        : "读取已验证的真实 AI 流水线产物，并创建页面版本。",
                    now)).ToArray(),
                plan.Settings ?? new TranslationTaskSettingsResponse(
                    LocalApplicationSettingsStore.DefaultTranslationBatchConcurrency,
                    LocalApplicationSettingsStore.DefaultTranslationBatchSize,
                    TranslationInputResolution.Original),
                now,
                now,
                JobStatus.Running);

        public static MutableTranslationJob FromPersistent(PersistentJobSnapshot snapshot) => new(
            snapshot.JobId,
            snapshot.ProjectId,
            Enum.TryParse<ExecutionMode>(snapshot.ExecutionMode, out var mode)
                ? mode
                : ExecutionMode.Cached,
            snapshot.FixtureRunId
            ?? throw new InvalidDataException($"翻译任务 {snapshot.JobId} 缺少 Run ID。"),
            snapshot.Events.Select(item => new MutableTranslationEvent(item)).ToArray(),
            ParseSettings(snapshot.SettingsJson),
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            snapshot.Status is PersistentJobStatus.Running or PersistentJobStatus.StopRequested
                ? JobStatus.Stopped
                : Enum.TryParse<JobStatus>(snapshot.Status.ToString(), out var status)
                    ? status
                    : JobStatus.Stopped);

        public void Stop(DateTimeOffset now)
        {
            foreach (var item in Events.Where(item => item.Status == ProcessEventStatus.Running))
            {
                item.Pause(now);
            }

            Status = JobStatus.Stopped;
            UpdatedAt = now;
        }

        public void Resume(DateTimeOffset now)
        {
            Status = JobStatus.Running;
            UpdatedAt = now;
        }

        public IReadOnlyList<int> StartNextBatch(int batchSize, DateTimeOffset now)
        {
            var pending = Events
                .Where(item => item.Status == ProcessEventStatus.Pending)
                .Take(Math.Max(1, batchSize))
                .ToArray();
            foreach (var item in pending)
            {
                item.Start(now);
            }
            if (pending.Length > 0) UpdatedAt = now;

            if (pending.Length == 0 && Events.All(item => item.Status == ProcessEventStatus.Completed))
            {
                Status = JobStatus.Completed;
                UpdatedAt = now;
            }
            return pending.Select(item => item.PageIndex).ToArray();
        }

        public void UpdatePage(
            int pageIndex,
            string summary,
            string detail,
            DateTimeOffset now)
        {
            var running = FindRunning(pageIndex);
            running.UpdateProgress(summary, detail, now);
            UpdatedAt = now;
        }

        public void CompletePage(
            int pageIndex,
            string responseSummary,
            string detail,
            DateTimeOffset now)
        {
            var running = FindRunning(pageIndex);
            running.Complete(responseSummary, detail, now);
            if (Events.All(item => item.Status == ProcessEventStatus.Completed))
            {
                Status = JobStatus.Completed;
            }

            UpdatedAt = now;
        }

        public void FailBatch(
            IReadOnlyList<int> pageIndices,
            string detail,
            DateTimeOffset now)
        {
            var pages = pageIndices.ToHashSet();
            foreach (var item in Events.Where(item =>
                         pages.Contains(item.PageIndex)
                         && item.Status == ProcessEventStatus.Running))
            {
                item.Fail(detail, now);
            }

            UpdatedAt = now;
        }

        public void FinalizeExecution(DateTimeOffset now)
        {
            if (Events.Any(item => item.Status is ProcessEventStatus.Pending or ProcessEventStatus.Running))
            {
                throw new InvalidOperationException(
                    "Translation execution cannot be finalized while pages are pending or running.");
            }

            Status = Events.Any(item => item.Status == ProcessEventStatus.Failed)
                ? JobStatus.Failed
                : JobStatus.Completed;
            UpdatedAt = now;
        }

        public void Fail(string detail, DateTimeOffset now)
        {
            Status = JobStatus.Failed;
            UpdatedAt = now;
            var running = Events.Where(item => item.Status == ProcessEventStatus.Running).ToArray();
            if (running.Length == 0)
            {
                Events.Add(MutableTranslationEvent.Failure(detail, now));
            }
            else
            {
                foreach (var item in running) item.Fail(detail, now);
            }
        }

        public JobSnapshotResponse ToSnapshot() => new(
            JobId,
            ProjectId,
            Status,
            ExecutionMode,
            RunId,
            Status == JobStatus.Completed
                ? 100
                : Events.Count == 0
                    ? 0
                    : (int)Math.Round(
                        Events.Count(item => item.Status == ProcessEventStatus.Completed)
                        * 100d / Events.Count),
            Events.Select(item => item.ToResponse()).ToArray(),
            CreatedAt,
            UpdatedAt,
            Settings);

        public PersistentJobSnapshot ToPersistent() => new(
            JobId,
            ProjectId,
            "Translation",
            Enum.TryParse<PersistentJobStatus>(Status.ToString(), out var status)
                ? status
                : PersistentJobStatus.Failed,
            ExecutionMode.ToString(),
            RunId,
            ToSnapshot().ProgressPercent,
            Events.Select(item => item.ToPersistent()).ToArray(),
            CreatedAt,
            UpdatedAt,
            System.Text.Json.JsonSerializer.Serialize(Settings));

        private static TranslationTaskSettingsResponse ParseSettings(string? json)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var settings = System.Text.Json.JsonSerializer.Deserialize<TranslationTaskSettingsResponse>(json);
                    if (settings is not null)
                    {
                        return settings with { InputResolution = TranslationInputResolution.Original };
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Legacy or corrupt task metadata falls back to the previous one-page behavior.
                }
            }
            return new TranslationTaskSettingsResponse(5, 1, TranslationInputResolution.Original);
        }

        private MutableTranslationEvent FindRunning(int pageIndex) =>
            Events.FirstOrDefault(item =>
                item.PageIndex == pageIndex && item.Status == ProcessEventStatus.Running)
            ?? throw new InvalidOperationException(
                $"Page {pageIndex} is not the active translation event.");
    }

    private sealed class MutableTranslationEvent
    {
        public MutableTranslationEvent(
            string eventId,
            int pageIndex,
            string title,
            string requestSummary,
            DateTimeOffset now)
        {
            EventId = eventId;
            PageIndex = pageIndex;
            Title = title;
            RequestSummary = requestSummary;
            UpdatedAt = now;
        }

        public MutableTranslationEvent(PersistentProcessEvent source)
        {
            EventId = source.EventId;
            PageIndex = source.PageIndices.FirstOrDefault();
            Title = source.Title;
            RequestSummary = source.RequestSummary;
            ResponseSummary = source.ResponseSummary;
            Detail = source.Detail;
            Status = source.Status == PersistentProcessEventStatus.Running
                ? ProcessEventStatus.Pending
                : Enum.TryParse<ProcessEventStatus>(source.Status.ToString(), out var status)
                    ? status
                    : ProcessEventStatus.Pending;
            UpdatedAt = source.UpdatedAt;
        }

        private MutableTranslationEvent(string detail, DateTimeOffset now)
        {
            EventId = "failure";
            PageIndex = 0;
            Title = "翻译工作流执行失败";
            RequestSummary = "执行逐页翻译、清字和嵌字。";
            ResponseSummary = "任务未完成。";
            Detail = detail;
            Status = ProcessEventStatus.Failed;
            UpdatedAt = now;
        }

        public string EventId { get; }
        public int PageIndex { get; }
        public string Title { get; }
        public string RequestSummary { get; }
        public string? ResponseSummary { get; private set; }
        public string? Detail { get; private set; }
        public ProcessEventStatus Status { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }

        public static MutableTranslationEvent Failure(string detail, DateTimeOffset now) =>
            new(detail, now);

        public void Start(DateTimeOffset now)
        {
            Status = ProcessEventStatus.Running;
            ResponseSummary = "页面流水线已开始。";
            Detail = null;
            UpdatedAt = now;
        }

        public void Pause(DateTimeOffset now)
        {
            Status = ProcessEventStatus.Pending;
            ResponseSummary = "页面处理已暂停，可从检查点继续。";
            UpdatedAt = now;
        }

        public void UpdateProgress(string summary, string detail, DateTimeOffset now)
        {
            ResponseSummary = summary;
            Detail = detail;
            UpdatedAt = now;
        }

        public void Complete(string summary, string detail, DateTimeOffset now)
        {
            Status = ProcessEventStatus.Completed;
            ResponseSummary = summary;
            Detail = detail;
            UpdatedAt = now;
        }

        public void Fail(string detail, DateTimeOffset now)
        {
            Status = ProcessEventStatus.Failed;
            ResponseSummary = "该页面处理失败。";
            Detail = detail;
            UpdatedAt = now;
        }

        public ProcessEventResponse ToResponse() => new(
            EventId,
            "page-pipeline",
            Title,
            RequestSummary,
            ResponseSummary,
            Detail,
            Status,
            null,
            PageIndex > 0 ? [PageIndex] : [],
            UpdatedAt);

        public PersistentProcessEvent ToPersistent() => new(
            EventId,
            "page-pipeline",
            Title,
            RequestSummary,
            ResponseSummary,
            Detail,
            Enum.TryParse<PersistentProcessEventStatus>(Status.ToString(), out var status)
                ? status
                : PersistentProcessEventStatus.Failed,
            null,
            PageIndex > 0 ? [PageIndex] : [],
            UpdatedAt);
    }

    private sealed class DrivenExecution : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private Exception? failure;

        public string ExecutionId { get; } = Guid.NewGuid().ToString("N");
        public CancellationToken Token => cancellation.Token;
        public bool IsCancellationRequested => cancellation.IsCancellationRequested;
        public Exception? Failure => failure;
        public Task? Task { get; set; }

        public void Cancel() => cancellation.Cancel();

        public void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref failure, exception, null);
            cancellation.Cancel();
        }

        public void Dispose() => cancellation.Dispose();
    }
}
