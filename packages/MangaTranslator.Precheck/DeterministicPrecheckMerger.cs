using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Precheck;

public sealed class DeterministicPrecheckMerger : IPrecheckMerger
{
    public PrecheckMergeResult Merge(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        BatchPlanDocument batchPlan,
        IReadOnlyList<BatchObservationDocument> observations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observations);

        var conflicts = new List<PrecheckConflict>();
        var chapters = MergeArtifact(
            request,
            observations,
            "chapters",
            "chapters",
            conflicts);
        var characters = MergeArtifact(
            request,
            observations,
            "characters",
            "characters",
            conflicts);
        var culturalLocalizations = MergeArtifact(
            request,
            observations,
            "culturalLocalizations",
            "items",
            conflicts);
        var glossary = MergeArtifact(
            request,
            observations,
            "glossary",
            "terms",
            conflicts);
        var storyBible = MergeArtifact(
            request,
            observations,
            "storyBible",
            "items",
            conflicts);
        var processingHints = MergeProcessingHints(request, observations);

        return new PrecheckMergeResult(
            chapters,
            characters,
            culturalLocalizations,
            glossary,
            storyBible,
            processingHints,
            conflicts);
    }

    private static JsonElement MergeArtifact(
        PrecheckRequest request,
        IReadOnlyList<BatchObservationDocument> observations,
        string artifactName,
        string collectionName,
        List<PrecheckConflict> conflicts)
    {
        var deltas = observations
            .SelectMany(observation => ReadArrayProperty(
                observation.ArtifactDeltas,
                artifactName)
                .Select(value => new ArtifactCandidate(
                    observation.BatchId,
                    value.Clone())))
            .ToArray();
        var mergedItems = new List<JsonElement>();

        foreach (var group in deltas
            .GroupBy(candidate => GetMergeKey(candidate.Value), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var candidates = group
                .GroupBy(candidate => candidate.Value.GetRawText(), StringComparer.Ordinal)
                .Select(candidateGroup => candidateGroup.First())
                .ToArray();

            if (candidates.Length == 1)
            {
                var candidate = candidates[0].Value.Clone();
                mergedItems.Add(candidate);
                var candidateConfidence = GetDouble(candidate, "confidence");
                if (candidateConfidence < request.Options.AutomaticReviewConfidenceThreshold)
                {
                    var candidatePages = ReadStringArray(candidate, "pages");
                    conflicts.Add(new PrecheckConflict(
                        $"conflict_{artifactName}_{conflicts.Count + 1:000}",
                        "low_confidence_artifact_claim",
                        artifactName,
                        group.Key,
                        candidatePages,
                        [candidate.GetRawText()],
                        "pending",
                        candidateConfidence,
                        GetString(candidate, "evidence")
                            ?? "The artifact claim is below the automatic confirmation threshold."));
                }

                continue;
            }

            var conflictId = $"conflict_{artifactName}_{conflicts.Count + 1:000}";
            var claims = candidates.Select(candidate => candidate.Value.GetRawText()).ToArray();
            var pages = candidates
                .SelectMany(candidate => ReadStringArray(candidate.Value, "pages"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var evidence = string.Join(
                " | ",
                candidates
                    .Select(candidate => GetString(candidate.Value, "evidence"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            var confidence = candidates
                .Select(candidate => GetDouble(candidate.Value, "confidence"))
                .DefaultIfEmpty(0)
                .Min();

            conflicts.Add(new PrecheckConflict(
                conflictId,
                "artifact_delta_conflict",
                artifactName,
                group.Key,
                pages,
                claims,
                "pending",
                confidence,
                evidence));

            mergedItems.Add(JsonSerializer.SerializeToElement(new
            {
                status = "conflicted",
                mergeKey = group.Key,
                candidates = candidates.Select(candidate => candidate.Value).ToArray()
            }));
        }

        return CreateDocument(
            request,
            collectionName,
            mergedItems,
            conflicts.Where(conflict => StringComparer.Ordinal.Equals(conflict.Artifact, artifactName)));
    }

    private static JsonElement MergeProcessingHints(
        PrecheckRequest request,
        IReadOnlyList<BatchObservationDocument> observations)
    {
        var hints = observations
            .SelectMany(observation => ReadArray(observation.ProcessingHints))
            .GroupBy(hint => GetString(hint, "pageId") ?? hint.GetRawText(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return CreateDocument(request, "pages", hints, []);
    }

    private static JsonElement CreateDocument(
        PrecheckRequest request,
        string collectionName,
        IReadOnlyList<JsonElement> items,
        IEnumerable<PrecheckConflict> conflicts)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["runId"] = request.Options.RunId,
            ["bookId"] = request.BookId
        };
        var itemArray = new JsonArray();
        foreach (var item in items)
        {
            itemArray.Add(JsonNode.Parse(item.GetRawText()));
        }

        root[collectionName] = itemArray;
        root["conflicts"] = JsonSerializer.SerializeToNode(conflicts.Select(conflict => new
        {
            conflict.ConflictId,
            conflict.MergeKey,
            conflict.Status
        }).ToArray());
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string GetMergeKey(JsonElement value)
    {
        var explicitKey = GetString(value, "mergeKey");
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        var source = value.GetRawText();
        return "auto_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static JsonElement[] ReadArrayProperty(
        JsonElement root,
        string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            ? ReadArray(value)
            : [];
    }

    private static JsonElement[] ReadArray(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [];
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : [];
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static double GetDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.TryGetDouble(out var result)
                ? result
                : 0;
    }

    private sealed record ArtifactCandidate(string BatchId, JsonElement Value);
}
