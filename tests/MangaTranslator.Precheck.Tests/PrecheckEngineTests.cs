using System.Text.Json;
using MangaTranslator.Precheck;

namespace MangaTranslator.Precheck.Tests;

public sealed class PrecheckEngineTests
{
    [Fact]
    public async Task RunAsyncReturnsAllArtifactsWithoutRequiringPersistence()
    {
        var pages = new[]
        {
            CreatePage("p0001", 1),
            CreatePage("p0003", 3)
        };
        var thumbnailAnalyzer = new StubThumbnailAnalyzer(pages);
        var batchAnalyzer = new StubBatchAnalyzer();
        var merger = new StubMerger();
        var reviewer = new StubAutomaticReviewProcessor();
        var semanticConsolidator = new StubGlobalSemanticConsolidator();
        var engine = new PrecheckEngine(
            thumbnailAnalyzer,
            new DynamicBatchPlanner(),
            batchAnalyzer,
            merger,
            reviewer,
            semanticConsolidator);
        var request = new PrecheckRequest(
            "book-test",
            pages,
            new ThrowingPageAssetProvider(),
            new PrecheckOptions
            {
                RunId = "run-test",
                MaxConcurrentBatches = 1
            });

        var result = await engine.RunAsync(request);

        Assert.Equal(PrecheckStatus.Completed, result.Status);
        Assert.Equal("run-test", result.RunId);
        Assert.Single(result.BatchPlan.Batches);
        Assert.Single(result.BatchObservations);
        var manifestIssue = Assert.Single(result.SourceManifest.Issues);
        Assert.Equal("missing_source_index", manifestIssue.Code);
        Assert.Equal(2, manifestIssue.SourceIndex);
        Assert.Equal(1, thumbnailAnalyzer.CallCount);
        Assert.Equal(1, batchAnalyzer.CallCount);
        Assert.Equal(1, reviewer.CallCount);
        Assert.Equal(1, semanticConsolidator.CallCount);
        Assert.Equal("zh-Hans", result.SemanticMerge.TargetLanguage);
        Assert.Equal(2, result.BookManifest.Pages.Count);

        TestArtifactWriter.WriteJson(
            nameof(RunAsyncReturnsAllArtifactsWithoutRequiringPersistence),
            "orchestration/precheck_result.json",
            result);
        TestArtifactWriter.WriteJson(
            nameof(RunAsyncReturnsAllArtifactsWithoutRequiringPersistence),
            "orchestration/book_manifest.json",
            result.BookManifest);
    }

    [Fact]
    public async Task RunAsyncHonorsConfiguredFiveBatchConcurrency()
    {
        var pages = Enumerable.Range(1, 10)
            .Select(index => CreatePage($"p{index:0000}", index))
            .ToArray();
        var analyzer = new ConcurrencyTrackingBatchAnalyzer();
        var engine = new PrecheckEngine(
            new StubThumbnailAnalyzer(pages),
            new DynamicBatchPlanner(),
            analyzer,
            new StubMerger(),
            new StubAutomaticReviewProcessor(),
            new StubGlobalSemanticConsolidator());
        var request = new PrecheckRequest(
            "book-concurrency",
            pages,
            new ThrowingPageAssetProvider(),
            new PrecheckOptions
            {
                RunId = "run-concurrency",
                MaxBatchPageCount = 1,
                MaxBatchCost = 100,
                ContextPagesPerSide = 0,
                MaxConcurrentBatches = 5
            });

        var result = await engine.RunAsync(request);

        Assert.Equal(10, result.BatchPlan.Batches.Count);
        Assert.Equal(10, analyzer.CallCount);
        Assert.Equal(5, analyzer.MaximumObservedConcurrency);
        TestArtifactWriter.WriteJson(
            nameof(RunAsyncHonorsConfiguredFiveBatchConcurrency),
            "concurrency/batch_plan.json",
            result.BatchPlan);
        TestArtifactWriter.WriteJson(
            nameof(RunAsyncHonorsConfiguredFiveBatchConcurrency),
            "concurrency/summary.json",
            new
            {
                configuredConcurrency = 5,
                analyzer.MaximumObservedConcurrency,
                analyzer.CallCount
            });
    }

    [Fact]
    public async Task RunAsyncResumesAfterAutomaticReviewWithoutRepeatingCompletedStages()
    {
        var pages = new[]
        {
            CreatePage("p0001", 1),
            CreatePage("p0002", 2)
        };
        var firstEngine = new PrecheckEngine(
            new StubThumbnailAnalyzer(pages),
            new DynamicBatchPlanner(),
            new StubBatchAnalyzer(),
            new StubMerger(),
            new StubAutomaticReviewProcessor(),
            new FailingGlobalSemanticConsolidator());
        var request = new PrecheckRequest(
            "book-resume",
            pages,
            new ThrowingPageAssetProvider(),
            new PrecheckOptions
            {
                RunId = "run-resume",
                MaxConcurrentBatches = 1
            });
        PrecheckCheckpoint? checkpoint = null;

        await Assert.ThrowsAsync<PrecheckAnalysisException>(async () =>
        {
            await foreach (var @event in firstEngine.RunStreamingAsync(request))
            {
                if (@event is PrecheckCheckpointReady ready)
                {
                    checkpoint = ready.Checkpoint;
                }
            }
        });

        Assert.NotNull(checkpoint);
        Assert.Equal("automatic_review", checkpoint.CompletedStage);
        var thumbnail = new StubThumbnailAnalyzer(pages);
        var batch = new StubBatchAnalyzer();
        var merger = new StubMerger();
        var reviewer = new StubAutomaticReviewProcessor();
        var semantic = new StubGlobalSemanticConsolidator();
        var resumedEngine = new PrecheckEngine(
            thumbnail,
            new DynamicBatchPlanner(),
            batch,
            merger,
            reviewer,
            semantic);

        var result = await resumedEngine.RunAsync(request with { ResumeFrom = checkpoint });

        Assert.Equal(PrecheckStatus.Completed, result.Status);
        Assert.Equal(0, thumbnail.CallCount);
        Assert.Equal(0, batch.CallCount);
        Assert.Equal(0, merger.CallCount);
        Assert.Equal(0, reviewer.CallCount);
        Assert.Equal(1, semantic.CallCount);
        TestArtifactWriter.WriteJson(
            nameof(RunAsyncResumesAfterAutomaticReviewWithoutRepeatingCompletedStages),
            "resume/checkpoint.json",
            checkpoint);
        TestArtifactWriter.WriteJson(
            nameof(RunAsyncResumesAfterAutomaticReviewWithoutRepeatingCompletedStages),
            "resume/result.json",
            result);
    }

    private static PageDescriptor CreatePage(string pageId, int index)
    {
        return new PageDescriptor(
            pageId,
            index,
            $"{index:000}.png",
            1000,
            1400,
            "image/png",
            $"hash-{index}");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ThrowingPageAssetProvider : IPageAssetProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
            string pageId,
            ImageQuality quality,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The orchestration test must not load page bytes.");
        }
    }

    private sealed class StubThumbnailAnalyzer(IReadOnlyList<PageDescriptor> pages)
        : IThumbnailPrecheckAnalyzer
    {
        public int CallCount { get; private set; }

        public Task<ThumbnailPrecheckDocument> AnalyzeAsync(
            PrecheckRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var observations = pages.Select(page => new PageThumbnailObservation(
                page.PageId,
                "story",
                null,
                null,
                1,
                0,
                0,
                0,
                false,
                0.95,
                [])).ToArray();

            return Task.FromResult(new ThumbnailPrecheckDocument(
                "1.0.0",
                new GlobalObservations("ja", "rtl", 0.95, []),
                observations,
                []));
        }
    }

    private sealed class StubBatchAnalyzer : IBatchAnalyzer
    {
        public int CallCount { get; private set; }

        public Task<BatchObservationDocument> AnalyzeAsync(
            PrecheckRequest request,
            BatchPlan batch,
            ThumbnailPrecheckDocument thumbnailPrecheck,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BatchObservationDocument(
                "1.0.0",
                batch.BatchId,
                batch.OwnerPages,
                Json("[]"),
                Json("{}"),
                Json("[]"),
                Json("[]"),
                Json("[]"),
                []));
        }
    }

    private sealed class ConcurrencyTrackingBatchAnalyzer : IBatchAnalyzer
    {
        private int _callCount;
        private int _currentConcurrency;
        private int _maximumObservedConcurrency;

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

        public async Task<BatchObservationDocument> AnalyzeAsync(
            PrecheckRequest request,
            BatchPlan batch,
            ThumbnailPrecheckDocument thumbnailPrecheck,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var current = Interlocked.Increment(ref _currentConcurrency);
            UpdateMaximum(current);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new BatchObservationDocument(
                    "1.0.0",
                    batch.BatchId,
                    batch.OwnerPages,
                    Json("[]"),
                    Json("[]"),
                    Json("{}"),
                    Json("[]"),
                    Json("[]"),
                    []);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrency);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var currentMaximum = Volatile.Read(ref _maximumObservedConcurrency);
                if (candidate <= currentMaximum)
                {
                    return;
                }

                if (Interlocked.CompareExchange(
                    ref _maximumObservedConcurrency,
                    candidate,
                    currentMaximum) == currentMaximum)
                {
                    return;
                }
            }
        }
    }

    private sealed class StubMerger : IPrecheckMerger
    {
        public int CallCount { get; private set; }

        public PrecheckMergeResult Merge(
            PrecheckRequest request,
            ThumbnailPrecheckDocument thumbnailPrecheck,
            BatchPlanDocument batchPlan,
            IReadOnlyList<BatchObservationDocument> observations)
        {
            CallCount++;
            return new PrecheckMergeResult(
                Json("{}"),
                Json("{}"),
                Json("{}"),
                Json("{}"),
                Json("{}"),
                Json("{}"),
                []);
        }
    }

    private sealed class StubAutomaticReviewProcessor : IAutomaticReviewProcessor
    {
        public int CallCount { get; private set; }

        public Task<AutoReviewResult> ReviewAsync(
            PrecheckRequest request,
            PrecheckMergeResult merge,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AutoReviewResult(merge, [], [], []));
        }
    }

    private sealed class StubGlobalSemanticConsolidator : IGlobalSemanticConsolidator
    {
        public int CallCount { get; private set; }

        public Task<GlobalSemanticMergeResult> ConsolidateAsync(
            PrecheckRequest request,
            ThumbnailPrecheckDocument thumbnailPrecheck,
            BatchPlanDocument batchPlan,
            IReadOnlyList<BatchObservationDocument> observations,
            AutoReviewResult automaticReview,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new GlobalSemanticMergeResult(
                request.Options.TargetLanguage,
                Json("[]"),
                Json("[]"),
                Json("[]"),
                Json("[]"),
                Json("{}")));
        }
    }

    private sealed class FailingGlobalSemanticConsolidator : IGlobalSemanticConsolidator
    {
        public Task<GlobalSemanticMergeResult> ConsolidateAsync(
            PrecheckRequest request,
            ThumbnailPrecheckDocument thumbnailPrecheck,
            BatchPlanDocument batchPlan,
            IReadOnlyList<BatchObservationDocument> observations,
            AutoReviewResult automaticReview,
            CancellationToken cancellationToken = default)
        {
            throw new PrecheckAnalysisException("Synthetic semantic failure after checkpoint.");
        }
    }
}
