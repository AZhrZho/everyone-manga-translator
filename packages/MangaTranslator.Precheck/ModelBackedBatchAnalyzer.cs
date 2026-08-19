using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

public sealed class ModelBackedBatchAnalyzer : IBatchAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IModelProvider _modelProvider;
    private readonly IPrecheckProgressReporter _progress;

    public ModelBackedBatchAnalyzer(
        IModelProvider modelProvider,
        IPrecheckProgressReporter? progress = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _progress = progress ?? NullPrecheckProgressReporter.Instance;
    }

    public async Task<BatchObservationDocument> AnalyzeAsync(
        PrecheckRequest request,
        BatchPlan batch,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(thumbnailPrecheck);

        var stopwatch = Stopwatch.StartNew();
        _progress.Report(new PrecheckProgress(
            "batch_analysis",
            "batch_started",
            batch.BatchId,
            Detail: $"owner pages: {string.Join(",", batch.OwnerPages)}; "
                + $"context: {batch.ContextPagesBefore.Count + batch.ContextPagesAfter.Count}"));

        var pagesById = request.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var pageIds = batch.ContextPagesBefore
            .Concat(batch.OwnerPages)
            .Concat(batch.ContextPagesAfter)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var content = new List<ModelContent>
        {
            new TextContent(PrecheckPromptCatalog.BatchUser(batch, thumbnailPrecheck))
        };

        foreach (var pageId in pageIds)
        {
            if (!pagesById.TryGetValue(pageId, out var page))
            {
                continue;
            }

            var quality = batch.OwnerPages.Contains(pageId, StringComparer.Ordinal)
                ? ImageQuality.Original
                : ImageQuality.Standard;
            var bytes = await request.PageAssets
                .GetPageAsync(pageId, quality, cancellationToken)
                .ConfigureAwait(false);
            content.Add(new TextContent(
                $"PAGE {page.PageId} (source index {page.SourceIndex}); "
                + (batch.OwnerPages.Contains(pageId, StringComparer.Ordinal) ? "OWNER" : "CONTEXT")));
            content.Add(new ImageContent(bytes, page.MediaType, request.Options.BatchImageDetail));
        }

        ModelResponse response;
        try
        {
            _progress.Report(new PrecheckProgress(
                "batch_analysis",
                "model_call_started",
                batch.BatchId,
                Detail: $"{pageIds.Length} images"));
            response = await _modelProvider.GenerateAsync(new ModelRequest(
                request.Options.AnalysisModel ?? request.Options.Model,
                [
                    new ModelMessage(
                        ModelRole.System,
                        PrecheckPromptCatalog.BatchSystem(request.Options.TargetLanguage)),
                    new ModelMessage(ModelRole.User, content)
                ],
                new JsonSchemaOutputFormat("precheck_batch", PrecheckSchemas.BatchAnalysis()),
                new ModelGenerationOptions
                {
                    MaxOutputTokens = 12_000,
                    TotalTimeout = TimeSpan.FromMinutes(15)
                },
                new Dictionary<string, string>
                {
                    ["run_id"] = request.Options.RunId,
                    ["batch_id"] = batch.BatchId
                }), cancellationToken).ConfigureAwait(false);
            _progress.Report(new PrecheckProgress(
                "batch_analysis",
                "model_call_completed",
                batch.BatchId,
                Detail: response.Usage is null
                    ? null
                    : $"tokens: {response.Usage.TotalTokens}",
                Elapsed: stopwatch.Elapsed));
        }
        catch (JsonException exception)
        {
            _progress.Report(new PrecheckProgress(
                "batch_analysis",
                "model_call_failed",
                batch.BatchId,
                Detail: exception.Message,
                Elapsed: stopwatch.Elapsed,
                ErrorCode: "invalid_json"));
            throw new PrecheckAnalysisException(
                $"The model output for batch {batch.BatchId} was not valid JSON.",
                exception);
        }
        catch (ModelProviderException exception)
        {
            _progress.Report(new PrecheckProgress(
                "batch_analysis",
                "model_call_failed",
                batch.BatchId,
                Detail: exception.Error.Message,
                Elapsed: stopwatch.Elapsed,
                ErrorCode: exception.Error.Code));
            throw new PrecheckAnalysisException(
                $"The model provider failed batch {batch.BatchId} "
                + $"[{exception.Error.Code}]: {exception.Error.Message} "
                + FormatProviderMetadata(exception.Error),
                exception);
        }

        EnsureCompleted(response, $"batch {batch.BatchId}");
        var root = GetRoot(response);
        var warnings = DeserializeWarnings(root, batch.BatchId);
        var result = new BatchObservationDocument(
            "1.0.0",
            batch.BatchId,
            batch.OwnerPages,
            RequiredProperty(root, "pageObservations"),
            RequiredProperty(root, "relationVerifications"),
            RequiredProperty(root, "artifactDeltas"),
            RequiredProperty(root, "processingHints"),
            RequiredProperty(root, "uncertainties"),
            warnings);
        _progress.Report(new PrecheckProgress(
            "batch_analysis",
            "batch_completed",
            batch.BatchId,
            Detail: $"{result.Observations.GetArrayLength()} observations; {warnings.Length} warnings",
            Elapsed: stopwatch.Elapsed));
        return result;
    }

    private static JsonElement GetRoot(ModelResponse response)
    {
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PrecheckAnalysisException("The model returned no batch JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = JsonNode.Parse(document.RootElement.GetRawText())
                ?? throw new PrecheckAnalysisException("The batch JSON root is null.");
            NormalizeAutomationLanguage(root);
            using var normalized = JsonDocument.Parse(root.ToJsonString());
            return normalized.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException(
                "The model output did not match the batch schema.",
                exception);
        }
    }

    private static JsonElement RequiredProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value))
        {
            return value.Clone();
        }

        throw new PrecheckAnalysisException(
            $"The batch output is missing required property '{propertyName}'.");
    }

    private static PrecheckWarning[] DeserializeWarnings(
        JsonElement root,
        string batchId)
    {
        var warnings = RequiredProperty(root, "warnings");
        return JsonSerializer.Deserialize<List<WarningModel>>(warnings.GetRawText(), JsonOptions)?
            .Select(warning => new PrecheckWarning(
                warning.Code,
                warning.Message,
                warning.PageId,
                warning.BatchId ?? batchId))
            .ToArray()
            ?? [];
    }

    private static void NormalizeAutomationLanguage(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var normalized = text
                .Replace("human review", "automatic multimodal review", StringComparison.OrdinalIgnoreCase)
                .Replace("manual review", "automatic multimodal review", StringComparison.OrdinalIgnoreCase)
                .Replace("editorial review", "automatic policy validation", StringComparison.OrdinalIgnoreCase)
                .Replace("editorial style guide", "automatic translation policy", StringComparison.OrdinalIgnoreCase)
                .Replace("operator review", "automatic validation", StringComparison.OrdinalIgnoreCase);
            if (!StringComparer.Ordinal.Equals(text, normalized))
            {
                value.ReplaceWith(normalized);
            }

            return;
        }

        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Value is not null)
                {
                    NormalizeAutomationLanguage(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray.ToArray())
            {
                if (item is not null)
                {
                    NormalizeAutomationLanguage(item);
                }
            }
        }
    }

    private static void EnsureCompleted(ModelResponse response, string stage)
    {
        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new PrecheckAnalysisException($"The model refused {stage}: {response.Refusal}");
        }

        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new PrecheckAnalysisException($"The model did not complete {stage}: {response.Status}.");
        }
    }

    private static string FormatProviderMetadata(ModelProviderError error)
    {
        return error.ProviderMetadata is { } metadata
            ? $"Metadata: {metadata.GetRawText()}"
            : string.Empty;
    }

    private sealed record WarningModel(
        string Code,
        string Message,
        string? PageId,
        string? BatchId);
}
