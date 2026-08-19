using System.Collections.Concurrent;
using System.Diagnostics;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ContractExportFormat = MangaTranslator.Contracts.ProjectExportFormat;
using ProjectExportFormat = MangaTranslator.ProjectSystem.ProjectExportFormat;

namespace MangaTranslator.Api.Tests;

public sealed class ExportJobServiceTests
{
    [Fact]
    public async Task ExportPersistsProgressAndCompletionAndReleasesEditLock()
    {
        var exports = new CompletingExportService();
        var store = new InMemoryJobStore();
        var editLock = new RecordingEditLock();
        var publisher = new RecordingPublisher();
        var service = CreateService(exports, editLock, store, publisher);

        var started = await service.StartAsync(
            new StartExportRequest("project-1", "destination", ContractExportFormat.Zip),
            CancellationToken.None);
        var completed = await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(100, completed.ProgressPercent);
        Assert.False(editLock.IsLocked("project-1"));
        Assert.Contains(publisher.Snapshots, item => item.ProgressPercent == 50);
        Assert.Equal(
            PersistentJobStatus.Completed,
            (await store.GetAsync("project-1", started.JobId, CancellationToken.None))?.Status);
        Assert.Contains(
            await service.ListForProjectAsync("project-1", CancellationToken.None),
            item => item.JobId == started.JobId && item.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task StopCancelsExportPersistsStoppedStateAndReleasesEditLock()
    {
        var exports = new BlockingExportService();
        var store = new InMemoryJobStore();
        var editLock = new RecordingEditLock();
        var service = CreateService(exports, editLock, store, new RecordingPublisher());

        var started = await service.StartAsync(
            new StartExportRequest("project-2", "destination", ContractExportFormat.Pdf),
            CancellationToken.None);
        await exports.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await service.StopAsync(started.JobId, CancellationToken.None);
        await exports.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Stopped, stopped?.Status);
        Assert.Equal(
            PersistentJobStatus.Stopped,
            (await store.GetAsync("project-2", started.JobId, CancellationToken.None))?.Status);
        await WaitUntilAsync(() => !editLock.IsLocked("project-2"));
    }

    [Fact]
    public async Task RestoredActiveExportIsMarkedStoppedBecauseDestinationGrantCannotResume()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "export-interrupted",
                "project-3",
                "Export",
                PersistentJobStatus.Running,
                ExecutionMode.Cached.ToString(),
                ContractExportFormat.Folder.ToString(),
                40,
                [new PersistentProcessEvent(
                    "export-progress",
                    "export",
                    "Export",
                    "request",
                    "running",
                    null,
                    PersistentProcessEventStatus.Running,
                    null,
                    [],
                    now)],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new CompletingExportService(),
            new RecordingEditLock(),
            store,
            new RecordingPublisher());

        await ((IHostedService)service).StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () =>
                (await store.GetAsync("project-3", "export-interrupted", CancellationToken.None))?.Status
                == PersistentJobStatus.Stopped);
            Assert.True(service.TryGet("export-interrupted", out var snapshot));
            Assert.Equal(JobStatus.Stopped, snapshot?.Status);
        }
        finally
        {
            await ((IHostedService)service).StopAsync(CancellationToken.None);
        }
    }

    private static ExportJobService CreateService(
        IExportService exports,
        IProjectEditLock editLock,
        IJobStore store,
        IJobUpdatePublisher publisher)
    {
        var precheck = new PrecheckJobService(
            publisher,
            [],
            store,
            new NoOpArtifactMaterializer(),
            NullLogger<PrecheckJobService>.Instance);
        var translation = new TranslationJobService(
            publisher,
            [],
            new UnusedPageRunner(),
            store,
            new FixedSettingsStore(),
            NullLogger<TranslationJobService>.Instance);
        return new ExportJobService(
            exports,
            editLock,
            precheck,
            translation,
            new ProjectOperationGate(),
            store,
            publisher,
            NullLogger<ExportJobService>.Instance);
    }

    private static async Task<JobSnapshotResponse> WaitForStatusAsync(
        ExportJobService service,
        string jobId,
        JobStatus expected)
    {
        JobSnapshotResponse? result = null;
        await WaitUntilAsync(() =>
        {
            if (service.TryGet(jobId, out var snapshot) && snapshot?.Status == expected)
            {
                result = snapshot;
                return true;
            }
            return false;
        });
        return result!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CompletingExportService : IExportService
    {
        public async Task<ExportResult> ExportAsync(
            string projectId,
            ExportProjectCommand command,
            Func<ExportProgress, CancellationToken, Task>? reportAsync,
            CancellationToken cancellationToken)
        {
            if (reportAsync is not null)
            {
                await reportAsync(new ExportProgress(1, 2, "render", "half"), cancellationToken);
                await reportAsync(new ExportProgress(2, 2, "render", "done"), cancellationToken);
            }
            return new ExportResult(command.Format, "destination", 2);
        }
    }

    private sealed class BlockingExportService : IExportService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExportResult> ExportAsync(
            string projectId,
            ExportProjectCommand command,
            Func<ExportProgress, CancellationToken, Task>? reportAsync,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Cancelled.TrySetResult();
            }
            throw new UnreachableException();
        }
    }

    private sealed class RecordingEditLock : IProjectEditLock
    {
        private readonly HashSet<string> locked = new(StringComparer.Ordinal);
        public bool IsLocked(string projectId) { lock (locked) return locked.Contains(projectId); }
        public IDisposable AcquireExport(string projectId)
        {
            lock (locked)
            {
                if (!locked.Add(projectId)) throw new InvalidOperationException("locked");
            }
            return new Lease(() => { lock (locked) locked.Remove(projectId); });
        }
        private sealed class Lease(Action dispose) : IDisposable
        {
            private int disposed;
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
        }
    }

    private sealed class RecordingPublisher : IJobUpdatePublisher
    {
        public ConcurrentQueue<JobSnapshotResponse> Snapshots { get; } = [];
        public Task PublishAsync(JobSnapshotResponse snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Enqueue(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryJobStore : IJobStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, PersistentJobSnapshot> snapshots = new(StringComparer.Ordinal);
        public Task SaveAsync(PersistentJobSnapshot snapshot, CancellationToken cancellationToken)
        {
            lock (gate) snapshots[snapshot.JobId] = snapshot;
            return Task.CompletedTask;
        }
        public Task<PersistentJobSnapshot?> GetAsync(string projectId, string jobId, CancellationToken cancellationToken)
        {
            lock (gate) return Task.FromResult(snapshots.GetValueOrDefault(jobId));
        }
        public Task<IReadOnlyList<PersistentJobSnapshot>> ListForProjectAsync(string projectId, CancellationToken cancellationToken)
        {
            lock (gate) return Task.FromResult<IReadOnlyList<PersistentJobSnapshot>>(snapshots.Values.Where(item => item.ProjectId == projectId).ToArray());
        }
        public Task<IReadOnlyList<PersistentJobSnapshot>> ListActiveAsync(CancellationToken cancellationToken)
        {
            lock (gate) return Task.FromResult<IReadOnlyList<PersistentJobSnapshot>>(snapshots.Values.Where(item => item.Status is not PersistentJobStatus.Completed and not PersistentJobStatus.Failed and not PersistentJobStatus.Stopped).ToArray());
        }
    }

    private sealed class NoOpArtifactMaterializer : IPrecheckArtifactMaterializer
    {
        public Task MaterializeAsync(string projectId, string? runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnusedPageRunner : ITranslationPageRunner
    {
        public Task<TranslationPagePipelineOutcome> RunAsync(ITranslationExecutionSource source, TranslationPageExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedSettingsStore : IApplicationSettingsStore
    {
        public Task<AppSettingsResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new AppSettingsResponse(
                "dark", "zh-CN", [], [], null, null, [], [],
                5, 1));
        public Task<AppSettingsResponse> UpdateAsync(UpdateAppSettingsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsResponse> ImportFontsAsync(ImportFontsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsResponse> RemoveFontAsync(string fontId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
