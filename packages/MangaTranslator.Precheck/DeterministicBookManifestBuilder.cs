using System.Text.Json;

namespace MangaTranslator.Precheck;

/// <summary>
/// Builds the downstream page-routing manifest from authoritative precheck results.
/// No model call or file-system access is performed by this component.
/// </summary>
public sealed class DeterministicBookManifestBuilder : IBookManifestBuilder
{
    private const string ContainsNonSfxText = "contains_non_sfx_text";
    private const string SfxOnly = "sfx_only";
    private const string NoText = "no_text";

    public BookManifestDocument Build(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        IReadOnlyList<BatchObservationDocument> observations,
        GlobalSemanticMergeResult semanticMerge)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(thumbnailPrecheck);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(semanticMerge);

        var orderedPages = request.Pages
            .OrderBy(page => page.SourceIndex)
            .ThenBy(page => page.PageId, StringComparer.Ordinal)
            .ToArray();
        var ordinalByPageId = orderedPages
            .Select((page, index) => (page.PageId, Ordinal: index))
            .ToDictionary(item => item.PageId, item => item.Ordinal, StringComparer.Ordinal);
        var thumbnails = thumbnailPrecheck.Pages
            .GroupBy(page => page.PageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var batchSignals = ReadBatchSignals(observations);
        var chapterRanges = ReadChapterRanges(semanticMerge.Chapters, ordinalByPageId);

        var pages = new List<BookManifestPage>(orderedPages.Length);
        PageState? previous = null;
        var sceneOrdinal = 0;
        var currentSceneId = string.Empty;

        for (var index = 0; index < orderedPages.Length; index++)
        {
            var page = orderedPages[index];
            thumbnails.TryGetValue(page.PageId, out var thumbnail);
            batchSignals.TryGetValue(page.PageId, out var signal);

            var pageType = NormalizePageType(signal?.PageType ?? thumbnail?.PageType);
            var chapterId = StringComparer.Ordinal.Equals(pageType, "story")
                ? FindChapterId(index, chapterRanges)
                : null;
            var scope = chapterId ?? (StringComparer.Ordinal.Equals(pageType, "story")
                ? "unassigned_story"
                : "paratext");
            var state = new PageState(
                page.PageId,
                index,
                pageType,
                chapterId,
                scope,
                signal?.SceneBoundaryBefore,
                signal?.TextContent,
                thumbnail?.SceneCandidateId);

            if (previous is null || StartsNewScene(
                    previous,
                    state,
                    thumbnailPrecheck.RelationCandidates,
                    request.Options.ContactSheetPageCount))
            {
                sceneOrdinal++;
                currentSceneId = $"c{sceneOrdinal:000}:{SceneKind(pageType)}";
            }

            pages.Add(new BookManifestPage(
                page.PageId,
                index + 1,
                chapterId,
                currentSceneId,
                pageType,
                ShouldIncludeInMask(pageType, signal?.TextContent)));
            previous = state;
        }

        return new BookManifestDocument("1.0", request.BookId, pages);
    }

    private static Dictionary<string, BatchPageSignal> ReadBatchSignals(
        IReadOnlyList<BatchObservationDocument> observations)
    {
        var result = new Dictionary<string, BatchPageSignal>(StringComparer.Ordinal);
        foreach (var batch in observations.OrderBy(item => item.BatchId, StringComparer.Ordinal))
        {
            if (batch.Observations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var ownerPages = batch.OwnerPages.ToHashSet(StringComparer.Ordinal);
            foreach (var item in batch.Observations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGetString(item, "pageId", out var pageId)
                    || !ownerPages.Contains(pageId))
                {
                    continue;
                }

                var pageType = TryGetString(item, "pageType", out var type) ? type : null;
                var textContent = TryGetString(item, "textContent", out var content)
                    ? content
                    : null;
                bool? sceneBoundaryBefore = item.TryGetProperty(
                        "sceneBoundaryBefore",
                        out var boundary)
                    && boundary.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? boundary.GetBoolean()
                        : null;
                result[pageId] = new BatchPageSignal(
                    pageType,
                    textContent,
                    sceneBoundaryBefore);
            }
        }

        return result;
    }

    private static ChapterRange[] ReadChapterRanges(
        JsonElement chapters,
        Dictionary<string, int> ordinalByPageId)
    {
        if (chapters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ranges = new List<ChapterRange>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            if (chapter.ValueKind != JsonValueKind.Object
                || !TryGetString(chapter, "chapter_id", out var chapterId)
                || !TryGetString(chapter, "page_start", out var pageStart)
                || !TryGetString(chapter, "page_end", out var pageEnd)
                || !ordinalByPageId.TryGetValue(pageStart, out var start)
                || !ordinalByPageId.TryGetValue(pageEnd, out var end)
                || end < start)
            {
                continue;
            }

            ranges.Add(new ChapterRange(chapterId, start, end));
        }

        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Start <= ordered[index - 1].End)
            {
                throw new PrecheckAnalysisException(
                    $"Final chapter ranges overlap: {ordered[index - 1].ChapterId} and "
                    + $"{ordered[index].ChapterId}.");
            }
        }

        return ordered;
    }

    private static string? FindChapterId(int pageOrdinal, ChapterRange[] ranges)
    {
        foreach (var range in ranges)
        {
            if (pageOrdinal >= range.Start && pageOrdinal <= range.End)
            {
                return range.ChapterId;
            }
        }

        return null;
    }

    private static bool StartsNewScene(
        PageState previous,
        PageState current,
        IReadOnlyList<PageRelation> relations,
        int contactSheetPageCount)
    {
        if (current.SceneBoundaryBefore is true
            || !StringComparer.Ordinal.Equals(previous.Scope, current.Scope))
        {
            return true;
        }

        if (!StringComparer.Ordinal.Equals(current.PageType, "story"))
        {
            return !StringComparer.Ordinal.Equals(previous.PageType, current.PageType);
        }

        if (current.SceneBoundaryBefore is false)
        {
            return false;
        }

        var relation = FindAdjacentSceneRelation(relations, previous.PageId, current.PageId);
        if (relation is not null)
        {
            if (StringComparer.Ordinal.Equals(relation.Type, "scene_transition"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(relation.Type, "scene_continuation"))
            {
                return false;
            }
        }

        var chunkSize = Math.Max(1, contactSheetPageCount);
        var sameThumbnailChunk = previous.Ordinal / chunkSize == current.Ordinal / chunkSize;
        return sameThumbnailChunk
            && !string.IsNullOrWhiteSpace(previous.SceneCandidateId)
            && !string.IsNullOrWhiteSpace(current.SceneCandidateId)
            && !StringComparer.Ordinal.Equals(
                previous.SceneCandidateId,
                current.SceneCandidateId);
    }

    private static PageRelation? FindAdjacentSceneRelation(
        IReadOnlyList<PageRelation> relations,
        string previousPageId,
        string currentPageId)
    {
        return relations
            .Where(relation => relation.Confidence >= 0.5)
            .Where(relation => StringComparer.Ordinal.Equals(relation.Type, "scene_transition")
                || StringComparer.Ordinal.Equals(relation.Type, "scene_continuation"))
            .FirstOrDefault(relation => relation.Pages.Count >= 2
                && relation.Pages.Zip(relation.Pages.Skip(1))
                    .Any(pair => StringComparer.Ordinal.Equals(pair.First, previousPageId)
                        && StringComparer.Ordinal.Equals(pair.Second, currentPageId)));
    }

    private static bool ShouldIncludeInMask(string pageType, string? textContent)
    {
        if (!StringComparer.Ordinal.Equals(pageType, "story"))
        {
            return false;
        }

        return textContent switch
        {
            ContainsNonSfxText => true,
            SfxOnly => false,
            NoText => false,
            _ => true
        };
    }

    private static string NormalizePageType(string? pageType) => pageType?.Trim() switch
    {
        "front_cover" or "back_cover" => "cover",
        "table_of_contents" => "toc",
        "copyright" => "credits",
        "" or null => "unknown",
        var value => value
    };

    private static string SceneKind(string pageType) => pageType switch
    {
        "story" => "story",
        "cover" => "cover",
        "toc" => "toc",
        "credits" => "credits",
        var value => value
    };

    private static bool TryGetString(
        JsonElement item,
        string propertyName,
        out string value)
    {
        if (item.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed record BatchPageSignal(
        string? PageType,
        string? TextContent,
        bool? SceneBoundaryBefore);

    private sealed record ChapterRange(string ChapterId, int Start, int End);

    private sealed record PageState(
        string PageId,
        int Ordinal,
        string PageType,
        string? ChapterId,
        string Scope,
        bool? SceneBoundaryBefore,
        string? TextContent,
        string? SceneCandidateId);
}
