using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;
using ContractExportFormat = MangaTranslator.Contracts.ProjectExportFormat;
using ProjectExportFormat = MangaTranslator.ProjectSystem.ProjectExportFormat;

namespace MangaTranslator.Api.Jobs;

public sealed class ExportJobService(
    IExportService exports,
    IProjectEditLock editLock,
    PrecheckJobService precheckJobs,
    TranslationJobService translationJobs,
    ProjectOperationGate operationGate,
    IJobStore jobStore,
    IJobUpdatePublisher updates,
    ILogger<ExportJobService> logger) : BackgroundService
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly Dictionary<string, MutableExportJob> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> executions = new(StringComparer.Ordinal);

    public async Task<JobSnapshotResponse> StartAsync(
        StartExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        if (!request.IncludeBackground && !request.IncludeLettering)
        {
            throw new InvalidOperationException("至少选择导出底图或嵌字中的一项。");
        }
        using var operation = await operationGate.EnterAsync(
            request.ProjectId,
            cancellationToken);
        if (precheckJobs.HasActiveForProject(request.ProjectId)
            || translationJobs.HasActiveForProject(request.ProjectId))
        {
            throw new InvalidOperationException("项目仍有工作流任务在运行，请停止或等待任务完成后再导出。");
        }

        var lease = editLock.AcquireExport(request.ProjectId);
        var job = MutableExportJob.Create(
            $"export-{Guid.NewGuid():N}",
            request.ProjectId,
            request.Format,
            DateTimeOffset.UtcNow);
        var execution = new CancellationTokenSource();
        try
        {
            await updateGate.WaitAsync(cancellationToken);
            try
            {
                lock (gate)
                {
                    jobs.Add(job.JobId, job);
                    executions.Add(job.JobId, execution);
                }
                await jobStore.SaveAsync(job.ToPersistent(), cancellationToken);
            }
            finally
            {
                updateGate.Release();
            }
        }
        catch
        {
            lock (gate)
            {
                jobs.Remove(job.JobId);
                executions.Remove(job.JobId);
            }
            execution.Dispose();
            lease.Dispose();
            throw;
        }

        _ = RunAsync(job.JobId, request, lease, execution);
        return job.ToSnapshot();
    }

    public bool TryGet(string jobId, out JobSnapshotResponse? snapshot)
    {
        lock (gate)
        {
            snapshot = jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
            return snapshot is not null;
        }
    }

    public async Task<IReadOnlyList<JobSnapshotResponse>> ListForProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var snapshots = (await jobStore.ListForProjectAsync(projectId, cancellationToken))
            .Where(snapshot => string.Equals(snapshot.Kind, "Export", StringComparison.Ordinal))
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ToArray();
        lock (gate)
        {
            return snapshots.Select(snapshot =>
                jobs.TryGetValue(snapshot.JobId, out var existing)
                    ? existing.ToSnapshot()
                    : MutableExportJob.FromPersistent(snapshot).ToSnapshot()).ToArray();
        }
    }

    public async Task<JobSnapshotResponse?> StopAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? execution;
        JobSnapshotResponse snapshot;
        PersistentJobSnapshot persistent;
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

                job.Stop("用户已停止导出。", DateTimeOffset.UtcNow);
                executions.TryGetValue(jobId, out execution);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource[] running;
        PersistentJobSnapshot[] stopped;
        lock (gate)
        {
            running = executions.Values.ToArray();
            var now = DateTimeOffset.UtcNow;
            stopped = jobs.Values
                .Where(job => job.Status is JobStatus.Pending or JobStatus.Running)
                .Select(job =>
                {
                    job.Stop("应用退出，导出已停止。", now);
                    return job.ToPersistent();
                })
                .ToArray();
        }

        foreach (var execution in running) execution.Cancel();
        foreach (var snapshot in stopped)
        {
            await jobStore.SaveAsync(snapshot, cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var active = (await jobStore.ListActiveAsync(stoppingToken))
                .Where(snapshot => string.Equals(snapshot.Kind, "Export", StringComparison.Ordinal))
                .ToArray();
            foreach (var snapshot in active)
            {
                var restored = MutableExportJob.FromPersistent(snapshot);
                restored.Stop("上次运行在导出完成前退出，未完成产物已丢弃。", DateTimeOffset.UtcNow);
                lock (gate) jobs[restored.JobId] = restored;
                await jobStore.SaveAsync(restored.ToPersistent(), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task RunAsync(
        string jobId,
        StartExportRequest request,
        IDisposable lease,
        CancellationTokenSource execution)
    {
        using (lease)
        {
            try
            {
                await PublishAsync(jobId, job => job.MarkRunning("正在准备离线渲染。"));
                var result = await exports.ExportAsync(
                    request.ProjectId,
                    new ExportProjectCommand(
                        request.DestinationToken,
                        Enum.Parse<ProjectExportFormat>(request.Format.ToString()),
                        request.IncludeBackground,
                        request.IncludeLettering,
                        Enum.Parse<MangaTranslator.ProjectSystem.ProjectExportResolution>(request.Resolution.ToString())),
                    async (progress, cancellationToken) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await PublishAsync(jobId, job => job.Report(progress));
                    },
                    execution.Token);
                await PublishAsync(jobId, job => job.Complete(result));
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                logger.LogInformation("Export job {JobId} was stopped.", jobId);
                await PublishAsync(jobId, job => job.Stop("用户已停止导出。", DateTimeOffset.UtcNow));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Export job {JobId} failed.", jobId);
                await PublishAsync(jobId, job => job.Fail(exception.Message));
            }
            finally
            {
                lock (gate) executions.Remove(jobId);
                execution.Dispose();
            }
        }
    }

    private async Task PublishAsync(string jobId, Action<MutableExportJob> update)
    {
        await updateGate.WaitAsync();
        try
        {
            JobSnapshotResponse snapshot;
            PersistentJobSnapshot persistent;
            lock (gate)
            {
                if (!jobs.TryGetValue(jobId, out var job)) return;
                update(job);
                snapshot = job.ToSnapshot();
                persistent = job.ToPersistent();
            }

            await jobStore.SaveAsync(persistent, CancellationToken.None);
            await updates.PublishAsync(snapshot, CancellationToken.None);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private sealed class MutableExportJob
    {
        private MutableExportJob(
            string jobId,
            string projectId,
            ContractExportFormat format,
            DateTimeOffset createdAt)
        {
            JobId = jobId;
            ProjectId = projectId;
            Format = format;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
            Status = JobStatus.Pending;
            Detail = "等待导出任务启动。";
        }

        private MutableExportJob(PersistentJobSnapshot snapshot)
        {
            JobId = snapshot.JobId;
            ProjectId = snapshot.ProjectId;
            Format = Enum.TryParse<ContractExportFormat>(snapshot.FixtureRunId, out var format)
                ? format
                : ContractExportFormat.Folder;
            CreatedAt = snapshot.CreatedAt;
            UpdatedAt = snapshot.UpdatedAt;
            Status = snapshot.Status switch
            {
                PersistentJobStatus.Pending => JobStatus.Pending,
                PersistentJobStatus.Running or PersistentJobStatus.StopRequested => JobStatus.Running,
                PersistentJobStatus.Stopped => JobStatus.Stopped,
                PersistentJobStatus.Completed => JobStatus.Completed,
                _ => JobStatus.Failed
            };
            ProgressPercent = snapshot.ProgressPercent;
            Detail = snapshot.Events.FirstOrDefault()?.Detail
                ?? snapshot.Events.FirstOrDefault()?.ResponseSummary
                ?? "导出任务状态已恢复。";
        }

        public string JobId { get; }
        public string ProjectId { get; }
        public ContractExportFormat Format { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; private set; }
        public JobStatus Status { get; private set; }
        public int ProgressPercent { get; private set; }
        public string Detail { get; private set; }

        public static MutableExportJob Create(string jobId, string projectId, ContractExportFormat format, DateTimeOffset createdAt) =>
            new(jobId, projectId, format, createdAt);

        public static MutableExportJob FromPersistent(PersistentJobSnapshot snapshot) => new(snapshot);

        public void MarkRunning(string detail)
        {
            if (Status == JobStatus.Stopped) return;
            Status = JobStatus.Running;
            Detail = detail;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void Report(ExportProgress progress)
        {
            if (Status == JobStatus.Stopped) return;
            Status = JobStatus.Running;
            ProgressPercent = progress.TotalPages == 0
                ? 0
                : Math.Clamp((int)Math.Round(progress.CompletedPages * 100d / progress.TotalPages), 0, 99);
            Detail = progress.Message;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void Complete(ExportResult result)
        {
            if (Status == JobStatus.Stopped) return;
            Status = JobStatus.Completed;
            ProgressPercent = 100;
            Detail = $"已导出 {result.PageCount} 页到 {result.DestinationPath}";
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void Stop(string detail, DateTimeOffset now)
        {
            if (Status is JobStatus.Completed or JobStatus.Failed) return;
            Status = JobStatus.Stopped;
            Detail = detail;
            UpdatedAt = now;
        }

        public void Fail(string detail)
        {
            if (Status == JobStatus.Stopped) return;
            Status = JobStatus.Failed;
            Detail = detail;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public JobSnapshotResponse ToSnapshot() => new(
            JobId,
            ProjectId,
            Status,
            ExecutionMode.Cached,
            Format.ToString(),
            ProgressPercent,
            [ToResponseEvent()],
            CreatedAt,
            UpdatedAt);

        public PersistentJobSnapshot ToPersistent() => new(
            JobId,
            ProjectId,
            "Export",
            Status switch
            {
                JobStatus.Pending => PersistentJobStatus.Pending,
                JobStatus.Running => PersistentJobStatus.Running,
                JobStatus.Stopped => PersistentJobStatus.Stopped,
                JobStatus.Completed => PersistentJobStatus.Completed,
                _ => PersistentJobStatus.Failed
            },
            ExecutionMode.Cached.ToString(),
            Format.ToString(),
            ProgressPercent,
            [new PersistentProcessEvent(
                "export-progress",
                "export",
                $"导出 {Format}",
                "锁定项目编辑并使用后端离线渲染器逐页输出。",
                Status is JobStatus.Pending ? null : Detail,
                Status is JobStatus.Failed or JobStatus.Stopped ? Detail : null,
                Status switch
                {
                    JobStatus.Pending or JobStatus.Stopped => PersistentProcessEventStatus.Pending,
                    JobStatus.Running => PersistentProcessEventStatus.Running,
                    JobStatus.Completed => PersistentProcessEventStatus.Completed,
                    _ => PersistentProcessEventStatus.Failed
                },
                null,
                [],
                UpdatedAt)],
            CreatedAt,
            UpdatedAt);

        private ProcessEventResponse ToResponseEvent() => new(
            "export-progress",
            "export",
            $"导出 {Format}",
            "锁定项目编辑并使用后端离线渲染器逐页输出。",
            Status is JobStatus.Pending ? null : Detail,
            Status is JobStatus.Failed or JobStatus.Stopped ? Detail : null,
            Status switch
            {
                JobStatus.Pending or JobStatus.Stopped => ProcessEventStatus.Pending,
                JobStatus.Running => ProcessEventStatus.Running,
                JobStatus.Completed => ProcessEventStatus.Completed,
                _ => ProcessEventStatus.Failed
            },
            null,
            [],
            UpdatedAt);
    }
}
