using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public sealed class PrecheckJobService(
    IJobUpdatePublisher updates,
    IEnumerable<IPrecheckExecutionSource> executionSources,
    IJobStore jobStore,
    IPrecheckArtifactMaterializer artifactMaterializer,
    ILogger<PrecheckJobService> logger) : BackgroundService
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly Dictionary<string, MutableJob> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DrivenExecution> drivenExecutions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<ExecutionMode, IPrecheckExecutionSource> sources =
        executionSources.ToDictionary(source => source.Mode);

    public async Task<JobSnapshotResponse> StartAsync(
        StartPrecheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        if (!sources.TryGetValue(request.ExecutionMode, out var source))
        {
            throw new NotSupportedException($"执行模式 {request.ExecutionMode} 尚未配置。");
        }

        var plan = await source.CreateAsync(request.ProjectId, request.FixtureRunId, cancellationToken);
        var job = new MutableJob(
            Guid.NewGuid().ToString("N"),
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

        await artifactMaterializer.MaterializeAsync(
            request.ProjectId,
            plan.FixtureRunId,
            cancellationToken);
        if (source.DrivesExecution && job.Status == JobStatus.Running)
        {
            LaunchDrivenExecution(job.JobId);
        }

        return GetSnapshot(job.JobId) ?? job.ToSnapshot();
    }

    public async Task<IReadOnlyList<JobSnapshotResponse>> ListForProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var snapshots = (await jobStore.ListForProjectAsync(projectId, cancellationToken))
            .Where(snapshot => string.Equals(snapshot.Kind, "Precheck", StringComparison.Ordinal))
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ToArray();
        lock (gate)
        {
            return snapshots.Select(snapshot =>
            {
                if (jobs.TryGetValue(snapshot.JobId, out var existing))
                {
                    return existing.ToSnapshot();
                }

                return MutableJob.FromPersistent(snapshot).ToSnapshot();
            }).ToArray();
        }
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
                if (drivenExecutions.Remove(jobId, out var running))
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
        var shouldLaunch = false;

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
                shouldLaunch = IsDriven(job.ExecutionMode);
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

        if (shouldLaunch)
        {
            LaunchDrivenExecution(jobId);
        }

        return GetSnapshot(jobId) ?? snapshot;
    }

    public async Task<JobSnapshotResponse?> RetryAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await RestoreFailedJobAsync(jobId, cancellationToken);
        JobSnapshotResponse? snapshot;
        PersistentJobSnapshot? persistent;

        await updateGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job)
                    || job.Status is not JobStatus.Failed
                    || !IsDriven(job.ExecutionMode))
                {
                    return null;
                }

                // A failed execution can still be unwinding when the user retries
                // immediately. Its execution id prevents late callbacks from applying.
                drivenExecutions.Remove(jobId);
                job.Retry(DateTimeOffset.UtcNow);
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

        LaunchDrivenExecution(jobId);
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
            || !string.Equals(persisted.Kind, "Precheck", StringComparison.Ordinal)
            || persisted.Status is not PersistentJobStatus.Stopped)
        {
            return;
        }

        lock (gate)
        {
            jobs.TryAdd(jobId, MutableJob.FromPersistent(persisted));
        }
    }

    private async Task RestoreFailedJobAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (jobs.ContainsKey(jobId)) return;
        }

        var persisted = await jobStore.FindAsync(jobId, cancellationToken);
        if (persisted is null
            || !string.Equals(persisted.Kind, "Precheck", StringComparison.Ordinal)
            || persisted.Status is not PersistentJobStatus.Failed)
        {
            return;
        }

        lock (gate)
        {
            jobs.TryAdd(jobId, MutableJob.FromPersistent(persisted));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        DrivenExecution[] executions;
        PersistentJobSnapshot[] stopped;
        lock (gate)
        {
            executions = drivenExecutions.Values.ToArray();
            drivenExecutions.Clear();
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

        foreach (var execution in executions)
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            string[] simulatedJobIds;
            lock (gate)
            {
                simulatedJobIds = jobs.Values
                    .Where(job => job.Status == JobStatus.Running && !IsDriven(job.ExecutionMode))
                    .Select(job => job.JobId)
                    .ToArray();
            }

            foreach (var jobId in simulatedJobIds)
            {
                await AdvanceSimulatedAsync(jobId, stoppingToken);
            }
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var restored = await jobStore.ListActiveAsync(cancellationToken);
            var interrupted = new List<PersistentJobSnapshot>();
            lock (gate)
            {
                foreach (var snapshot in restored.Where(snapshot =>
                             string.Equals(snapshot.Kind, "Precheck", StringComparison.Ordinal)))
                {
                    if (jobs.ContainsKey(snapshot.JobId))
                    {
                        continue;
                    }

                    var job = MutableJob.FromPersistent(snapshot);
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
            logger.LogWarning(exception, "Failed to restore persisted precheck jobs.");
        }
    }

    private async Task AdvanceSimulatedAsync(string jobId, CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            JobSnapshotResponse? snapshot;
            PersistentJobSnapshot? persistent;
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job)
                    || job.Status != JobStatus.Running
                    || IsDriven(job.ExecutionMode))
                {
                    return;
                }

                job.AdvanceSimulated(DateTimeOffset.UtcNow);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, cancellationToken);
            await updates.PublishAsync(snapshot, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to advance precheck job {JobId}.", jobId);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private void LaunchDrivenExecution(string jobId)
    {
        IPrecheckExecutionSource source;
        DrivenExecution execution;
        PrecheckExecutionContext context;
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var job)
                || job.Status != JobStatus.Running
                || !sources.TryGetValue(job.ExecutionMode, out source!)
                || !source.DrivesExecution
                || drivenExecutions.ContainsKey(jobId))
            {
                return;
            }

            var runId = job.FixtureRunId
                        ?? throw new InvalidOperationException($"实时预处理任务 {jobId} 缺少 Run ID。");
            execution = new DrivenExecution();
            drivenExecutions.Add(jobId, execution);
            context = new PrecheckExecutionContext(
                job.JobId,
                job.ProjectId,
                runId,
                (executionEvent, _) => ReportDrivenEventAsync(
                    jobId,
                    execution.ExecutionId,
                    executionEvent,
                    CancellationToken.None));
        }

        execution.Task = RunDrivenExecutionAsync(jobId, source, context, execution);
    }

    private async Task RunDrivenExecutionAsync(
        string jobId,
        IPrecheckExecutionSource source,
        PrecheckExecutionContext context,
        DrivenExecution execution)
    {
        try
        {
            await source.ExecuteAsync(context, execution.Token);
            await CompleteDrivenAsync(jobId, execution.ExecutionId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            logger.LogInformation("Precheck job {JobId} was stopped.", jobId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Live precheck job {JobId} failed.", jobId);
            await FailDrivenAsync(jobId, execution.ExecutionId, exception.Message, CancellationToken.None);
        }
        finally
        {
            lock (gate)
            {
                if (drivenExecutions.TryGetValue(jobId, out var current)
                    && current.ExecutionId == execution.ExecutionId)
                {
                    drivenExecutions.Remove(jobId);
                }
            }

            execution.Dispose();
        }
    }

    private async Task ReportDrivenEventAsync(
        string jobId,
        string executionId,
        PrecheckExecutionEvent executionEvent,
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

                job.Upsert(executionEvent, DateTimeOffset.UtcNow);
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

    private async Task CompleteDrivenAsync(
        string jobId,
        string executionId,
        CancellationToken cancellationToken)
    {
        await UpdateDrivenTerminalStateAsync(
            jobId,
            executionId,
            job => job.Complete(DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task FailDrivenAsync(
        string jobId,
        string executionId,
        string error,
        CancellationToken cancellationToken)
    {
        await UpdateDrivenTerminalStateAsync(
            jobId,
            executionId,
            job => job.Fail(error, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task UpdateDrivenTerminalStateAsync(
        string jobId,
        string executionId,
        Action<MutableJob> update,
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

                update(job);
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
        out MutableJob job)
    {
        if (jobs.TryGetValue(jobId, out job!)
            && drivenExecutions.TryGetValue(jobId, out var execution)
            && execution.ExecutionId == executionId)
        {
            return true;
        }

        job = null!;
        return false;
    }

    private bool IsDriven(ExecutionMode mode) =>
        sources.TryGetValue(mode, out var source) && source.DrivesExecution;

    private JobSnapshotResponse? GetSnapshot(string jobId)
    {
        lock (gate)
        {
            return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
        }
    }

    private sealed class MutableJob
    {
        public MutableJob(string jobId, string projectId, PrecheckExecutionPlan plan, DateTimeOffset now)
        {
            JobId = jobId;
            ProjectId = projectId;
            ExecutionMode = plan.ExecutionMode;
            FixtureRunId = plan.FixtureRunId;
            Events = plan.Events.Select(item => new MutableEvent(item, now, plan.AlreadyCompleted)).ToList();
            CreatedAt = now;
            UpdatedAt = now;
            Status = plan.AlreadyCompleted ? JobStatus.Completed : JobStatus.Running;
        }

        private MutableJob(PersistentJobSnapshot snapshot)
        {
            JobId = snapshot.JobId;
            ProjectId = snapshot.ProjectId;
            ExecutionMode = Enum.TryParse<ExecutionMode>(snapshot.ExecutionMode, out var executionMode)
                ? executionMode
                : ExecutionMode.Cached;
            FixtureRunId = snapshot.FixtureRunId;
            Events = snapshot.Events.Select(item => new MutableEvent(item)).ToList();
            CreatedAt = snapshot.CreatedAt;
            UpdatedAt = snapshot.UpdatedAt;
            Status = snapshot.Status is PersistentJobStatus.Running or PersistentJobStatus.StopRequested
                ? JobStatus.Stopped
                : Enum.TryParse<JobStatus>(snapshot.Status.ToString(), out var status)
                    ? status
                    : JobStatus.Stopped;
        }

        public static MutableJob FromPersistent(PersistentJobSnapshot snapshot) => new(snapshot);

        public string JobId { get; }
        public string ProjectId { get; }
        public ExecutionMode ExecutionMode { get; }
        public string? FixtureRunId { get; }
        public JobStatus Status { get; private set; }
        public List<MutableEvent> Events { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; private set; }

        public void Stop(DateTimeOffset now)
        {
            Status = JobStatus.Stopped;
            UpdatedAt = now;
        }

        public void Resume(DateTimeOffset now)
        {
            Status = JobStatus.Running;
            UpdatedAt = now;
        }

        public void Retry(DateTimeOffset now)
        {
            Events.RemoveAll(item => item.IsSyntheticFailure);
            foreach (var item in Events.Where(item => item.Status == ProcessEventStatus.Failed))
            {
                item.ResetForRetry(now);
            }

            Status = JobStatus.Running;
            UpdatedAt = now;
        }

        public void Complete(DateTimeOffset now)
        {
            foreach (var item in Events.Where(item => item.Status == ProcessEventStatus.Running))
            {
                item.CompleteFromPlan(now);
            }

            Status = JobStatus.Completed;
            UpdatedAt = now;
        }

        public void Fail(string error, DateTimeOffset now)
        {
            var running = Events.Where(item => item.Status == ProcessEventStatus.Running).ToArray();
            if (running.Length == 0)
            {
                Events.Add(MutableEvent.Failure(error, now));
            }
            else
            {
                foreach (var item in running)
                {
                    item.Fail(error, now);
                }
            }

            Status = JobStatus.Failed;
            UpdatedAt = now;
        }

        public void Upsert(PrecheckExecutionEvent source, DateTimeOffset now)
        {
            var existing = Events.FirstOrDefault(item =>
                string.Equals(item.EventId, source.EventId, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Apply(source, now);
            }
            else
            {
                var created = new MutableEvent(source, now, false);
                var insertionIndex = Events.FindIndex(item => StageOrder(item.Stage) > StageOrder(source.Stage));
                if (insertionIndex < 0)
                {
                    Events.Add(created);
                }
                else
                {
                    Events.Insert(insertionIndex, created);
                }
            }

            UpdatedAt = now;
        }

        public void AdvanceSimulated(DateTimeOffset now)
        {
            var running = Events.Where(item => item.Status == ProcessEventStatus.Running).ToArray();
            var pending = Events.Where(item => item.Status == ProcessEventStatus.Pending).ToArray();

            if (running.Length > 0)
            {
                foreach (var item in running)
                {
                    item.CompleteFromPlan(now);
                }
            }
            else if (pending.Length > 0)
            {
                var next = pending[0];
                var parallelGroup = next.ParallelGroup;
                foreach (var item in parallelGroup is null
                             ? [next]
                             : pending.Where(candidate => candidate.ParallelGroup == parallelGroup))
                {
                    item.Start(now);
                }
            }
            else
            {
                Status = JobStatus.Completed;
            }

            UpdatedAt = now;
        }

        public JobSnapshotResponse ToSnapshot()
        {
            var completed = Events.Count(item => item.Status == ProcessEventStatus.Completed);
            var progress = Status == JobStatus.Completed
                ? 100
                : Events.Count == 0
                    ? 0
                    : (int)Math.Round(completed * 100d / Events.Count);
            return new JobSnapshotResponse(
                JobId,
                ProjectId,
                Status,
                ExecutionMode,
                FixtureRunId,
                progress,
                Events.Select(item => item.ToResponse()).ToArray(),
                CreatedAt,
                UpdatedAt);
        }

        public PersistentJobSnapshot ToPersistent() => new(
            JobId,
            ProjectId,
            "Precheck",
            Enum.TryParse<PersistentJobStatus>(Status.ToString(), out var status)
                ? status
                : PersistentJobStatus.Failed,
            ExecutionMode.ToString(),
            FixtureRunId,
            ToSnapshot().ProgressPercent,
            Events.Select(item => item.ToPersistent()).ToArray(),
            CreatedAt,
            UpdatedAt);

        private static int StageOrder(string stage) => stage switch
        {
            "manifest" => 10,
            "scan-plan" => 20,
            "page-scan" => 30,
            "page-routing" => 40,
            "translation-context" => 50,
            "thumbnail" => 20,
            "planning" => 30,
            "batch-analysis" => 40,
            "merge" => 50,
            "automatic-review" => 60,
            "semantic-merge" => 70,
            "book-manifest" => 80,
            "consolidation" => 90,
            _ => 100
        };
    }

    private sealed class MutableEvent
    {
        public MutableEvent(PrecheckExecutionEvent source, DateTimeOffset now, bool alreadyCompleted)
        {
            ApplyMetadata(source);
            Status = alreadyCompleted ? ProcessEventStatus.Completed : source.InitialStatus;
            UpdatedAt = now;
            SetOutputForStatus();
        }

        public MutableEvent(PersistentProcessEvent source)
        {
            EventId = source.EventId;
            Stage = source.Stage;
            Title = source.Title;
            RequestSummary = source.RequestSummary;
            CompletionResponse = source.ResponseSummary ?? "该步骤已恢复，可继续执行。";
            CompletionDetail = source.Detail ?? string.Empty;
            ResponseSummary = source.ResponseSummary;
            Detail = source.Detail;
            Status = source.Status is PersistentProcessEventStatus.Running
                ? ProcessEventStatus.Pending
                : Enum.TryParse<ProcessEventStatus>(source.Status.ToString(), out var status)
                    ? status
                    : ProcessEventStatus.Pending;
            ParallelGroup = source.ParallelGroup;
            PageIndices = source.PageIndices;
            UpdatedAt = source.UpdatedAt;
        }

        private MutableEvent(string error, DateTimeOffset now)
        {
            EventId = "failure";
            Stage = "failure";
            Title = "预处理执行失败";
            RequestSummary = "执行实时预处理工作流。";
            CompletionResponse = "预处理未完成。";
            CompletionDetail = error;
            ResponseSummary = CompletionResponse;
            Detail = error;
            Status = ProcessEventStatus.Failed;
            ParallelGroup = null;
            PageIndices = [];
            UpdatedAt = now;
        }

        public string EventId { get; private set; } = string.Empty;
        public string Stage { get; private set; } = string.Empty;
        public string Title { get; private set; } = string.Empty;
        public string RequestSummary { get; private set; } = string.Empty;
        public string CompletionResponse { get; private set; } = string.Empty;
        public string CompletionDetail { get; private set; } = string.Empty;
        public string? ResponseSummary { get; private set; }
        public string? Detail { get; private set; }
        public ProcessEventStatus Status { get; private set; }
        public string? ParallelGroup { get; private set; }
        public IReadOnlyList<int> PageIndices { get; private set; } = [];
        public DateTimeOffset UpdatedAt { get; private set; }

        public bool IsSyntheticFailure =>
            string.Equals(EventId, "failure", StringComparison.Ordinal)
            && string.Equals(Stage, "failure", StringComparison.Ordinal);

        public static MutableEvent Failure(string error, DateTimeOffset now) => new(error, now);

        public void Apply(PrecheckExecutionEvent source, DateTimeOffset now)
        {
            ApplyMetadata(source);
            Status = source.InitialStatus;
            UpdatedAt = now;
            SetOutputForStatus();
        }

        public void Start(DateTimeOffset now)
        {
            Status = ProcessEventStatus.Running;
            ResponseSummary = null;
            Detail = null;
            UpdatedAt = now;
        }

        public void CompleteFromPlan(DateTimeOffset now)
        {
            Status = ProcessEventStatus.Completed;
            ResponseSummary = CompletionResponse;
            Detail = CompletionDetail;
            UpdatedAt = now;
        }

        public void Fail(string error, DateTimeOffset now)
        {
            Status = ProcessEventStatus.Failed;
            ResponseSummary = "该步骤执行失败。";
            Detail = error;
            UpdatedAt = now;
        }

        public void ResetForRetry(DateTimeOffset now)
        {
            Status = ProcessEventStatus.Pending;
            ResponseSummary = null;
            Detail = null;
            UpdatedAt = now;
        }

        public ProcessEventResponse ToResponse() => new(
            EventId,
            Stage,
            Title,
            RequestSummary,
            ResponseSummary,
            Detail,
            Status,
            ParallelGroup,
            PageIndices,
            UpdatedAt);

        public PersistentProcessEvent ToPersistent() => new(
            EventId,
            Stage,
            Title,
            RequestSummary,
            ResponseSummary,
            Detail,
            Enum.TryParse<PersistentProcessEventStatus>(Status.ToString(), out var status)
                ? status
                : PersistentProcessEventStatus.Failed,
            ParallelGroup,
            PageIndices,
            UpdatedAt);

        private void ApplyMetadata(PrecheckExecutionEvent source)
        {
            EventId = source.EventId;
            Stage = source.Stage;
            Title = source.Title;
            RequestSummary = source.RequestSummary;
            CompletionResponse = source.CompletionResponse;
            CompletionDetail = source.CompletionDetail;
            ParallelGroup = source.ParallelGroup;
            PageIndices = source.PageIndices;
        }

        private void SetOutputForStatus()
        {
            if (Status == ProcessEventStatus.Completed)
            {
                ResponseSummary = CompletionResponse;
                Detail = CompletionDetail;
            }
            else
            {
                ResponseSummary = null;
                Detail = null;
            }
        }
    }

    private sealed class DrivenExecution : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();

        public string ExecutionId { get; } = Guid.NewGuid().ToString("N");
        public CancellationToken Token => cancellation.Token;
        public bool IsCancellationRequested => cancellation.IsCancellationRequested;
        public Task? Task { get; set; }

        public void Cancel() => cancellation.Cancel();

        public void Dispose() => cancellation.Dispose();
    }
}
