using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Pipeline;

public sealed class PipelinePrecheckArtifactImporter(
    IProjectArtifactStore artifacts,
    IProjectStore projects)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task ImportAsync(
        string projectId,
        PipelineArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        foreach (var (name, path) in descriptor.PrecheckFixture.ArtifactPaths
                     ?? new Dictionary<string, string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await artifacts.SaveAsync(
                projectId,
                name,
                await PlatformIoPath.ReadAllTextAsync(path, cancellationToken),
                cancellationToken);
        }

        if (descriptor.PrecheckFixture.ArtifactPaths is not null
            && descriptor.PrecheckFixture.ArtifactPaths.TryGetValue("semantic_merge.json", out var semanticPath))
        {
            using var semantic = JsonDocument.Parse(
                await PlatformIoPath.ReadAllTextAsync(semanticPath, cancellationToken));
            await artifacts.SaveAsync(
                projectId,
                "characters.json",
                CreateCharactersJson(descriptor, semantic.RootElement),
                cancellationToken);
            await artifacts.SaveAsync(
                projectId,
                "glossary.json",
                CreateGlossaryJson(descriptor, semantic.RootElement),
                cancellationToken);
        }

        await ApplyBookManifestAsync(projectId, descriptor, cancellationToken);
    }

    private async Task ApplyBookManifestAsync(
        string projectId,
        PipelineArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.PrecheckFixture.ArtifactPaths is null
            || !descriptor.PrecheckFixture.ArtifactPaths.TryGetValue("book_manifest.json", out var manifestPath))
        {
            return;
        }

        using var document = JsonDocument.Parse(
            await PlatformIoPath.ReadAllTextAsync(manifestPath, cancellationToken));
        if (!document.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var snapshot = await projects.GetAsync(projectId, cancellationToken);
        var pagesByIndex = snapshot.Pages.ToDictionary(page => page.Index);
        foreach (var manifestPage in pages.EnumerateArray().OrderBy(ReadOrder))
        {
            var index = ReadOrder(manifestPage);
            if (!pagesByIndex.TryGetValue(index, out var page)) continue;
            var pageType = ReadString(manifestPage, "page_type")
                           ?? ReadString(manifestPage, "pageType")
                           ?? "unknown";
            var included = ReadBoolean(manifestPage, "include_in_mask")
                           ?? ReadBoolean(manifestPage, "includeInMask")
                           ?? true;
            if (string.Equals(page.PageType, pageType, StringComparison.OrdinalIgnoreCase)
                && page.IncludedInWorkflow == included)
            {
                continue;
            }

            snapshot = await projects.UpdatePageReviewAsync(
                projectId,
                page.PageId,
                new UpdatePageReviewCommand(pageType, included, snapshot.Revision),
                cancellationToken);
            pagesByIndex = snapshot.Pages.ToDictionary(item => item.Index);
        }
    }

    private static string CreateCharactersJson(
        PipelineArtifactDescriptor descriptor,
        JsonElement semantic)
    {
        var result = new List<object>();
        if (semantic.TryGetProperty("characters", out var characters)
            && characters.ValueKind == JsonValueKind.Array)
        {
            foreach (var character in characters.EnumerateArray())
            {
                var key = ReadString(character, "character_id")
                          ?? StableKey(ReadString(character, "original_name") ?? Guid.NewGuid().ToString("N"));
                var value = ReadString(character, "translated_name")
                            ?? ReadString(character, "original_name")
                            ?? key;
                result.Add(new
                {
                    operation = "add",
                    mergeKey = key,
                    candidateId = (string?)null,
                    value,
                    pages = ReadEvidencePages(character),
                    evidence = ReadEvidenceSummary(character),
                    confidence = ReadDouble(character, "confidence") ?? 0d
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            runId = descriptor.RunId,
            bookId = descriptor.BookId,
            characters = result,
            conflicts = Array.Empty<object>()
        }, JsonOptions);
    }

    private static string CreateGlossaryJson(
        PipelineArtifactDescriptor descriptor,
        JsonElement semantic)
    {
        var result = new List<object>();
        if (semantic.TryGetProperty("glossary", out var terms)
            && terms.ValueKind == JsonValueKind.Array)
        {
            foreach (var term in terms.EnumerateArray())
            {
                var source = ReadString(term, "source") ?? string.Empty;
                var value = ReadString(term, "translation") ?? source;
                result.Add(new
                {
                    operation = "add",
                    mergeKey = StableKey(source.Length == 0 ? value : source),
                    candidateId = (string?)null,
                    value,
                    pages = ReadEvidencePages(term),
                    evidence = ReadEvidenceSummary(term),
                    confidence = ReadBoolean(term, "locked") == true ? 1d : 0.85d
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            runId = descriptor.RunId,
            bookId = descriptor.BookId,
            terms = result,
            conflicts = Array.Empty<object>()
        }, JsonOptions);
    }

    private static IReadOnlyList<string> ReadEvidencePages(JsonElement item)
    {
        if (!item.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return evidence.EnumerateArray()
            .Select(entry => ReadString(entry, "page_id"))
            .Where(page => !string.IsNullOrWhiteSpace(page))
            .Select(page => page!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadEvidenceSummary(JsonElement item)
    {
        if (!item.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Array)
        {
            return ReadString(item, "selection_rationale")
                   ?? ReadString(item, "rationale")
                   ?? string.Empty;
        }

        var entries = evidence.EnumerateArray()
            .Select(entry => new
            {
                Page = ReadString(entry, "page_id"),
                Quote = ReadString(entry, "quote")
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Quote))
            .Take(4)
            .Select(entry => string.IsNullOrWhiteSpace(entry.Page)
                ? entry.Quote!
                : $"{entry.Page}: {entry.Quote}")
            .ToArray();
        return entries.Length == 0
            ? ReadString(item, "selection_rationale")
              ?? ReadString(item, "rationale")
              ?? string.Empty
            : string.Join("；", entries);
    }

    private static string StableKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "term_" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static int ReadOrder(JsonElement page)
    {
        if (page.TryGetProperty("order", out var order)
            && order.ValueKind == JsonValueKind.Number
            && order.TryGetInt32(out var value))
        {
            return value;
        }

        var pageId = ReadString(page, "page_id") ?? ReadString(page, "pageId") ?? string.Empty;
        var digits = new string(pageId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out value) ? value : 0;
    }

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
