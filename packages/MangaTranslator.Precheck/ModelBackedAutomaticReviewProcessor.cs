using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

/// <summary>
/// Resolves deterministic merge conflicts without introducing a human approval gate.
/// A conflict is sent back to the model together with the relevant original pages and
/// the competing structured claims. If the model cannot make a sufficiently confident
/// decision within the configured attempt budget, the conflicting candidates are kept
/// and marked <c>unresolved_auto</c> with a conservative downstream strategy.
/// </summary>
public sealed class ModelBackedAutomaticReviewProcessor : IAutomaticReviewProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const double MinimumResolutionConfidence = 0.70;

    private readonly IModelProvider _modelProvider;
    private readonly IPrecheckProgressReporter _progress;

    public ModelBackedAutomaticReviewProcessor(
        IModelProvider modelProvider,
        IPrecheckProgressReporter? progress = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _progress = progress ?? NullPrecheckProgressReporter.Instance;
    }

    public async Task<AutoReviewResult> ReviewAsync(
        PrecheckRequest request,
        PrecheckMergeResult merge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(merge);

        var conflicts = merge.Conflicts
            .Where(conflict => !StringComparer.Ordinal.Equals(conflict.Status, "resolved"))
            .ToArray();
        if (conflicts.Length == 0)
        {
            return new AutoReviewResult(merge, [], [], []);
        }

        var current = merge;
        var reviewItems = new List<AutoReviewItem>(conflicts.Length);
        var resolved = new List<PrecheckConflict>();
        var unresolved = new List<PrecheckConflict>();
        var maximumAttempts = Math.Max(0, request.Options.MaxAutomaticReviewAttempts);
        _progress.Report(new PrecheckProgress(
            "automatic_review",
            "queue_created",
            Total: conflicts.Length,
            Detail: $"maximum {maximumAttempts} attempts per conflict"));

        for (var conflictIndex = 0; conflictIndex < conflicts.Length; conflictIndex++)
        {
            var conflict = conflicts[conflictIndex];
            cancellationToken.ThrowIfCancellationRequested();
            _progress.Report(new PrecheckProgress(
                "automatic_review",
                "conflict_started",
                conflict.ConflictId,
                conflictIndex + 1,
                conflicts.Length,
                $"{conflict.Artifact}/{conflict.MergeKey}; {conflict.Pages.Count} pages"));

            var claims = conflict.Claims.ToArray();
            var attempts = 0;
            var didResolve = false;
            var evidence = string.Empty;
            var strategy = GetConservativeStrategy(conflict);

            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                attempts = attempt;
                var attemptStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _progress.Report(new PrecheckProgress(
                    "automatic_review",
                    "attempt_started",
                    conflict.ConflictId,
                    attempt,
                    maximumAttempts));
                try
                {
                    var decision = await ReviewConflictAsync(
                        request,
                        conflict,
                        claims,
                        attempt,
                        maximumAttempts,
                        cancellationToken).ConfigureAwait(false);
                    evidence = decision.Evidence;

                    if (!IsResolved(decision, claims.Length))
                    {
                        strategy = string.IsNullOrWhiteSpace(decision.ConservativeStrategy)
                            ? strategy
                            : decision.ConservativeStrategy;
                        continue;
                    }

                    var selectedClaim = ParseClaim(claims[decision.SelectedClaimIndex!.Value]);
                    current = ApplyResolvedClaim(current, conflict, selectedClaim);
                    var resolvedConflict = conflict with
                    {
                        Status = "resolved",
                        Confidence = Math.Clamp(decision.Confidence, 0, 1),
                        Evidence = string.IsNullOrWhiteSpace(decision.Evidence)
                            ? conflict.Evidence
                            : decision.Evidence
                    };
                    current = UpdateConflict(current, conflict.ConflictId, resolvedConflict);
                    resolved.Add(resolvedConflict);
                    didResolve = true;
                    _progress.Report(new PrecheckProgress(
                        "automatic_review",
                        "attempt_resolved",
                        conflict.ConflictId,
                        attempt,
                        maximumAttempts,
                        $"selected claim {decision.SelectedClaimIndex.Value}; confidence {decision.Confidence:0.00}",
                        attemptStopwatch.Elapsed));
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is JsonException or ModelProviderException or PrecheckAnalysisException)
                {
                    evidence = exception.Message;
                    _progress.Report(new PrecheckProgress(
                        "automatic_review",
                        "attempt_failed",
                        conflict.ConflictId,
                        attempt,
                        maximumAttempts,
                        exception.Message,
                        attemptStopwatch.Elapsed,
                        exception is ModelProviderException providerException
                            ? providerException.Error.Code
                            : exception.GetType().Name));
                }
            }

            if (!didResolve)
            {
                var unresolvedConflict = conflict with
                {
                    Status = "unresolved_auto",
                    Evidence = string.IsNullOrWhiteSpace(evidence)
                        ? "The automatic reviewer could not establish a unique claim."
                        : evidence
                };
                current = ApplyConservativeStrategy(current, conflict, unresolvedConflict, strategy);
                unresolved.Add(unresolvedConflict);
                _progress.Report(new PrecheckProgress(
                    "automatic_review",
                    "conflict_unresolved_auto",
                    conflict.ConflictId,
                    conflictIndex + 1,
                    conflicts.Length,
                    strategy));
            }

            reviewItems.Add(new AutoReviewItem(
                $"auto_review_{reviewItems.Count + 1:000}",
                conflict.Type,
                conflict.Pages,
                didResolve ? "resolved" : "unresolved_auto",
                attempts,
                didResolve ? "none" : strategy));
        }

        return new AutoReviewResult(current, reviewItems, resolved, unresolved);
    }

    private async Task<ReviewDecision> ReviewConflictAsync(
        PrecheckRequest request,
        PrecheckConflict conflict,
        IReadOnlyList<string> claims,
        int attempt,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var pagesById = request.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var content = new List<ModelContent>
        {
            new TextContent(
                PrecheckPromptCatalog.ReviewUser(conflict, claims)
                + $"\nAutomatic attempt {attempt} of {maximumAttempts}.")
        };

        foreach (var pageId in conflict.Pages.Distinct(StringComparer.Ordinal))
        {
            if (!pagesById.TryGetValue(pageId, out var page))
            {
                continue;
            }

            var bytes = await request.PageAssets
                .GetPageAsync(pageId, ImageQuality.Original, cancellationToken)
                .ConfigureAwait(false);
            content.Add(new TextContent($"EVIDENCE PAGE {page.PageId} (source index {page.SourceIndex})"));
            content.Add(new ImageContent(bytes, page.MediaType, request.Options.BatchImageDetail));
        }

        var response = await _modelProvider.GenerateAsync(new ModelRequest(
            request.Options.AnalysisModel ?? request.Options.Model,
            [
                new ModelMessage(
                    ModelRole.System,
                    PrecheckPromptCatalog.ReviewSystem(request.Options.TargetLanguage)),
                new ModelMessage(ModelRole.User, content)
            ],
            new JsonSchemaOutputFormat("precheck_automatic_review", PrecheckSchemas.AutomaticReview()),
            new ModelGenerationOptions
            {
                MaxOutputTokens = 2_000,
                TotalTimeout = TimeSpan.FromMinutes(10)
            },
            new Dictionary<string, string>
            {
                ["run_id"] = request.Options.RunId,
                ["conflict_id"] = conflict.ConflictId,
                ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }), cancellationToken).ConfigureAwait(false);

        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new PrecheckAnalysisException(
                $"The model refused automatic review for {conflict.ConflictId}: {response.Refusal}");
        }

        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new PrecheckAnalysisException(
                $"The model did not complete automatic review for {conflict.ConflictId}: {response.Status}.");
        }

        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PrecheckAnalysisException(
                $"The model returned no automatic review JSON for {conflict.ConflictId}.");
        }

        return JsonSerializer.Deserialize<ReviewDecision>(json, JsonOptions)
            ?? throw new PrecheckAnalysisException(
                $"The model returned a null automatic review result for {conflict.ConflictId}.");
    }

    private static bool IsResolved(ReviewDecision decision, int claimCount)
    {
        var normalizedDecision = decision.Decision.Trim().ToLowerInvariant();
        return normalizedDecision is "resolved" or "confirmed" or "select"
            && decision.SelectedClaimIndex is >= 0
            && decision.SelectedClaimIndex.Value < claimCount
            && decision.Confidence >= MinimumResolutionConfidence;
    }

    private static JsonElement ParseClaim(string claim)
    {
        try
        {
            using var document = JsonDocument.Parse(claim);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrecheckAnalysisException("An artifact claim must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException("An artifact claim is not valid JSON.", exception);
        }
    }

    private static PrecheckMergeResult ApplyResolvedClaim(
        PrecheckMergeResult merge,
        PrecheckConflict conflict,
        JsonElement selectedClaim)
    {
        var document = ReplaceArtifactEntry(
            GetArtifactDocument(merge, conflict.Artifact),
            conflict.MergeKey,
            selectedClaim,
            unresolved: false,
            conservativeStrategy: null);
        return SetArtifactDocument(merge, conflict.Artifact, document);
    }

    private static PrecheckMergeResult ApplyConservativeStrategy(
        PrecheckMergeResult merge,
        PrecheckConflict originalConflict,
        PrecheckConflict unresolvedConflict,
        string strategy)
    {
        var document = ReplaceArtifactEntry(
            GetArtifactDocument(merge, originalConflict.Artifact),
            originalConflict.MergeKey,
            selectedClaim: null,
            unresolved: true,
            conservativeStrategy: strategy);
        var updated = SetArtifactDocument(merge, originalConflict.Artifact, document);
        return UpdateConflict(updated, originalConflict.ConflictId, unresolvedConflict);
    }

    private static PrecheckMergeResult UpdateConflict(
        PrecheckMergeResult merge,
        string conflictId,
        PrecheckConflict replacement)
    {
        var conflicts = merge.Conflicts
            .Select(conflict => StringComparer.Ordinal.Equals(conflict.ConflictId, conflictId)
                ? replacement
                : conflict)
            .ToArray();

        return merge with
        {
            Chapters = UpdateConflictMetadata(merge.Chapters, conflicts, "chapters"),
            Characters = UpdateConflictMetadata(merge.Characters, conflicts, "characters"),
            CulturalLocalizations = UpdateConflictMetadata(
                merge.CulturalLocalizations,
                conflicts,
                "culturalLocalizations"),
            Glossary = UpdateConflictMetadata(merge.Glossary, conflicts, "glossary"),
            StoryBible = UpdateConflictMetadata(merge.StoryBible, conflicts, "storyBible"),
            Conflicts = conflicts
        };
    }

    private static JsonElement ReplaceArtifactEntry(
        JsonElement document,
        string mergeKey,
        JsonElement? selectedClaim,
        bool unresolved,
        string? conservativeStrategy)
    {
        var root = JsonNode.Parse(document.GetRawText()) as JsonObject
            ?? throw new PrecheckAnalysisException("The merged artifact is not a JSON object.");
        var collectionName = root
            .Where(property => property.Value is JsonArray)
            .Select(property => property.Key)
            .FirstOrDefault(key => !StringComparer.Ordinal.Equals(key, "conflicts"));
        if (collectionName is null || root[collectionName] is not JsonArray items)
        {
            return document;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject item
                || !StringComparer.Ordinal.Equals(item["mergeKey"]?.GetValue<string>(), mergeKey))
            {
                continue;
            }

            if (unresolved)
            {
                item["status"] = "unresolved_auto";
                item["conservativeStrategy"] = conservativeStrategy;
            }
            else if (selectedClaim is { } claim)
            {
                var selected = JsonNode.Parse(claim.GetRawText()) as JsonObject
                    ?? throw new PrecheckAnalysisException("The selected artifact claim is not a JSON object.");
                selected["status"] = "confirmed";
                items[index] = selected;
            }

            break;
        }

        return SerializeNode(root);
    }

    private static JsonElement GetArtifactDocument(
        PrecheckMergeResult merge,
        string artifact) => artifact switch
        {
            "chapters" => merge.Chapters,
            "characters" => merge.Characters,
            "culturalLocalizations" => merge.CulturalLocalizations,
            "glossary" => merge.Glossary,
            "storyBible" => merge.StoryBible,
            _ => throw new PrecheckAnalysisException($"Unknown conflict artifact '{artifact}'.")
        };

    private static PrecheckMergeResult SetArtifactDocument(
        PrecheckMergeResult merge,
        string artifact,
        JsonElement document) => artifact switch
        {
            "chapters" => merge with { Chapters = document },
            "characters" => merge with { Characters = document },
            "culturalLocalizations" => merge with { CulturalLocalizations = document },
            "glossary" => merge with { Glossary = document },
            "storyBible" => merge with { StoryBible = document },
            _ => throw new PrecheckAnalysisException($"Unknown conflict artifact '{artifact}'.")
        };

    private static JsonElement UpdateConflictMetadata(
        JsonElement document,
        IReadOnlyList<PrecheckConflict> conflicts,
        string artifact)
    {
        var root = JsonNode.Parse(document.GetRawText()) as JsonObject
            ?? throw new PrecheckAnalysisException("The merged artifact is not a JSON object.");
        root["conflicts"] = JsonSerializer.SerializeToNode(
            conflicts
                .Where(conflict => StringComparer.Ordinal.Equals(conflict.Artifact, artifact))
                .Select(conflict => new
                {
                    conflictId = conflict.ConflictId,
                    mergeKey = conflict.MergeKey,
                    status = conflict.Status
                })
                .ToArray());
        return SerializeNode(root);
    }

    private static JsonElement SerializeNode(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string GetConservativeStrategy(PrecheckConflict conflict)
    {
        return conflict.Artifact switch
        {
            "chapters" => "Do not establish a hard chapter boundary; retain the candidate and adjacent-page context.",
            "characters" => "Keep identity candidates separate and require downstream speaker/character verification.",
            "culturalLocalizations" => "Retain competing cultural interpretations and defer wording selection to translation context.",
            "glossary" => "Retain all term variants without forcing a canonical translation.",
            "storyBible" => "Keep the fact unresolved and prevent it from becoming a hard narrative constraint.",
            _ => "Retain competing candidates and require downstream high-risk review."
        };
    }

    private sealed record ReviewDecision(
        string Decision,
        int? SelectedClaimIndex,
        double Confidence,
        string Evidence,
        string ConservativeStrategy);
}
