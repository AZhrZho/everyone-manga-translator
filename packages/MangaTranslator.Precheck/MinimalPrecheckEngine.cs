using System.Collections.Concurrent;
using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

public sealed class MinimalPrecheckEngine(
    IModelProvider scanProvider,
    IModelProvider consolidationProvider)
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ExcludedPageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "title_page",
        "table_of_contents",
        "credits",
        "advertisement",
        "blank"
    };

    public static IReadOnlyList<MinimalPrecheckScanBatch> Plan(MinimalPrecheckRequest request)
    {
        Validate(request);
        var ordered = request.Pages.OrderBy(page => page.SourceIndex).ToArray();
        var pageCount = Math.Max(1, request.Options.PagesPerScan);
        var context = Math.Max(0, request.Options.ContextPagesPerSide);
        return ordered.Chunk(pageCount).Select((owners, index) =>
        {
            var ownerIds = owners.Select(page => page.PageId).ToArray();
            var first = Array.FindIndex(ordered, page => page.PageId == ownerIds[0]);
            var last = first + owners.Length - 1;
            return new MinimalPrecheckScanBatch(
                $"scan_{index + 1:000}",
                ownerIds,
                ordered.Skip(Math.Max(0, first - context)).Take(Math.Min(context, first)).Select(page => page.PageId).ToArray(),
                ordered.Skip(last + 1).Take(context).Select(page => page.PageId).ToArray());
        }).ToArray();
    }

    public async Task<IReadOnlyList<MinimalPrecheckScanResult>> ScanAsync(
        MinimalPrecheckRequest request,
        IReadOnlyList<MinimalPrecheckScanBatch> plan,
        IReadOnlyDictionary<string, MinimalPrecheckScanResult>? restored,
        Func<MinimalPrecheckScanBatch, bool, CancellationToken, Task>? onStarted,
        Func<MinimalPrecheckScanBatch, MinimalPrecheckScanResult, bool, CancellationToken, Task>? onCompleted,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var restoredResults = restored ?? new Dictionary<string, MinimalPrecheckScanResult>(StringComparer.Ordinal);
        var results = new ConcurrentDictionary<string, MinimalPrecheckScanResult>(StringComparer.Ordinal);
        foreach (var (batchId, result) in restoredResults)
        {
            results[batchId] = result;
        }

        var maximumConcurrency = Math.Max(1, request.Options.MaxConcurrentScans);
        using var semaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var tasks = plan.Select(async batch =>
        {
            if (restoredResults.TryGetValue(batch.BatchId, out var restoredResult))
            {
                if (onStarted is not null) await onStarted(batch, true, cancellationToken).ConfigureAwait(false);
                if (onCompleted is not null) await onCompleted(batch, restoredResult, true, cancellationToken).ConfigureAwait(false);
                return;
            }

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (onStarted is not null) await onStarted(batch, false, cancellationToken).ConfigureAwait(false);
                var result = await ScanBatchAsync(request, batch, cancellationToken).ConfigureAwait(false);
                results[batch.BatchId] = result;
                if (onCompleted is not null) await onCompleted(batch, result, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return plan.Select(batch => results[batch.BatchId]).ToArray();
    }

    public static MinimalPageManifestDocument BuildPageManifest(
        MinimalPrecheckRequest request,
        IReadOnlyList<MinimalPrecheckScanResult> scans)
    {
        var byPage = scans.SelectMany(scan => scan.Pages)
            .GroupBy(page => page.PageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(page => page.Confidence).First(), StringComparer.Ordinal);
        return new MinimalPageManifestDocument(
            "1.0.0",
            request.Options.RunId,
            request.BookId,
            request.Pages.OrderBy(page => page.SourceIndex).Select(page =>
            {
                var classification = byPage.GetValueOrDefault(page.PageId)
                    ?? new MinimalPageClassification(page.PageId, "unknown", 0, ["The scan did not return this page; included conservatively."]);
                var pageType = NormalizePageType(classification.PageType);
                var exclude = classification.Confidence >= request.Options.MinimumExclusionConfidence
                              && ExcludedPageTypes.Contains(pageType);
                return new MinimalPageManifestEntry(
                    page.PageId,
                    page.SourceIndex,
                    pageType,
                    !exclude,
                    Math.Clamp(classification.Confidence, 0, 1),
                    classification.Evidence);
            }).ToArray());
    }

    public async Task<TranslationBookContextDocument> ConsolidateAsync(
        MinimalPrecheckRequest request,
        MinimalPageManifestDocument manifest,
        IReadOnlyList<MinimalPrecheckScanResult> scans,
        CancellationToken cancellationToken)
    {
        var response = await consolidationProvider.GenerateAsync(new ModelRequest(
            request.Options.ConsolidationModel,
            [
                new ModelMessage(ModelRole.System, MinimalPrecheckPromptCatalog.ConsolidationSystem(request.Options.TargetLanguage)),
                new ModelMessage(ModelRole.User, MinimalPrecheckPromptCatalog.ConsolidationUser(request, manifest, scans))
            ],
            new JsonSchemaOutputFormat("minimal_translation_book_context", MinimalPrecheckSchemas.TranslationContext()),
            new ModelGenerationOptions { MaxOutputTokens = 30_000, TotalTimeout = TimeSpan.FromMinutes(20) },
            new Dictionary<string, string> { ["run_id"] = request.Options.RunId, ["stage"] = "translation_context" }), cancellationToken).ConfigureAwait(false);
        var root = ParseCompleted(response, "translation context");
        return new TranslationBookContextDocument(
            "1.0.0",
            request.Options.RunId,
            request.BookId,
            request.Options.TargetLanguage,
            Required(root, "synopsis"),
            Required(root, "translationStyle"),
            Required(root, "characters"),
            Required(root, "glossary"),
            Required(root, "continuityFacts"),
            Required(root, "unresolvedQuestions"));
    }

    public static BookManifestDocument CreateBookManifest(
        MinimalPrecheckRequest request,
        MinimalPageManifestDocument pageManifest) => new(
        "1.0",
        request.BookId,
        pageManifest.Pages.Select(page => new BookManifestPage(
            page.PageId,
            page.Order,
            null,
            $"page-{page.Order:0000}",
            page.PageType,
            page.IncludeInWorkflow)).ToArray());

    public static GlobalSemanticMergeResult CreateSemanticMerge(
        TranslationBookContextDocument context)
    {
        var empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
        var storyBible = JsonSerializer.SerializeToElement(new
        {
            schema_version = context.SchemaVersion,
            book_id = context.BookId,
            target = context.TargetLanguage,
            synopsis = context.Synopsis,
            translation_style = context.TranslationStyle,
            continuity_facts = context.ContinuityFacts,
            unresolved_questions = context.UnresolvedQuestions
        });
        return new GlobalSemanticMergeResult(
            context.TargetLanguage,
            empty,
            NormalizeCharacters(context.Characters),
            empty,
            NormalizeGlossary(context.Glossary),
            storyBible);
    }

    private async Task<MinimalPrecheckScanResult> ScanBatchAsync(
        MinimalPrecheckRequest request,
        MinimalPrecheckScanBatch batch,
        CancellationToken cancellationToken)
    {
        var pages = request.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var pageIds = batch.ContextPagesBefore.Concat(batch.OwnerPages).Concat(batch.ContextPagesAfter).Distinct(StringComparer.Ordinal).ToArray();
        var content = new List<ModelContent>
        {
            new TextContent(MinimalPrecheckPromptCatalog.ScanUser(batch, pages))
        };
        foreach (var pageId in pageIds)
        {
            var page = pages[pageId];
            var bytes = await request.PageAssets.GetPageAsync(pageId, ImageQuality.Original, cancellationToken).ConfigureAwait(false);
            var role = batch.OwnerPages.Contains(pageId, StringComparer.Ordinal) ? "OWNER" : "CONTEXT";
            content.Add(new TextContent($"{role} PAGE {page.PageId}; order {page.SourceIndex}; file {page.FileName}"));
            content.Add(new ImageContent(bytes, page.MediaType, request.Options.ImageDetail));
        }

        var response = await scanProvider.GenerateAsync(new ModelRequest(
            request.Options.ScanModel,
            [
                new ModelMessage(ModelRole.System, MinimalPrecheckPromptCatalog.ScanSystem(request.Options.TargetLanguage)),
                new ModelMessage(ModelRole.User, content)
            ],
            new JsonSchemaOutputFormat("minimal_precheck_scan", MinimalPrecheckSchemas.Scan()),
            new ModelGenerationOptions { MaxOutputTokens = 16_000, TotalTimeout = TimeSpan.FromMinutes(15) },
            new Dictionary<string, string> { ["run_id"] = request.Options.RunId, ["batch_id"] = batch.BatchId }), cancellationToken).ConfigureAwait(false);
        var root = ParseCompleted(response, batch.BatchId);
        var classifications = JsonSerializer.Deserialize<MinimalPageClassification[]>(
            Required(root, "pages").GetRawText(),
            WebJsonOptions) ?? [];
        var owners = batch.OwnerPages.ToHashSet(StringComparer.Ordinal);
        return new MinimalPrecheckScanResult(
            "1.0.0",
            batch.BatchId,
            batch.OwnerPages,
            classifications.Where(page => owners.Contains(page.PageId)).ToArray(),
            Required(root, "contextDelta"),
            JsonSerializer.Deserialize<string[]>(Required(root, "unresolvedQuestions").GetRawText()) ?? []);
    }

    private static JsonElement ParseCompleted(ModelResponse response, string stage)
    {
        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new PrecheckAnalysisException($"The model refused {stage}: {response.Refusal}");
        }
        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new PrecheckAnalysisException($"The model did not complete {stage}: {response.Status}.");
        }
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json)) throw new PrecheckAnalysisException($"The model returned no JSON for {stage}.");
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException($"The model output for {stage} was not valid JSON.", exception);
        }
    }

    private static JsonElement Required(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value)
            ? value.Clone()
            : throw new PrecheckAnalysisException($"The model output is missing '{propertyName}'.");

    private static string NormalizePageType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "story" => "story",
        "bonus_story" => "bonus_story",
        "cover" or "front_cover" or "back_cover" => "cover",
        "title_page" or "chapter_title" => "title_page",
        "table_of_contents" or "toc" => "table_of_contents",
        "credits" or "copyright" or "afterword" => "credits",
        "advertisement" or "ad" => "advertisement",
        "blank" => "blank",
        "illustration" => "illustration",
        _ => "unknown"
    };

    private static JsonElement NormalizeCharacters(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Array) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        var items = source.EnumerateArray().Select((item, index) => new
        {
            character_id = GetString(item, "characterId") ?? $"character_{index + 1:000}",
            original_name = GetString(item, "originalName") ?? string.Empty,
            translated_name = GetString(item, "translatedName") ?? GetString(item, "originalName") ?? string.Empty,
            aliases_original = GetStringArray(item, "aliases"),
            role = GetString(item, "role") ?? string.Empty,
            speech_rules = GetStringArray(item, "speechRules"),
            relationships = GetStringArray(item, "relationships"),
            valid_from_order = GetInt(item, "validFromOrder") ?? 1,
            valid_to_order = GetNullableInt(item, "validToOrder"),
            reveal_sensitive = GetBool(item, "revealSensitive"),
            confidence = GetDouble(item, "confidence"),
            evidence = Evidence(item, "evidencePages")
        });
        return JsonSerializer.SerializeToElement(items);
    }

    private static JsonElement NormalizeGlossary(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Array) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        var items = source.EnumerateArray().Select(item => new
        {
            source = GetString(item, "source") ?? string.Empty,
            translation = GetString(item, "translation") ?? GetString(item, "source") ?? string.Empty,
            category = GetString(item, "category") ?? string.Empty,
            usage_rule = GetString(item, "usageRule") ?? string.Empty,
            valid_from_order = GetInt(item, "validFromOrder") ?? 1,
            valid_to_order = GetNullableInt(item, "validToOrder"),
            reveal_sensitive = GetBool(item, "revealSensitive"),
            confidence = GetDouble(item, "confidence"),
            evidence = Evidence(item, "evidencePages"),
            locked = false
        });
        return JsonSerializer.SerializeToElement(items);
    }

    private static object[] Evidence(JsonElement item, string propertyName) =>
        GetStringArray(item, propertyName).Select(pageId => new { page_id = pageId, region_id = string.Empty, quote = string.Empty }).Cast<object>().ToArray();

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] GetStringArray(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString() ?? string.Empty).ToArray()
            : [];

    private static int? GetInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static int? GetNullableInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static double GetDouble(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? Math.Clamp(number, 0, 1) : 0;

    private static void Validate(MinimalPrecheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(request));
        if (request.Options.PagesPerScan <= 0 || request.Options.MaxConcurrentScans <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Scan size and concurrency must be positive.");
        }
    }
}
