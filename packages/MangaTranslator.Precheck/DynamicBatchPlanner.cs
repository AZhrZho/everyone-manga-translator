namespace MangaTranslator.Precheck;

public sealed class DynamicBatchPlanner
{
#pragma warning disable CA1822
    public BatchPlanDocument Plan(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(thumbnailPrecheck);

        if (request.Pages.Count == 0)
        {
            return new BatchPlanDocument("1.0.0", []);
        }

        var observationsById = thumbnailPrecheck.Pages
            .ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var relations = thumbnailPrecheck.RelationCandidates
            .Where(relation => relation.MustSameBatch || relation.HardConstraint)
            .Where(relation => relation.Pages.Count > 1)
            .ToArray();

        var groups = BuildHardGroups(request.Pages, relations);
        var batches = new List<BatchPlan>();
        var current = new List<PageGroup>();
        var currentCost = 0d;

        foreach (var group in groups)
        {
            var groupCost = CalculateGroupCost(group, observationsById);
            var exceedsPageLimit = current.Count > 0
                && current.Sum(item => item.Pages.Count) + group.Pages.Count > request.Options.MaxBatchPageCount;
            var exceedsCostLimit = current.Count > 0
                && currentCost + groupCost > request.Options.MaxBatchCost;

            if (exceedsPageLimit || exceedsCostLimit)
            {
                batches.Add(CreateBatch(
                    request,
                    current,
                    observationsById,
                    relations,
                    batches.Count + 1,
                    currentCost,
                    "cost_or_page_limit"));

                current = [];
                currentCost = 0;
            }

            current.Add(group);
            currentCost += groupCost;
        }

        if (current.Count > 0)
        {
            batches.Add(CreateBatch(
                request,
                current,
                observationsById,
                relations,
                batches.Count + 1,
                currentCost,
                "end_of_input"));
        }

        return new BatchPlanDocument("1.0.0", batches);
    }
#pragma warning restore CA1822

    private static PageGroup[] BuildHardGroups(
        IReadOnlyList<PageDescriptor> pages,
        IReadOnlyList<PageRelation> relations)
    {
        var parent = pages.ToDictionary(page => page.PageId, page => page.PageId, StringComparer.Ordinal);

        string Find(string pageId)
        {
            var current = pageId;
            while (!StringComparer.Ordinal.Equals(parent[current], current))
            {
                parent[current] = parent[parent[current]];
                current = parent[current];
            }

            return current;
        }

        void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (!StringComparer.Ordinal.Equals(leftRoot, rightRoot))
            {
                parent[rightRoot] = leftRoot;
            }
        }

        foreach (var relation in relations)
        {
            var first = relation.Pages.FirstOrDefault(parent.ContainsKey);
            if (first is null)
            {
                continue;
            }

            foreach (var pageId in relation.Pages.Skip(1).Where(parent.ContainsKey))
            {
                Union(first, pageId);
            }
        }

        var groups = pages
            .GroupBy(page => Find(page.PageId), StringComparer.Ordinal)
            .Select(group => new PageGroup(group.OrderBy(page => page.SourceIndex).ToArray()))
            .OrderBy(group => group.Pages[0].SourceIndex)
            .ToArray();

        return groups;
    }

    private static double CalculateGroupCost(
        PageGroup group,
        Dictionary<string, PageThumbnailObservation> observations)
    {
        return group.Pages.Sum(page =>
        {
            if (!observations.TryGetValue(page.PageId, out var observation))
            {
                return 1d;
            }

            return 1d
                + 0.6 * Math.Clamp(observation.LayoutComplexity, 0, 5)
                + 1.2 * Math.Clamp(observation.TextRiskScore, 0, 5) / 5d
                + Math.Clamp(observation.MaskRiskScore, 0, 5) / 5d
                + Math.Clamp(observation.UncertaintyScore, 0, 5) / 5d;
        });
    }

    private static BatchPlan CreateBatch(
        PrecheckRequest request,
        IReadOnlyList<PageGroup> groups,
        Dictionary<string, PageThumbnailObservation> observations,
        IReadOnlyList<PageRelation> relations,
        int sequence,
        double cost,
        string splitReasonAfter)
    {
        var ownerPages = groups
            .SelectMany(group => group.Pages)
            .OrderBy(page => page.SourceIndex)
            .ToArray();
        var firstIndex = ownerPages[0].SourceIndex;
        var lastIndex = ownerPages[^1].SourceIndex;

        var contextBefore = request.Pages
            .Where(page => page.SourceIndex < firstIndex)
            .OrderByDescending(page => page.SourceIndex)
            .Take(request.Options.ContextPagesPerSide)
            .OrderBy(page => page.SourceIndex)
            .Select(page => page.PageId)
            .ToArray();
        var contextAfter = request.Pages
            .Where(page => page.SourceIndex > lastIndex)
            .OrderBy(page => page.SourceIndex)
            .Take(request.Options.ContextPagesPerSide)
            .Select(page => page.PageId)
            .ToArray();

        var relationIds = relations
            .Where(relation => relation.Pages.Any(ownerPages.Select(page => page.PageId).Contains))
            .Select(relation => relation.RelationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var priority = ownerPages.Any(page =>
        {
            return observations.TryGetValue(page.PageId, out var observation)
                && (observation.NeedsHighResolutionReview
                    || observation.Confidence < 0.7);
        }) ? "high" : "normal";

        return new BatchPlan(
            $"batch_{sequence:000}",
            ownerPages.Select(page => page.PageId).ToArray(),
            contextBefore,
            contextAfter,
            relationIds,
            cost,
            priority,
            null,
            splitReasonAfter);
    }

    private sealed record PageGroup(IReadOnlyList<PageDescriptor> Pages);
}
