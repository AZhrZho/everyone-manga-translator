using System.Text.Json;
using MangaTranslator.Precheck;

namespace MangaTranslator.Precheck.Tests;

public sealed class DynamicBatchPlannerTests
{
    [Fact]
    public void PlanKeepsHardRelationsInOneBatchAndAddsContextPages()
    {
        var pages = Enumerable.Range(1, 5)
            .Select(index => new PageDescriptor(
                $"p{index:0000}",
                index,
                $"{index:000}.png",
                1000,
                1400,
                "image/png",
                $"hash-{index}"))
            .ToArray();

        var request = new PrecheckRequest(
            "book-test",
            pages,
            new NullPageAssetProvider(),
            new PrecheckOptions
            {
                MaxBatchPageCount = 3,
                MaxBatchCost = 100,
                ContextPagesPerSide = 1
            });

        var thumbnail = new ThumbnailPrecheckDocument(
            "1.0.0",
            new GlobalObservations("ja", "rtl", 0.95, []),
            pages.Select(page => new PageThumbnailObservation(
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
                [])).ToArray(),
            [new PageRelation(
                "rel-spread",
                "spread",
                ["p0002", "p0003"],
                HardConstraint: true,
                MustSameBatch: true,
                RequiresOcrCoordination: true,
                RequiresMaskCoordination: true,
                Confidence: 0.95,
                Evidence: "continuation")]);

        var plan = new DynamicBatchPlanner().Plan(request, thumbnail);

        Assert.Equal(2, plan.Batches.Count);
        Assert.Contains(plan.Batches, batch =>
            batch.OwnerPages.SequenceEqual(["p0001", "p0002", "p0003"]));
        Assert.Contains(plan.Batches, batch =>
            batch.OwnerPages.SequenceEqual(["p0004", "p0005"]));

        var first = plan.Batches[0];
        Assert.Contains("rel-spread", first.HardRelationIds);
        Assert.Equal(["p0004"], plan.Batches[0].ContextPagesAfter);
        Assert.Equal(["p0003"], plan.Batches[1].ContextPagesBefore);
    }

    private sealed class NullPageAssetProvider : IPageAssetProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
            string pageId,
            ImageQuality quality,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }
    }
}
