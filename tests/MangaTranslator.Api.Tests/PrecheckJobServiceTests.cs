using System.Collections.Concurrent;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace MangaTranslator.Api.Tests;

public sealed class PrecheckJobServiceTests
{
    [Fact]
    public async Task LiveExecutionPersistsDynamicEventsAndCompletes()
    {
        var source = new ScriptedExecutionSource(
            async (context, cancellationToken) =>
            {
                await context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Running), cancellationToken);
                await context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Completed), cancellationToken);
                await context.ReportAsync(Event("planning", "planning", ProcessEventStatus.Completed), cancellationToken);
                await context.ReportAsync(Event("batch-002", "batch-analysis", ProcessEventStatus.Running), cancellationToken);
                await context.ReportAsync(Event("batch-001", "batch-analysis", ProcessEventStatus.Running), cancellationToken);
                await context.ReportAsync(Event("batch-001", "batch-analysis", ProcessEventStatus.Completed), cancellationToken);
                await context.ReportAsync(Event("batch-002", "batch-analysis", ProcessEventStatus.Completed), cancellationToken);
                await context.ReportAsync(Event("merge", "merge", ProcessEventStatus.Completed), cancellationToken);
            });
        var store = new InMemoryJobStore();
        var publisher = new RecordingPublisher();
        var service = CreateService(source, store, publisher);

        var started = await service.StartAsync(
            new StartPrecheckRequest("project-1", ExecutionMode.Live),
            CancellationToken.None);
        var completed = await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(100, completed.ProgressPercent);
        Assert.Equal(
            ["manifest", "planning", "batch-002", "batch-001", "merge"],
            completed.Events.Select(item => item.EventId));
        Assert.All(completed.Events, item => Assert.Equal(ProcessEventStatus.Completed, item.Status));
        Assert.Contains(publisher.Snapshots, item => item.Status == JobStatus.Completed);
        Assert.Equal(
            PersistentJobStatus.Completed,
            (await store.GetAsync("project-1", started.JobId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task StopCancelsLiveExecutionAndResumeUsesSameRunId()
    {
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttemptCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runIds = new ConcurrentQueue<string>();
        var attempt = 0;
        var source = new ScriptedExecutionSource(
            async (context, cancellationToken) =>
            {
                runIds.Enqueue(context.RunId);
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    await context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Running), cancellationToken);
                    firstAttemptStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        firstAttemptCancelled.TrySetResult();
                    }

                    return;
                }

                await context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Completed), cancellationToken);
            });
        var store = new InMemoryJobStore();
        var service = CreateService(source, store, new RecordingPublisher());

        var started = await service.StartAsync(
            new StartPrecheckRequest("project-2", ExecutionMode.Live),
            CancellationToken.None);
        await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopped = await service.StopAsync(started.JobId, CancellationToken.None);
        Assert.Equal(JobStatus.Stopped, stopped?.Status);
        await firstAttemptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resumed = await service.ResumeAsync(started.JobId, CancellationToken.None);
        Assert.True(resumed?.Status is JobStatus.Running or JobStatus.Completed);
        var completed = await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(2, attempt);
        Assert.Single(runIds.Distinct(StringComparer.Ordinal));
        Assert.Equal(started.FixtureRunId, runIds.First());
        Assert.Equal(JobStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task LiveExecutionFailureIsPersistedAndPublished()
    {
        var source = new ScriptedExecutionSource(
            async (context, cancellationToken) =>
            {
                await context.ReportAsync(Event("thumbnail", "thumbnail", ProcessEventStatus.Running), cancellationToken);
                throw new InvalidOperationException("provider rejected request");
            });
        var store = new InMemoryJobStore();
        var publisher = new RecordingPublisher();
        var service = CreateService(source, store, publisher);

        var started = await service.StartAsync(
            new StartPrecheckRequest("project-3", ExecutionMode.Live),
            CancellationToken.None);
        var failed = await WaitForStatusAsync(service, started.JobId, JobStatus.Failed);

        var failedEvent = Assert.Single(
            failed.Events,
            item => item.Status == ProcessEventStatus.Failed);
        Assert.Contains("provider rejected request", failedEvent.Detail, StringComparison.Ordinal);
        Assert.Contains(publisher.Snapshots, item => item.Status == JobStatus.Failed);
        Assert.Equal(
            PersistentJobStatus.Failed,
            (await store.GetAsync("project-3", started.JobId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task RetryFailedLiveExecutionKeepsCompletedEventsAndUsesSameRunId()
    {
        var runIds = new ConcurrentQueue<string>();
        var attempt = 0;
        var source = new ScriptedExecutionSource(
            async (context, cancellationToken) =>
            {
                runIds.Enqueue(context.RunId);
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    await context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Completed), cancellationToken);
                    await context.ReportAsync(Event("scan-001", "batch-analysis", ProcessEventStatus.Completed), cancellationToken);
                    await context.ReportAsync(Event("scan-002", "batch-analysis", ProcessEventStatus.Running), cancellationToken);
                    throw new InvalidOperationException("temporary provider failure");
                }

                await context.ReportAsync(Event("scan-002", "batch-analysis", ProcessEventStatus.Completed), cancellationToken);
            });
        var store = new InMemoryJobStore();
        var service = CreateService(source, store, new RecordingPublisher());

        var started = await service.StartAsync(
            new StartPrecheckRequest("project-retry", ExecutionMode.Live, "retry-run"),
            CancellationToken.None);
        var failed = await WaitForStatusAsync(service, started.JobId, JobStatus.Failed);
        Assert.Equal(ProcessEventStatus.Completed,
            Assert.Single(failed.Events, item => item.EventId == "scan-001").Status);
        Assert.Equal(ProcessEventStatus.Failed,
            Assert.Single(failed.Events, item => item.EventId == "scan-002").Status);

        var retried = await service.RetryAsync(started.JobId, CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal(ProcessEventStatus.Completed,
            Assert.Single(retried!.Events, item => item.EventId == "scan-001").Status);
        Assert.DoesNotContain(retried.Events, item => item.Status == ProcessEventStatus.Failed);

        var completed = await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);
        Assert.Equal(2, attempt);
        Assert.Single(runIds.Distinct(StringComparer.Ordinal));
        Assert.Equal("retry-run", completed.FixtureRunId);
        Assert.Equal(ProcessEventStatus.Completed,
            Assert.Single(completed.Events, item => item.EventId == "scan-002").Status);
    }

    [Fact]
    public async Task PersistedFailedPrecheckCanRetryAfterServiceRestart()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "precheck-failed",
                "project-failed",
                "Precheck",
                PersistentJobStatus.Failed,
                ExecutionMode.Live.ToString(),
                "precheck-run-failed",
                50,
                [
                    new PersistentProcessEvent(
                        "scan-001",
                        "batch-analysis",
                        "Scan 1",
                        "Inspect pages.",
                        "Completed.",
                        null,
                        PersistentProcessEventStatus.Completed,
                        "batch-analysis",
                        [],
                        now),
                    new PersistentProcessEvent(
                        "scan-002",
                        "batch-analysis",
                        "Scan 2",
                        "Inspect pages.",
                        "Failed.",
                        "temporary failure",
                        PersistentProcessEventStatus.Failed,
                        "batch-analysis",
                        [],
                        now)
                ],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new ScriptedExecutionSource((context, cancellationToken) =>
                context.ReportAsync(Event("scan-002", "batch-analysis", ProcessEventStatus.Completed), cancellationToken)),
            store,
            new RecordingPublisher());

        var retried = await service.RetryAsync("precheck-failed", CancellationToken.None);
        var completed = await WaitForStatusAsync(service, "precheck-failed", JobStatus.Completed);

        Assert.NotNull(retried);
        Assert.Equal("precheck-run-failed", completed.FixtureRunId);
        Assert.Equal(ProcessEventStatus.Completed,
            Assert.Single(completed.Events, item => item.EventId == "scan-001").Status);
        Assert.Equal(PersistentJobStatus.Completed,
            (await store.FindAsync("precheck-failed", CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task RestoredRunningJobIsPersistedAsStoppedBeforeItCanBeResumed()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "interrupted-job",
                "project-4",
                "Precheck",
                PersistentJobStatus.Running,
                ExecutionMode.Live.ToString(),
                "live-run-interrupted",
                0,
                [
                    new PersistentProcessEvent(
                        "manifest",
                        "manifest",
                        "生成 Book Manifest",
                        "扫描页面。",
                        null,
                        null,
                        PersistentProcessEventStatus.Running,
                        null,
                        [],
                        now)
                ],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new ScriptedExecutionSource((_, _) => Task.CompletedTask),
            store,
            new RecordingPublisher());

        await service.StartAsync(CancellationToken.None);
        try
        {
            var restored = await WaitForStatusAsync(service, "interrupted-job", JobStatus.Stopped);
            var persisted = await store.GetAsync("project-4", "interrupted-job", CancellationToken.None);

            Assert.Equal("live-run-interrupted", restored.FixtureRunId);
            Assert.Equal(PersistentJobStatus.Stopped, persisted?.Status);
            Assert.Equal(PersistentProcessEventStatus.Pending, Assert.Single(persisted!.Events).Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PersistedStoppedPrecheckCanResumeAfterServiceRestartWithoutPriorListing()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "precheck-stopped",
                "project-5",
                "Precheck",
                PersistentJobStatus.Stopped,
                ExecutionMode.Live.ToString(),
                "precheck-run-stopped",
                0,
                [
                    new PersistentProcessEvent(
                        "manifest",
                        "manifest",
                        "Build manifest",
                        "Inspect pages.",
                        "Stopped by user.",
                        null,
                        PersistentProcessEventStatus.Pending,
                        null,
                        [],
                        now)
                ],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new ScriptedExecutionSource((context, cancellationToken) =>
                context.ReportAsync(Event("manifest", "manifest", ProcessEventStatus.Completed), cancellationToken)),
            store,
            new RecordingPublisher());

        var resumed = await service.ResumeAsync("precheck-stopped", CancellationToken.None);
        var completed = await WaitForStatusAsync(service, "precheck-stopped", JobStatus.Completed);

        Assert.NotNull(resumed);
        Assert.Equal("precheck-run-stopped", completed.FixtureRunId);
        Assert.Equal(PersistentJobStatus.Completed,
            (await store.FindAsync("precheck-stopped", CancellationToken.None))?.Status);
    }

    private static PrecheckJobService CreateService(
        IPrecheckExecutionSource source,
        IJobStore store,
        IJobUpdatePublisher publisher) =>
        new(
            publisher,
            [source],
            store,
            new NoOpArtifactMaterializer(),
            NullLogger<PrecheckJobService>.Instance);

    private static async Task<JobSnapshotResponse> WaitForStatusAsync(
        PrecheckJobService service,
        string jobId,
        JobStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (service.TryGet(jobId, out var snapshot) && snapshot?.Status == expected)
            {
                return snapshot;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Job {jobId} did not reach {expected}.");
    }

    private static PrecheckExecutionEvent Event(
        string eventId,
        string stage,
        ProcessEventStatus status) =>
        new(
            eventId,
            stage,
            eventId,
            $"request:{eventId}",
            $"response:{eventId}",
            $"detail:{eventId}",
            stage == "batch-analysis" ? "batch-analysis" : null,
            [],
            status);

    private sealed class ScriptedExecutionSource(
        Func<PrecheckExecutionContext, CancellationToken, Task> execute) : IPrecheckExecutionSource
    {
        public ExecutionMode Mode => ExecutionMode.Live;
        public bool DrivesExecution => true;

        public Task<PrecheckExecutionPlan> CreateAsync(
            string projectId,
            string? fixtureRunId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PrecheckExecutionPlan(
                ExecutionMode.Live,
                fixtureRunId ?? "live-run-1",
                [
                    Event("manifest", "manifest", ProcessEventStatus.Pending),
                    Event("planning", "planning", ProcessEventStatus.Pending),
                    Event("merge", "merge", ProcessEventStatus.Pending)
                ]));

        public Task ExecuteAsync(
            PrecheckExecutionContext context,
            CancellationToken cancellationToken) => execute(context, cancellationToken);
    }

    private sealed class NoOpArtifactMaterializer : IPrecheckArtifactMaterializer
    {
        public Task MaterializeAsync(
            string projectId,
            string? runId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPublisher : IJobUpdatePublisher
    {
        public ConcurrentQueue<JobSnapshotResponse> Snapshots { get; } = new();

        public Task PublishAsync(
            JobSnapshotResponse snapshot,
            CancellationToken cancellationToken)
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
            lock (gate)
            {
                snapshots[snapshot.JobId] = snapshot;
            }

            return Task.CompletedTask;
        }

        public Task<PersistentJobSnapshot?> FindAsync(
            string jobId,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                snapshots.TryGetValue(jobId, out var snapshot);
                return Task.FromResult(snapshot);
            }
        }

        public Task<PersistentJobSnapshot?> GetAsync(
            string projectId,
            string jobId,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                snapshots.TryGetValue(jobId, out var snapshot);
                return Task.FromResult(snapshot);
            }
        }

        public Task<IReadOnlyList<PersistentJobSnapshot>> ListForProjectAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<PersistentJobSnapshot>>(
                    snapshots.Values.Where(item => item.ProjectId == projectId).ToArray());
            }
        }

        public Task<IReadOnlyList<PersistentJobSnapshot>> ListActiveAsync(
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<PersistentJobSnapshot>>(
                    snapshots.Values.Where(item => item.Status is not PersistentJobStatus.Completed and not PersistentJobStatus.Failed).ToArray());
            }
        }
    }
}
