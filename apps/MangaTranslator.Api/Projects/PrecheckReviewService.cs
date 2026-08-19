using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.Contracts;
using ProjectModel = MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Projects;

public sealed class PrecheckReviewService(
    ProjectModel.IProjectArtifactStore artifacts,
    ProjectModel.IProjectStore projects)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PrecheckReviewResponse> GetAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var artifactMap = (await artifacts.ListAsync(projectId, cancellationToken))
            .ToDictionary(item => item.Name, item => item.Json, StringComparer.OrdinalIgnoreCase);
        return CreateResponse(artifactMap, project.Revision);
    }

    internal static PrecheckReviewResponse CreateResponse(
        IReadOnlyDictionary<string, string> artifactMap,
        int projectRevision)
    {
        var storyBible = ReadCompletedStoryBible(artifactMap);
        return new PrecheckReviewResponse(
            ReadRunId(artifactMap),
            projectRevision,
            ReadPages(artifactMap),
            ReadCharacters(artifactMap, storyBible),
            ReadTerms(artifactMap, storyBible),
            ReadBatches(artifactMap),
            ReadContext(storyBible));
    }

    public async Task<ProjectModel.ProjectSnapshot> UpdateItemAsync(
        string projectId,
        string artifactName,
        UpdatePrecheckItemRequest request,
        CancellationToken cancellationToken)
    {
        var artifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Name, artifactName, StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            throw new ProjectModel.ProjectFormatException($"项目中不存在预处理结果 {artifactName}。");
        }

        var root = JsonNode.Parse(artifact.Json) as JsonObject
            ?? throw new ProjectModel.ProjectFormatException($"预处理结果 {artifactName} 不是 JSON 对象。");
        var arrayName = artifactName.Equals("characters.json", StringComparison.OrdinalIgnoreCase)
            ? "characters"
            : artifactName.Equals("glossary.json", StringComparison.OrdinalIgnoreCase)
                ? "terms"
                : throw new ProjectModel.ProjectFormatException("当前只支持编辑角色和术语结果。");
        var items = root[arrayName] as JsonArray
            ?? throw new ProjectModel.ProjectFormatException($"预处理结果 {artifactName} 缺少 {arrayName} 数组。");
        var source = items
            .OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(item["mergeKey"]?.GetValue<string>(), request.ItemKey, StringComparison.Ordinal));
        if (source is null)
        {
            throw new ProjectModel.ProjectFormatException($"预处理结果中不存在条目 {request.ItemKey}。");
        }

        if (!string.IsNullOrWhiteSpace(request.MergeIntoKey))
        {
            var target = items
                .OfType<JsonObject>()
                .FirstOrDefault(item => string.Equals(item["mergeKey"]?.GetValue<string>(), request.MergeIntoKey, StringComparison.Ordinal));
            if (target is null)
            {
                throw new ProjectModel.ProjectFormatException($"找不到合并目标 {request.MergeIntoKey}。");
            }

            MergePages(target, source);
            if (!string.IsNullOrWhiteSpace(request.Value))
            {
                target["value"] = request.Value.Trim();
            }

            items.Remove(source);
        }
        else if (request.Delete)
        {
            items.Remove(source);
        }
        else if (request.Value is not null)
        {
            source["value"] = request.Value.Trim();
        }
        else
        {
            throw new ProjectModel.ProjectFormatException("没有可应用的预处理审阅操作。");
        }

        var updatedJson = root.ToJsonString(JsonOptions);
        await artifacts.UpdateAsync(projectId, artifact.Name, updatedJson, request.ExpectedRevision, cancellationToken);
        return await projects.GetAsync(projectId, cancellationToken);
    }

    private static string? ReadRunId(IReadOnlyDictionary<string, string> artifacts)
    {
        foreach (var artifact in artifacts.Values)
        {
            using var document = JsonDocument.Parse(artifact);
            if (document.RootElement.TryGetProperty("runId", out var runId) && runId.ValueKind is JsonValueKind.String)
            {
                return runId.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<PrecheckPageReviewResponse> ReadPages(IReadOnlyDictionary<string, string> artifacts)
    {
        if (artifacts.TryGetValue("page_manifest.json", out var manifestJson))
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            if (manifest.RootElement.TryGetProperty("pages", out var manifestPages)
                && manifestPages.ValueKind is JsonValueKind.Array)
            {
                return manifestPages.EnumerateArray().Select(page => new PrecheckPageReviewResponse(
                    ReadString(page, "pageId") ?? string.Empty,
                    ReadInt(page, "order") ?? 0,
                    ReadString(page, "pageType") ?? "unknown",
                    ReadDouble(page, "confidence") ?? 0,
                    ReadStringArray(page, "evidence"))).ToArray();
            }
        }

        if (!artifacts.TryGetValue("thumbnail_precheck.json", out var json)) return [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind is not JsonValueKind.Array) return [];
        return pages.EnumerateArray().Select(page =>
        {
            var pageId = ReadString(page, "pageId") ?? string.Empty;
            return new PrecheckPageReviewResponse(
                pageId,
                PageIndex(pageId) ?? 0,
                ReadString(page, "pageType") ?? "unknown",
                ReadDouble(page, "confidence") ?? 0,
                ReadStringArray(page, "evidence"));
        }).ToArray();
    }

    private static IReadOnlyList<PrecheckCharacterResponse> ReadCharacters(
        IReadOnlyDictionary<string, string> artifacts,
        JsonElement? storyBible)
    {
        if (!artifacts.TryGetValue("characters.json", out var json)) return [];
        var detailsByKey = IndexCharacterDetails(storyBible);
        return ReadItems(json, "characters")
            .Select(item =>
            {
                var mergeKey = ReadString(item, "mergeKey") ?? string.Empty;
                var value = ReadString(item, "value") ?? string.Empty;
                detailsByKey.TryGetValue(mergeKey, out var detail);
                return new PrecheckCharacterResponse(
                    mergeKey,
                    value,
                    ReadPageIndices(item, "pages"),
                    ReadString(item, "evidence") ?? string.Empty,
                    ReadDouble(item, "confidence") ?? 0,
                    detail is null ? null : ReadCharacterDetails(detail.Value, value));
            })
            .ToArray();
    }

    private static IReadOnlyList<PrecheckTermResponse> ReadTerms(
        IReadOnlyDictionary<string, string> artifacts,
        JsonElement? storyBible)
    {
        if (!artifacts.TryGetValue("glossary.json", out var json)) return [];
        var detailsByKey = IndexTermDetails(storyBible);
        return ReadItems(json, "terms")
            .Select(item =>
            {
                var mergeKey = ReadString(item, "mergeKey") ?? string.Empty;
                var value = ReadString(item, "value") ?? string.Empty;
                detailsByKey.TryGetValue(mergeKey, out var detail);
                return new PrecheckTermResponse(
                    mergeKey,
                    value,
                    ReadPageIndices(item, "pages"),
                    ReadString(item, "evidence") ?? string.Empty,
                    ReadDouble(item, "confidence") ?? 0,
                    detail is null ? null : ReadTermDetails(detail.Value, value));
            })
            .ToArray();
    }

    private static IReadOnlyList<PrecheckBatchResponse> ReadBatches(IReadOnlyDictionary<string, string> artifacts)
    {
        if (artifacts.TryGetValue("precheck_scan_plan.json", out var planJson))
        {
            using var plan = JsonDocument.Parse(planJson);
            if (plan.RootElement.TryGetProperty("batches", out var scanBatches)
                && scanBatches.ValueKind is JsonValueKind.Array)
            {
                return scanBatches.EnumerateArray().Select(batch => new PrecheckBatchResponse(
                    ReadString(batch, "batchId") ?? string.Empty,
                    ReadPageIndices(batch, "ownerPages"),
                    "normal",
                    ReadPageIndices(batch, "ownerPages").Count)).ToArray();
            }
        }

        if (!artifacts.TryGetValue("batch_plan.json", out var json)) return [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("batches", out var batches) || batches.ValueKind is not JsonValueKind.Array) return [];
        return batches.EnumerateArray().Select(batch => new PrecheckBatchResponse(
            ReadString(batch, "batchId") ?? string.Empty,
            ReadPageIndices(batch, "ownerPages"),
            ReadString(batch, "priority") ?? "normal",
            ReadDouble(batch, "estimatedCost") ?? 0)).ToArray();
    }

    private static JsonElement? ReadCompletedStoryBible(IReadOnlyDictionary<string, string> artifacts)
    {
        if (!artifacts.TryGetValue("translation_book_context.json", out var json)
            || !artifacts.ContainsKey("characters.json")
            || !artifacts.ContainsKey("glossary.json"))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var runId = ReadString(root, "runId");
            if (root.ValueKind is not JsonValueKind.Object
                || string.IsNullOrWhiteSpace(ReadString(root, "schemaVersion"))
                || string.IsNullOrWhiteSpace(runId)
                || !ArtifactMatchesRun(artifacts["characters.json"], runId)
                || !ArtifactMatchesRun(artifacts["glossary.json"], runId)
                || !TryGetObject(root, "synopsis", out var synopsis)
                || !TryGetObject(root, "translationStyle", out var style)
                || ReadString(synopsis, "overall") is null
                || ReadString(synopsis, "tone") is null
                || !HasArray(synopsis, "themes")
                || ReadString(style, "overallVoice") is null
                || !HasArray(style, "dialogueRules")
                || ReadString(style, "honorificPolicy") is null
                || ReadString(style, "namePolicy") is null
                || !HasArray(style, "prohibitedTendencies")
                || !HasArray(root, "characters")
                || !HasArray(root, "glossary")
                || !HasArray(root, "continuityFacts")
                || !HasArray(root, "unresolvedQuestions"))
            {
                return null;
            }

            return root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ArtifactMatchesRun(string json, string runId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return string.Equals(ReadString(document.RootElement, "runId"), runId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PrecheckContextSummaryResponse? ReadContext(JsonElement? storyBible)
    {
        if (storyBible is not { } root
            || !TryGetObject(root, "synopsis", out var synopsis)
            || !TryGetObject(root, "translationStyle", out var style))
        {
            return null;
        }

        var continuityFacts = ReadArray(root, "continuityFacts")
            .Select(item => new PrecheckContinuityFactResponse(
                ReadString(item, "fact") ?? string.Empty,
                ReadInt(item, "revealedAtOrder") ?? 0,
                ReadString(item, "safeWordingBeforeReveal") ?? string.Empty,
                ReadStringArray(item, "evidencePages")))
            .ToArray();
        return new PrecheckContextSummaryResponse(
            ReadString(synopsis, "overall") ?? string.Empty,
            ReadString(synopsis, "tone") ?? string.Empty,
            ReadStringArray(synopsis, "themes"),
            ReadString(style, "overallVoice") ?? string.Empty,
            ReadStringArray(style, "dialogueRules"),
            ReadString(style, "honorificPolicy") ?? string.Empty,
            ReadString(style, "namePolicy") ?? string.Empty,
            ReadStringArray(style, "prohibitedTendencies"),
            ArrayLength(root, "characters"),
            ArrayLength(root, "glossary"),
            continuityFacts.Length,
            continuityFacts,
            ReadStringArray(root, "unresolvedQuestions"));
    }

    private static IReadOnlyDictionary<string, JsonElement?> IndexCharacterDetails(JsonElement? storyBible)
    {
        Dictionary<string, JsonElement?> result = new(StringComparer.Ordinal);
        if (storyBible is not { } root) return result;
        foreach (var character in ReadArray(root, "characters"))
        {
            var key = ReadString(character, "characterId");
            if (!string.IsNullOrWhiteSpace(key)) result[key] = character;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement?> IndexTermDetails(JsonElement? storyBible)
    {
        Dictionary<string, JsonElement?> result = new(StringComparer.Ordinal);
        if (storyBible is not { } root) return result;
        foreach (var term in ReadArray(root, "glossary"))
        {
            var source = ReadString(term, "source") ?? string.Empty;
            var translation = ReadString(term, "translation") ?? source;
            result[StableTermKey(source.Length == 0 ? translation : source)] = term;
        }

        return result;
    }

    private static PrecheckCharacterDetailsResponse ReadCharacterDetails(JsonElement item, string reviewedValue) => new(
        ReadString(item, "originalName") ?? string.Empty,
        reviewedValue,
        ReadStringArray(item, "aliases"),
        ReadString(item, "role") ?? string.Empty,
        ReadStringArray(item, "speechRules"),
        ReadStringArray(item, "relationships"),
        ReadInt(item, "validFromOrder") ?? 1,
        ReadInt(item, "validToOrder"),
        ReadBoolean(item, "revealSensitive") ?? false,
        ReadStringArray(item, "evidencePages"));

    private static PrecheckTermDetailsResponse ReadTermDetails(JsonElement item, string reviewedValue) => new(
        ReadString(item, "source") ?? string.Empty,
        reviewedValue,
        ReadString(item, "category") ?? string.Empty,
        ReadString(item, "usageRule") ?? string.Empty,
        ReadInt(item, "validFromOrder") ?? 1,
        ReadInt(item, "validToOrder"),
        ReadBoolean(item, "revealSensitive") ?? false,
        ReadStringArray(item, "evidencePages"));

    private static IEnumerable<JsonElement> ReadItems(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var items) || items.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        // Clone each element before the document is disposed.
        return items.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var values) && values.ValueKind is JsonValueKind.Array
            ? values.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [];

    private static void MergePages(JsonObject target, JsonObject source)
    {
        var targetPages = target["pages"] as JsonArray ?? new JsonArray();
        var seen = targetPages.Select(item => item?.GetValue<string>()).Where(item => item is not null).ToHashSet(StringComparer.Ordinal);
        if (source["pages"] is JsonArray sourcePages)
        {
            foreach (var page in sourcePages)
            {
                var value = page?.GetValue<string>();
                if (value is not null && seen.Add(value)) targetPages.Add(value);
            }
        }

        target["pages"] = targetPages;
    }

    private static IReadOnlyList<int> ReadPageIndices(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var pages) || pages.ValueKind is not JsonValueKind.Array) return [];
        return pages.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.Number when item.TryGetInt32(out var number) => number,
            JsonValueKind.String => PageIndex(item.GetString() ?? string.Empty) ?? 0,
            _ => 0
        }).Where(item => item > 0).ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var values) && values.ValueKind is JsonValueKind.Array
            ? values.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static double? ReadDouble(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var result) ? result : null;

    private static bool? ReadBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? ReadInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    private static int ArrayLength(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static bool HasArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.Array;

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.TryGetProperty(propertyName, out value) && value.ValueKind is JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static string StableTermKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "term_" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static int? PageIndex(string pageId)
    {
        var digits = new string(pageId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
