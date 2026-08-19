using System.Collections.Concurrent;
using System.Text.Json;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace MangaTranslator.Api.Tests;

public sealed class TranslationJobServiceTests
{
    [Fact]
    public async Task LiveExecutionPublishesPageProgressAndCompletes()
    {
        var source = new ScriptedSource(
            async (context, cancellationToken) =>
            {
                await context.ReportAsync("正在检测", "regions: 4", cancellationToken);
                await context.ReportAsync("正在嵌字", "segments: 3", cancellationToken);
                return Result(context.PageIndex);
            },
            [Page(1), Page(2)]);
        var store = new InMemoryJobStore();
        var publisher = new RecordingPublisher();
        var service = CreateService(source, store, publisher);

        var started = await service.StartAsync(
            new StartTranslationRequest("project-1", ExecutionMode.Live),
            CancellationToken.None);
        var completed = await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(100, completed.ProgressPercent);
        Assert.Equal([1, 2], completed.Events.Select(item => Assert.Single(item.PageIndices)));
        Assert.All(completed.Events, item => Assert.Equal(ProcessEventStatus.Completed, item.Status));
        Assert.Contains(publisher.Snapshots, item =>
            item.Events.Any(@event => @event.ResponseSummary == "正在检测"));
        Assert.Equal(
            PersistentJobStatus.Completed,
            (await store.GetAsync("project-1", started.JobId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task TranslationRespectsConfiguredPageConcurrency()
    {
        const int configuredConcurrency = 3;
        var active = 0;
        var maximumActive = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(
            async (context, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var observed = Volatile.Read(ref maximumActive);
                    if (current <= observed || Interlocked.CompareExchange(ref maximumActive, current, observed) == observed) break;
                }
                if (current == configuredConcurrency) allWorkersStarted.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return Result(context.PageIndex);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            Enumerable.Range(1, 7).Select(Page).ToArray());
        var service = CreateService(source, new InMemoryJobStore(), new RecordingPublisher(), configuredConcurrency);

        var started = await service.StartAsync(
            new StartTranslationRequest("project-concurrency", ExecutionMode.Live),
            CancellationToken.None);
        await allWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(configuredConcurrency, Volatile.Read(ref maximumActive));
        Assert.Equal(configuredConcurrency, Volatile.Read(ref active));

        release.TrySetResult();
        await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);
        Assert.Equal(configuredConcurrency, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public async Task BatchSizeGroupsSevenPagesIntoThreeModelRequests()
    {
        var batches = new ConcurrentQueue<int[]>();
        var source = new BatchScriptedSource(
            (contexts, _) =>
            {
                batches.Enqueue(contexts.Select(item => item.PageIndex).ToArray());
                return Task.FromResult<IReadOnlyList<TranslationBatchExecutionResult>>(
                    contexts.Select(context => new TranslationBatchExecutionResult(
                        context.PageIndex,
                        Result(context.PageIndex))).ToArray());
            },
            Enumerable.Range(1, 7).Select(Page).ToArray(),
            new TranslationTaskSettingsResponse(1, 3, TranslationInputResolution.Half));
        var service = CreateService(source, new InMemoryJobStore(), new RecordingPublisher());

        var started = await service.StartAsync(
            new StartTranslationRequest("project-batches", ExecutionMode.Live),
            CancellationToken.None);
        await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(3, batches.Count);
        Assert.Equal([3, 3, 1], batches.Select(item => item.Length).ToArray());
        Assert.Equal(Enumerable.Range(1, 7), batches.SelectMany(item => item));
        Assert.Equal(3, started.TranslationSettings?.BatchSize);
        Assert.Equal(TranslationInputResolution.Original, started.TranslationSettings?.InputResolution);
    }

    [Fact]
    public async Task BatchConcurrencyLimitsConcurrentModelRequests()
    {
        const int configuredConcurrency = 2;
        var active = 0;
        var maximumActive = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BatchScriptedSource(
            async (contexts, _) =>
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var observed = Volatile.Read(ref maximumActive);
                    if (current <= observed || Interlocked.CompareExchange(ref maximumActive, current, observed) == observed) break;
                }
                if (current == configuredConcurrency) workersStarted.TrySetResult();
                try
                {
                    await release.Task;
                    return contexts.Select(context => new TranslationBatchExecutionResult(
                        context.PageIndex,
                        Result(context.PageIndex))).ToArray();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            Enumerable.Range(1, 8).Select(Page).ToArray(),
            new TranslationTaskSettingsResponse(configuredConcurrency, 2, TranslationInputResolution.Quarter));
        var service = CreateService(source, new InMemoryJobStore(), new RecordingPublisher());

        var started = await service.StartAsync(
            new StartTranslationRequest("project-batch-concurrency", ExecutionMode.Live),
            CancellationToken.None);
        await workersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(configuredConcurrency, Volatile.Read(ref maximumActive));
        Assert.Equal(configuredConcurrency, Volatile.Read(ref active));

        release.TrySetResult();
        await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);
        Assert.Equal(configuredConcurrency, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public async Task StopCancelsActivePageAndResumeUsesSameRunId()
    {
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttemptCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runIds = new ConcurrentQueue<string>();
        var attempt = 0;
        var source = new ScriptedSource(
            async (context, cancellationToken) =>
            {
                runIds.Enqueue(context.RunId);
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    firstAttemptStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        firstAttemptCancelled.TrySetResult();
                    }
                }

                return Result(context.PageIndex);
            },
            [Page(8)]);
        var store = new InMemoryJobStore();
        var service = CreateService(source, store, new RecordingPublisher());

        var started = await service.StartAsync(
            new StartTranslationRequest("project-2", ExecutionMode.Live),
            CancellationToken.None);
        await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await service.StopAsync(started.JobId, CancellationToken.None);

        Assert.Equal(JobStatus.Stopped, stopped?.Status);
        Assert.Equal(ProcessEventStatus.Pending, Assert.Single(stopped!.Events).Status);
        await firstAttemptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resumed = await service.ResumeAsync(started.JobId, CancellationToken.None);
        Assert.True(resumed?.Status is JobStatus.Running or JobStatus.Completed);
        await WaitForStatusAsync(service, started.JobId, JobStatus.Completed);

        Assert.Equal(2, attempt);
        Assert.Single(runIds.Distinct(StringComparer.Ordinal));
        Assert.Equal(started.FixtureRunId, runIds.First());
    }

    [Fact]
    public async Task PageFailureIsPersistedAndPublished()
    {
        var source = new ScriptedSource(
            (_, _) => throw new InvalidOperationException("model rejected page"),
            [Page(3)]);
        var store = new InMemoryJobStore();
        var publisher = new RecordingPublisher();
        var service = CreateService(source, store, publisher);

        var started = await service.StartAsync(
            new StartTranslationRequest("project-3", ExecutionMode.Live),
            CancellationToken.None);
        var failed = await WaitForStatusAsync(service, started.JobId, JobStatus.Failed);

        Assert.Contains("model rejected page", Assert.Single(failed.Events).Detail, StringComparison.Ordinal);
        Assert.Contains(publisher.Snapshots, item => item.Status == JobStatus.Failed);
        Assert.Equal(
            PersistentJobStatus.Failed,
            (await store.GetAsync("project-3", started.JobId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task FailedBatchDoesNotCancelOtherParallelBatches()
    {
        var secondBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BatchScriptedSource(
            async (contexts, cancellationToken) =>
            {
                var pageIndex = Assert.Single(contexts).PageIndex;
                if (pageIndex == 1)
                {
                    await secondBatchStarted.Task.WaitAsync(cancellationToken);
                    throw new InvalidOperationException("first batch failed");
                }

                if (pageIndex == 2)
                {
                    secondBatchStarted.TrySetResult();
                    await Task.Delay(100, cancellationToken);
                }

                return [new TranslationBatchExecutionResult(pageIndex, Result(pageIndex))];
            },
            [Page(1), Page(2), Page(3)],
            new TranslationTaskSettingsResponse(2, 1, TranslationInputResolution.Original));
        var service = CreateService(source, new InMemoryJobStore(), new RecordingPublisher());

        var started = await service.StartAsync(
            new StartTranslationRequest("project-isolated-parallel-failure", ExecutionMode.Live),
            CancellationToken.None);
        var failed = await WaitForStatusAsync(service, started.JobId, JobStatus.Failed);

        Assert.Equal(ProcessEventStatus.Failed, failed.Events.Single(item => Assert.Single(item.PageIndices) == 1).Status);
        Assert.Equal(ProcessEventStatus.Completed, failed.Events.Single(item => Assert.Single(item.PageIndices) == 2).Status);
        Assert.Equal(ProcessEventStatus.Completed, failed.Events.Single(item => Assert.Single(item.PageIndices) == 3).Status);
    }

    [Fact]
    public async Task WorkerContinuesWithLaterBatchAfterOneBatchFails()
    {
        var attempts = new ConcurrentQueue<int>();
        var source = new BatchScriptedSource(
            (contexts, _) =>
            {
                var pageIndex = Assert.Single(contexts).PageIndex;
                attempts.Enqueue(pageIndex);
                if (pageIndex == 1)
                {
                    throw new InvalidOperationException("first batch failed");
                }

                return Task.FromResult<IReadOnlyList<TranslationBatchExecutionResult>>(
                    [new TranslationBatchExecutionResult(pageIndex, Result(pageIndex))]);
            },
            [Page(1), Page(2)],
            new TranslationTaskSettingsResponse(1, 1, TranslationInputResolution.Original));
        var store = new InMemoryJobStore();
        var service = CreateService(source, store, new RecordingPublisher());

        var started = await service.StartAsync(
            new StartTranslationRequest("project-continues-after-failure", ExecutionMode.Live),
            CancellationToken.None);
        var failed = await WaitForStatusAsync(service, started.JobId, JobStatus.Failed);

        Assert.Equal([1, 2], attempts.ToArray());
        Assert.Equal(ProcessEventStatus.Failed, failed.Events.Single(item => Assert.Single(item.PageIndices) == 1).Status);
        Assert.Equal(ProcessEventStatus.Completed, failed.Events.Single(item => Assert.Single(item.PageIndices) == 2).Status);
        var persisted = await store.GetAsync(
            "project-continues-after-failure",
            started.JobId,
            CancellationToken.None);
        Assert.Equal(PersistentProcessEventStatus.Failed, persisted!.Events.Single(item => Assert.Single(item.PageIndices) == 1).Status);
        Assert.Equal(PersistentProcessEventStatus.Completed, persisted.Events.Single(item => Assert.Single(item.PageIndices) == 2).Status);
    }

    [Fact]
    public async Task RestoredRunningTranslationIsPersistedAsStopped()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "translation-interrupted",
                "project-4",
                "Translation",
                PersistentJobStatus.Running,
                ExecutionMode.Live.ToString(),
                "live-translation-existing",
                0,
                [
                    new PersistentProcessEvent(
                        "page-pipeline-0008",
                        "page-pipeline",
                        "处理第 8 页",
                        "执行页面流水线。",
                        null,
                        null,
                        PersistentProcessEventStatus.Running,
                        null,
                        [8],
                        now)
                ],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new ScriptedSource((context, _) => Task.FromResult(Result(context.PageIndex)), [Page(8)]),
            store,
            new RecordingPublisher());

        await service.StartAsync(CancellationToken.None);
        try
        {
            var restored = await WaitForStatusAsync(
                service,
                "translation-interrupted",
                JobStatus.Stopped);
            var persisted = await store.GetAsync(
                "project-4",
                "translation-interrupted",
                CancellationToken.None);

            Assert.Equal("live-translation-existing", restored.FixtureRunId);
            Assert.Equal(PersistentJobStatus.Stopped, persisted?.Status);
            Assert.Equal(PersistentProcessEventStatus.Pending, Assert.Single(persisted!.Events).Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PersistedStoppedTranslationCanResumeAfterServiceRestartWithoutPriorListing()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "translation-stopped",
                "project-5",
                "Translation",
                PersistentJobStatus.Stopped,
                ExecutionMode.Live.ToString(),
                "live-run-stopped",
                0,
                [
                    new PersistentProcessEvent(
                        "page-pipeline-0012",
                        "page-pipeline",
                        "Process page 12",
                        "Run page pipeline.",
                        "Stopped by user.",
                        null,
                        PersistentProcessEventStatus.Pending,
                        null,
                        [12],
                        now)
                ],
                now,
                now),
            CancellationToken.None);
        var service = CreateService(
            new ScriptedSource((context, _) => Task.FromResult(Result(context.PageIndex)), [Page(12)]),
            store,
            new RecordingPublisher());

        var resumed = await service.ResumeAsync("translation-stopped", CancellationToken.None);
        var completed = await WaitForStatusAsync(service, "translation-stopped", JobStatus.Completed);

        Assert.NotNull(resumed);
        Assert.Equal("live-run-stopped", completed.FixtureRunId);
        Assert.Equal(PersistentJobStatus.Completed,
            (await store.FindAsync("translation-stopped", CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task PersistedStoppedTranslationResumesWithOriginalResolutionAndItsBatchSettingsSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new TranslationTaskSettingsResponse(
            1,
            4,
            TranslationInputResolution.Quarter);
        var store = new InMemoryJobStore();
        await store.SaveAsync(
            new PersistentJobSnapshot(
                "translation-settings-snapshot",
                "project-settings-snapshot",
                "Translation",
                PersistentJobStatus.Stopped,
                ExecutionMode.Live.ToString(),
                "live-run-settings-snapshot",
                0,
                [
                    new PersistentProcessEvent(
                        "page-pipeline-0014",
                        "page-pipeline",
                        "Process page 14",
                        "Run page pipeline.",
                        "Stopped by user.",
                        null,
                        PersistentProcessEventStatus.Pending,
                        null,
                        [14],
                        now)
                ],
                now,
                now,
                JsonSerializer.Serialize(settings)),
            CancellationToken.None);
        var source = new BatchScriptedSource(
            (contexts, _) => Task.FromResult<IReadOnlyList<TranslationBatchExecutionResult>>(
                contexts.Select(context => new TranslationBatchExecutionResult(
                    context.PageIndex,
                    Result(context.PageIndex))).ToArray()),
            [Page(14)],
            settings);
        var service = CreateService(source, store, new RecordingPublisher());

        var resumed = await service.ResumeAsync(
            "translation-settings-snapshot",
            CancellationToken.None);
        var completed = await WaitForStatusAsync(
            service,
            "translation-settings-snapshot",
            JobStatus.Completed);

        Assert.NotNull(resumed);
        var normalizedSettings = settings with { InputResolution = TranslationInputResolution.Original };
        Assert.Equal(normalizedSettings, completed.TranslationSettings);
        Assert.Equal(
            JsonSerializer.Serialize(normalizedSettings),
            (await store.FindAsync("translation-settings-snapshot", CancellationToken.None))?.SettingsJson);
    }

    private static TranslationJobService CreateService(
        ITranslationExecutionSource source,
        IJobStore store,
        IJobUpdatePublisher publisher,
        int concurrency = 5) =>
        new(
            publisher,
            [source],
            new DelegatingPageRunner(),
            store,
            new FixedSettingsStore(concurrency),
            NullLogger<TranslationJobService>.Instance);

    private sealed class FixedSettingsStore(int concurrency = 5) : IApplicationSettingsStore
    {
        public Task<AppSettingsResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new AppSettingsResponse(
                "dark", "zh-CN", [], [], null, null, [], [],
                concurrency, 1));

        public Task<AppSettingsResponse> UpdateAsync(UpdateAppSettingsRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppSettingsResponse> ImportFontsAsync(ImportFontsRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppSettingsResponse> RemoveFontAsync(string fontId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task<JobSnapshotResponse> WaitForStatusAsync(
        TranslationJobService service,
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

    private static ProjectPageSnapshot Page(int index) => new(
        $"page-{index:0000}",
        index,
        $"page_{index:0000}.png",
        100,
        140,
        "story",
        true,
        MangaTranslator.ProjectSystem.PageReviewTag.Unreviewed,
        $"source/{index:0000}.png",
        false,
        false,
        1,
        $"version-{index}",
        $"version-{index}",
        $"background-version-{index}",
        1);

    private static TranslationPageExecutionResult Result(int pageIndex) => new(
        "{}",
        $"clean/page-{pageIndex:0000}.png",
        $"mask/page-{pageIndex:0000}.png",
        $"rendered/page-{pageIndex:0000}.png",
        $"第 {pageIndex} 页完成。",
        "done");

    private sealed class ScriptedSource(
        Func<TranslationPageExecutionContext, CancellationToken, Task<TranslationPageExecutionResult>> execute,
        IReadOnlyList<ProjectPageSnapshot> pages) : ITranslationExecutionSource
    {
        public ExecutionMode Mode => ExecutionMode.Live;

        public Task<TranslationExecutionPlan> CreateAsync(
            StartTranslationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TranslationExecutionPlan(
                Mode,
                request.FixtureRunId ?? "live-translation-run",
                pages,
                1));

        public Task<TranslationPageExecutionResult> ExecutePageAsync(
            TranslationPageExecutionContext context,
            CancellationToken cancellationToken) => execute(context, cancellationToken);
    }

    private sealed class BatchScriptedSource(
        Func<IReadOnlyList<TranslationPageExecutionContext>, CancellationToken, Task<IReadOnlyList<TranslationBatchExecutionResult>>> execute,
        IReadOnlyList<ProjectPageSnapshot> pages,
        TranslationTaskSettingsResponse settings) : ITranslationExecutionSource
    {
        public ExecutionMode Mode => ExecutionMode.Live;

        public Task<TranslationExecutionPlan> CreateAsync(
            StartTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new TranslationExecutionPlan(
                Mode,
                request.FixtureRunId ?? "live-batch-run",
                pages,
                0,
                settings));

        public Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
            IReadOnlyList<TranslationPageExecutionContext> contexts,
            TranslationTaskSettingsResponse taskSettings,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                settings with { InputResolution = TranslationInputResolution.Original },
                taskSettings);
            return execute(contexts, cancellationToken);
        }
    }

    private sealed class DelegatingPageRunner : ITranslationPageRunner
    {
        public async Task<TranslationPagePipelineOutcome> RunAsync(
            ITranslationExecutionSource source,
            TranslationPageExecutionContext context,
            CancellationToken cancellationToken)
        {
            var result = await source.ExecutePageAsync(context, cancellationToken);
            return new TranslationPagePipelineOutcome(result.ResponseSummary, result.Detail);
        }
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
                    snapshots.Values.Where(item =>
                        item.Status is not PersistentJobStatus.Completed
                            and not PersistentJobStatus.Failed).ToArray());
            }
        }
    }
}
