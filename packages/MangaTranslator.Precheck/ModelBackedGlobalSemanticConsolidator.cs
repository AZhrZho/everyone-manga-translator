using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

/// <summary>
/// Performs the final complete-book semantic merge. Batch-local merge keys are included
/// only as evidence and are never used by this component as final identities.
/// </summary>
public sealed class ModelBackedGlobalSemanticConsolidator : IGlobalSemanticConsolidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly ArtifactDefinition[] ArtifactDefinitions =
    [
        new(
            "chapters",
            "chapters",
            "chapters",
            PrecheckFinalArtifactSchemas.Chapters),
        new(
            "characters",
            "characters",
            "characters",
            PrecheckFinalArtifactSchemas.Characters),
        new(
            "cultural_localizations",
            "culturalLocalizations",
            "culturalLocalizations",
            PrecheckFinalArtifactSchemas.CulturalLocalizations),
        new(
            "glossary",
            "glossary",
            "glossary",
            PrecheckFinalArtifactSchemas.Glossary)
    ];

    private readonly IModelProvider _modelProvider;
    private readonly IPrecheckProgressReporter _progress;
    private readonly IGlobalSemanticArtifactStore? _artifactStore;

    public ModelBackedGlobalSemanticConsolidator(
        IModelProvider modelProvider,
        IPrecheckProgressReporter? progress = null,
        IGlobalSemanticArtifactStore? artifactStore = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _progress = progress ?? NullPrecheckProgressReporter.Instance;
        _artifactStore = artifactStore;
    }

    public async Task<GlobalSemanticMergeResult> ConsolidateAsync(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        BatchPlanDocument batchPlan,
        IReadOnlyList<BatchObservationDocument> observations,
        AutoReviewResult automaticReview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(thumbnailPrecheck);
        ArgumentNullException.ThrowIfNull(batchPlan);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(automaticReview);

        if (string.IsNullOrWhiteSpace(request.Options.TargetLanguage))
        {
            throw new PrecheckAnalysisException("A target language is required for semantic consolidation.");
        }

        var maximumConcurrency = Math.Max(1, request.Options.MaxConcurrentSemanticMergeCalls);
        using var semaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        _progress.Report(new PrecheckProgress(
            "global_semantic_merge",
            "artifact_queue_created",
            Total: ArtifactDefinitions.Length,
            Detail: $"target {request.Options.TargetLanguage}; concurrency {maximumConcurrency}"));

        var restoredOutputs = new List<ArtifactOutput>(ArtifactDefinitions.Length);
        foreach (var definition in ArtifactDefinitions)
        {
            var restored = await TryLoadArtifactAsync(
                request.Options.RunId,
                definition.OutputName,
                JsonValueKind.Array,
                cancellationToken).ConfigureAwait(false);
            if (restored is not null)
            {
                restoredOutputs.Add(new ArtifactOutput(definition.OutputName, restored.Value));
                _progress.Report(new PrecheckProgress(
                    "global_semantic_merge",
                    "artifact_restored",
                    definition.OutputName,
                    Detail: $"{restored.Value.GetArrayLength()} persisted items"));
            }
        }

        var restoredNames = restoredOutputs
            .Select(output => output.Name)
            .ToHashSet(StringComparer.Ordinal);
        var artifactTasks = ArtifactDefinitions
            .Where(definition => !restoredNames.Contains(definition.OutputName))
            .Select((definition, index) => ConsolidateArtifactWithLimitAsync(
                request,
                thumbnailPrecheck,
                observations,
                automaticReview,
                definition,
                index,
                semaphore,
                cancellationToken))
            .ToArray();
        var generatedOutputs = await Task.WhenAll(artifactTasks).ConfigureAwait(false);
        var artifactOutputs = restoredOutputs.Concat(generatedOutputs).ToArray();
        var byName = artifactOutputs.ToDictionary(
            output => output.Name,
            output => output.Items,
            StringComparer.Ordinal);

        _progress.Report(new PrecheckProgress(
            "global_semantic_merge",
            "story_bible_started",
            "story_bible",
            Detail: "using four authoritative semantic artifacts"));
        var storyStopwatch = Stopwatch.StartNew();
        var storyBible = await TryLoadArtifactAsync(
            request.Options.RunId,
            "story_bible",
            JsonValueKind.Object,
            cancellationToken).ConfigureAwait(false);
        if (storyBible is null)
        {
            storyBible = await ConsolidateStoryBibleAsync(
                request,
                thumbnailPrecheck,
                batchPlan,
                observations,
                automaticReview,
                byName["chapters"],
                byName["characters"],
                byName["cultural_localizations"],
                byName["glossary"],
                cancellationToken).ConfigureAwait(false);
            await SaveArtifactAsync(
                request.Options.RunId,
                "story_bible",
                storyBible.Value,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _progress.Report(new PrecheckProgress(
                "global_semantic_merge",
                "artifact_restored",
                "story_bible"));
        }

        _progress.Report(new PrecheckProgress(
            "global_semantic_merge",
            "story_bible_completed",
            "story_bible",
            Elapsed: storyStopwatch.Elapsed));

        return new GlobalSemanticMergeResult(
            request.Options.TargetLanguage,
            byName["chapters"],
            byName["characters"],
            byName["cultural_localizations"],
            byName["glossary"],
            storyBible.Value);
    }

    private async Task<ArtifactOutput> ConsolidateArtifactWithLimitAsync(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        IReadOnlyList<BatchObservationDocument> observations,
        AutoReviewResult automaticReview,
        ArtifactDefinition definition,
        int index,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _progress.Report(new PrecheckProgress(
                "global_semantic_merge",
                "artifact_started",
                definition.OutputName,
                index + 1,
                ArtifactDefinitions.Length));
            var inputJson = BuildArtifactEvidencePackage(
                request,
                thumbnailPrecheck,
                observations,
                automaticReview,
                definition);
            var response = await GenerateAsync(
                request,
                $"precheck_global_{definition.OutputName}",
                PrecheckPromptCatalog.SemanticArtifactSystem(
                    request.Options.TargetLanguage,
                    definition.OutputName),
                PrecheckPromptCatalog.SemanticArtifactUser(definition.OutputName, inputJson),
                definition.SchemaFactory(),
                30_000,
                new Dictionary<string, string>
                {
                    ["artifact"] = definition.OutputName
                },
                cancellationToken).ConfigureAwait(false);
            var items = ExtractArtifactItems(response, definition.OutputName);
            items = ForceNoHumanReview(items);
            await SaveArtifactAsync(
                request.Options.RunId,
                definition.OutputName,
                items,
                cancellationToken).ConfigureAwait(false);
            _progress.Report(new PrecheckProgress(
                "global_semantic_merge",
                "artifact_completed",
                definition.OutputName,
                index + 1,
                ArtifactDefinitions.Length,
                $"{items.GetArrayLength()} final items",
                stopwatch.Elapsed));
            return new ArtifactOutput(definition.OutputName, items);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _progress.Report(new PrecheckProgress(
                "global_semantic_merge",
                "artifact_failed",
                definition.OutputName,
                index + 1,
                ArtifactDefinitions.Length,
                exception.Message,
                stopwatch.Elapsed,
                GetErrorCode(exception)));
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async ValueTask<JsonElement?> TryLoadArtifactAsync(
        string runId,
        string artifactName,
        JsonValueKind expectedKind,
        CancellationToken cancellationToken)
    {
        if (_artifactStore is null)
        {
            return null;
        }

        var value = await _artifactStore
            .TryLoadAsync(runId, artifactName, cancellationToken)
            .ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind != expectedKind)
        {
            throw new PrecheckAnalysisException(
                $"Persisted global semantic artifact '{artifactName}' must be "
                + $"{expectedKind}, but was {value.Value.ValueKind}.");
        }

        return value.Value.Clone();
    }

    private ValueTask SaveArtifactAsync(
        string runId,
        string artifactName,
        JsonElement value,
        CancellationToken cancellationToken) =>
        _artifactStore is null
            ? ValueTask.CompletedTask
            : _artifactStore.SaveAsync(runId, artifactName, value, cancellationToken);

    private async Task<JsonElement> ConsolidateStoryBibleAsync(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        BatchPlanDocument batchPlan,
        IReadOnlyList<BatchObservationDocument> observations,
        AutoReviewResult automaticReview,
        JsonElement chapters,
        JsonElement characters,
        JsonElement culturalLocalizations,
        JsonElement glossary,
        CancellationToken cancellationToken)
    {
        var storyCandidates = observations
            .Select(observation => new
            {
                batchId = observation.BatchId,
                ownerPages = observation.OwnerPages,
                candidates = ReadArtifactCandidates(observation, "storyBible"),
                uncertainties = observation.Uncertainties
            })
            .ToArray();
        var package = new
        {
            bookId = request.BookId,
            targetLanguage = request.Options.TargetLanguage,
            pageManifest = request.Pages
                .OrderBy(page => page.SourceIndex)
                .Select(page => new { page.PageId, page.SourceIndex, page.FileName }),
            thumbnailGlobal = thumbnailPrecheck.Global,
            thumbnailPages = thumbnailPrecheck.Pages,
            batchPlan,
            storyCandidates,
            reviewedStoryAggregate = automaticReview.FinalMerge.StoryBible,
            automaticReview = new
            {
                automaticReview.ResolvedConflicts,
                automaticReview.UnresolvedConflicts
            },
            chapters,
            characters,
            glossary,
            culturalLocalizations
        };
        var inputJson = JsonSerializer.Serialize(package, JsonOptions);
        var response = await GenerateAsync(
            request,
            "precheck_global_story_bible",
            PrecheckPromptCatalog.StoryBibleSystem(request.Options.TargetLanguage),
            PrecheckPromptCatalog.StoryBibleUser(inputJson),
            PrecheckFinalArtifactSchemas.StoryBibleCore(),
            16_000,
            new Dictionary<string, string>
            {
                ["artifact"] = "story_bible"
            },
            cancellationToken).ConfigureAwait(false);
        var core = ParseResponseRoot(response, "story_bible");
        return AssembleStoryBible(
            request,
            core,
            chapters,
            characters,
            culturalLocalizations,
            glossary);
    }

    private async Task<ModelResponse> GenerateAsync(
        PrecheckRequest request,
        string schemaName,
        string systemPrompt,
        string userPrompt,
        JsonElement schema,
        int maxOutputTokens,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var requestMetadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        {
            ["run_id"] = request.Options.RunId,
            ["target_language"] = request.Options.TargetLanguage
        };

        ModelResponse response;
        try
        {
            response = await _modelProvider.GenerateAsync(new ModelRequest(
                request.Options.AnalysisModel ?? request.Options.Model,
                [
                    new ModelMessage(ModelRole.System, systemPrompt),
                    new ModelMessage(ModelRole.User, userPrompt)
                ],
                new JsonSchemaOutputFormat(schemaName, schema),
                new ModelGenerationOptions
                {
                    MaxOutputTokens = maxOutputTokens,
                    TotalTimeout = TimeSpan.FromMinutes(20)
                },
                requestMetadata), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException(
                $"The model output for {schemaName} was not valid JSON.",
                exception);
        }
        catch (ModelProviderException exception)
        {
            throw new PrecheckAnalysisException(
                $"The model provider failed {schemaName} "
                + $"[{exception.Error.Code}]: {exception.Error.Message}.",
                exception);
        }

        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new PrecheckAnalysisException(
                $"The model refused {schemaName}: {response.Refusal}");
        }

        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new PrecheckAnalysisException(
                $"The model did not complete {schemaName}: {response.Status}.");
        }

        return response;
    }

    private static string BuildArtifactEvidencePackage(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        IReadOnlyList<BatchObservationDocument> observations,
        AutoReviewResult automaticReview,
        ArtifactDefinition definition)
    {
        var batches = observations.Select(observation => new
        {
            batchId = observation.BatchId,
            ownerPages = observation.OwnerPages,
            candidates = ReadArtifactCandidates(observation, definition.InputPropertyName),
            supportingPageObservations = observation.Observations,
            uncertainties = observation.Uncertainties
        });
        var package = new
        {
            bookId = request.BookId,
            targetLanguage = request.Options.TargetLanguage,
            pageManifest = request.Pages
                .OrderBy(page => page.SourceIndex)
                .Select(page => new { page.PageId, page.SourceIndex, page.FileName }),
            thumbnailGlobal = thumbnailPrecheck.Global,
            thumbnailPages = thumbnailPrecheck.Pages,
            rawBatchEvidence = batches,
            reviewedPreliminaryAggregate = GetReviewedArtifact(
                automaticReview.FinalMerge,
                definition.MergeArtifactName),
            automaticReview = new
            {
                resolved = automaticReview.ResolvedConflicts
                    .Where(conflict => StringComparer.Ordinal.Equals(
                        conflict.Artifact,
                        definition.MergeArtifactName)),
                unresolvedAuto = automaticReview.UnresolvedConflicts
                    .Where(conflict => StringComparer.Ordinal.Equals(
                        conflict.Artifact,
                        definition.MergeArtifactName))
            }
        };
        return JsonSerializer.Serialize(package, JsonOptions);
    }

    private static JsonElement[] ReadArtifactCandidates(
        BatchObservationDocument observation,
        string propertyName)
    {
        if (observation.ArtifactDeltas.ValueKind != JsonValueKind.Object
            || !observation.ArtifactDeltas.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static JsonElement GetReviewedArtifact(
        PrecheckMergeResult merge,
        string artifactName) => artifactName switch
        {
            "chapters" => merge.Chapters,
            "characters" => merge.Characters,
            "culturalLocalizations" => merge.CulturalLocalizations,
            "glossary" => merge.Glossary,
            "storyBible" => merge.StoryBible,
            _ => throw new PrecheckAnalysisException(
                $"Unknown semantic merge artifact '{artifactName}'.")
        };

    private static JsonElement ExtractArtifactItems(ModelResponse response, string artifactName)
    {
        var root = ParseResponseRoot(response, artifactName);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new PrecheckAnalysisException(
                $"The global {artifactName} response must contain an items array.");
        }

        return items.Clone();
    }

    private static JsonElement ParseResponseRoot(ModelResponse response, string artifactName)
    {
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PrecheckAnalysisException(
                $"The model returned no global {artifactName} JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException(
                $"The global {artifactName} output was not valid JSON.",
                exception);
        }
    }

    private static JsonElement ForceNoHumanReview(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText())
            ?? throw new PrecheckAnalysisException("A final artifact JSON value was null.");
        ForceNoHumanReview(node);
        return SerializeNode(node);
    }

    private static void ForceNoHumanReview(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (StringComparer.Ordinal.Equals(property.Key, "needs_human_review"))
                {
                    jsonObject[property.Key] = false;
                }
                else if (property.Value is not null)
                {
                    ForceNoHumanReview(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                if (item is not null)
                {
                    ForceNoHumanReview(item);
                }
            }
        }
    }

    private static JsonElement AssembleStoryBible(
        PrecheckRequest request,
        JsonElement core,
        JsonElement chapters,
        JsonElement characters,
        JsonElement culturalLocalizations,
        JsonElement glossary)
    {
        if (core.ValueKind != JsonValueKind.Object)
        {
            throw new PrecheckAnalysisException("The story-bible core must be a JSON object.");
        }

        var coreNode = JsonNode.Parse(core.GetRawText()) as JsonObject
            ?? throw new PrecheckAnalysisException("The story-bible core was null.");
        var result = new JsonObject
        {
            ["schema_version"] = Take(coreNode, "schema_version", "1.0.0"),
            ["book_id"] = request.BookId,
            ["title_original"] = Take(coreNode, "title_original", string.Empty),
            ["title_translation"] = Take(coreNode, "title_translation", string.Empty),
            ["target"] = request.Options.TargetLanguage,
            ["genre"] = Take(coreNode, "genre", new JsonArray()),
            ["setting"] = Take(coreNode, "setting", string.Empty),
            ["worldview"] = Take(coreNode, "worldview", new JsonArray()),
            ["themes"] = Take(coreNode, "themes", new JsonArray()),
            ["overall_synopsis"] = Take(coreNode, "overall_synopsis", string.Empty),
            ["tone_arc"] = Take(coreNode, "tone_arc", string.Empty),
            ["timeline"] = Take(coreNode, "timeline", new JsonArray()),
            ["spoiler_policy"] = Take(coreNode, "spoiler_policy", string.Empty),
            ["reveal_sensitive_facts"] = Take(
                coreNode,
                "reveal_sensitive_facts",
                new JsonArray()),
            ["style_guide"] = Take(coreNode, "style_guide", new JsonObject()),
            ["typography"] = Take(coreNode, "typography", new JsonObject()),
            ["chapters"] = JsonNode.Parse(chapters.GetRawText()),
            ["characters"] = JsonNode.Parse(characters.GetRawText()),
            ["glossary"] = JsonNode.Parse(glossary.GetRawText()),
            ["cultural_localizations"] = JsonNode.Parse(culturalLocalizations.GetRawText()),
            ["unresolved_questions"] = Take(
                coreNode,
                "unresolved_questions",
                new JsonArray())
        };
        ForceNoHumanReview(result);
        return SerializeNode(result);
    }

    private static JsonNode Take(JsonObject source, string propertyName, JsonNode fallback)
    {
        if (source.Remove(propertyName, out var value) && value is not null)
        {
            return value;
        }

        return fallback;
    }

    private static JsonElement SerializeNode(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string GetErrorCode(Exception exception)
    {
        return exception is ModelProviderException providerException
            ? providerException.Error.Code
            : exception.InnerException is ModelProviderException innerProviderException
                ? innerProviderException.Error.Code
                : exception.GetType().Name;
    }

    private sealed record ArtifactDefinition(
        string OutputName,
        string InputPropertyName,
        string MergeArtifactName,
        Func<JsonElement> SchemaFactory);

    private sealed record ArtifactOutput(string Name, JsonElement Items);
}
