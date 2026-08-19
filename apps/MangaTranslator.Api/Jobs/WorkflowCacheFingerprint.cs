using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.Jobs;

internal sealed record WorkflowStageFingerprint(
    string Stage,
    string ContractVersion,
    string Sha256,
    IReadOnlyDictionary<string, string> Inputs)
{
    public static WorkflowStageFingerprint Create(
        string stage,
        string contractVersion,
        IEnumerable<KeyValuePair<string, string>> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentNullException.ThrowIfNull(inputs);
        var ordered = inputs
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stage,
            contractVersion,
            inputs = ordered
        });
        return new WorkflowStageFingerprint(
            stage,
            contractVersion,
            Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant(),
            ordered);
    }

    public bool Matches(WorkflowStageFingerprint? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(Stage, other.Stage)
        && StringComparer.Ordinal.Equals(ContractVersion, other.ContractVersion)
        && StringComparer.Ordinal.Equals(Sha256, other.Sha256);

    public bool HasValidDigest() =>
        !string.IsNullOrWhiteSpace(Stage)
        && !string.IsNullOrWhiteSpace(ContractVersion)
        && !string.IsNullOrWhiteSpace(Sha256)
        && Inputs is not null
        && Matches(Create(Stage, ContractVersion, Inputs));
}

internal static class WorkflowCacheFingerprint
{
    public const string DetectionContractVersion = "local-detection-cleaning-v3";
    public const string LegacyDetectionContractVersion = "local-detection-cleaning-v2";
    public const string TranslationContractVersion = "dialogue-layout-v1";
    public const string PageContractVersion = "offline-page-v2-layout-clipped-cleaning";
    public const string CleanerAlgorithmVersion = "opencv-text-cleaner-v1";
    public const string CleanerOptionsVersion = "default-options-2026-08-12";
    public const string OfflineRendererVersion = "managed-lettering-v1";

    public static WorkflowStageFingerprint Detection(
        string sourceSha256,
        string detectorModelSha256,
        string maskSha256,
        string cleanSha256,
        string dialoguePageId,
        TextMaskDetectorDescriptor detector,
        string regionsSha256) =>
        WorkflowStageFingerprint.Create(
            "detection-cleaning",
            DetectionContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_sha256"] = sourceSha256,
                ["dialogue_page_id"] = dialoguePageId,
                ["detector_model_sha256"] = detectorModelSha256,
                ["mask_sha256"] = maskSha256,
                ["clean_sha256"] = cleanSha256,
                ["detector_id"] = detector.DetectorId,
                ["detector_algorithm"] = detector.AlgorithmVersion,
                ["detector_options_sha256"] = detector.OptionsFingerprint,
                ["cleaner_algorithm"] = CleanerAlgorithmVersion,
                ["cleaner_options"] = CleanerOptionsVersion,
                ["regions_sha256"] = regionsSha256
            });

    public static WorkflowStageFingerprint PipelineDetection(
        string sourceSha256,
        string detectionSha256,
        string maskSha256,
        string cleanSha256,
        string dialoguePageId,
        string regionsSha256) =>
        WorkflowStageFingerprint.Create(
            "detection-cleaning",
            DetectionContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_sha256"] = sourceSha256,
                ["dialogue_page_id"] = dialoguePageId,
                ["detector_model_sha256"] = "pipeline-artifact",
                ["mask_sha256"] = maskSha256,
                ["clean_sha256"] = cleanSha256,
                ["detector_id"] = "pipeline-artifact",
                ["detector_algorithm"] = "pipeline-detection-v1",
                ["detector_options_sha256"] = "pipeline-artifact",
                ["cleaner_algorithm"] = CleanerAlgorithmVersion,
                ["cleaner_options"] = CleanerOptionsVersion,
                ["pipeline_detection_sha256"] = detectionSha256,
                ["pipeline_mask_sha256"] = maskSha256,
                ["pipeline_clean_sha256"] = cleanSha256,
                ["regions_sha256"] = regionsSha256
            });

    public static WorkflowStageFingerprint Translation(
        string workflowMode,
        string sourceSha256,
        string detectionSha256,
        string targetLanguage,
        string providerId,
        string endpoint,
        string modelId,
        string promptProfile,
        string promptContractVersion,
        string schemaContractVersion,
        string fontSetSha256,
        string knowledgeSha256,
        string guidanceSha256,
        string pageIdentitySha256) =>
        WorkflowStageFingerprint.Create(
            "translation-layout",
            TranslationContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_mode"] = workflowMode,
                ["source_sha256"] = sourceSha256,
                ["detection_sha256"] = detectionSha256,
                ["target_language"] = targetLanguage,
                ["provider_id"] = providerId,
                ["endpoint"] = endpoint,
                ["model_id"] = modelId,
                ["prompt_profile"] = promptProfile,
                ["prompt_contract"] = promptContractVersion,
                ["schema_contract"] = schemaContractVersion,
                ["font_set_sha256"] = fontSetSha256,
                ["knowledge_sha256"] = knowledgeSha256,
                ["guidance_sha256"] = guidanceSha256,
                ["page_identity_sha256"] = pageIdentitySha256
            });

    public static WorkflowStageFingerprint Page(
        WorkflowStageFingerprint detection,
        WorkflowStageFingerprint translation) =>
        WorkflowStageFingerprint.Create(
            "page-checkpoint",
            PageContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detection_sha256"] = detection.Sha256,
                ["translation_sha256"] = translation.Sha256,
                ["renderer"] = OfflineRendererVersion
            });

    public static bool MatchesValid(
        WorkflowStageFingerprint expected,
        WorkflowStageFingerprint? actual) =>
        actual is not null
        && actual.HasValidDigest()
        && expected.Matches(actual);

    public static bool DetectionMatches(
        WorkflowStageFingerprint? fingerprint,
        string sourceSha256,
        string dialoguePageId,
        string? currentDetectorModelSha256,
        string currentMaskSha256,
        string currentCleanSha256,
        bool pipelineArtifact,
        TextMaskDetectorDescriptor currentDetector)
    {
        if (fingerprint is null
            || !fingerprint.HasValidDigest()
            || !StringComparer.Ordinal.Equals(fingerprint.Stage, "detection-cleaning")
            || (!StringComparer.Ordinal.Equals(fingerprint.ContractVersion, DetectionContractVersion)
                && !StringComparer.Ordinal.Equals(fingerprint.ContractVersion, LegacyDetectionContractVersion))
            || !InputEquals(fingerprint, "source_sha256", sourceSha256)
            || !InputEquals(fingerprint, "dialogue_page_id", dialoguePageId)
            || !InputEquals(fingerprint, "mask_sha256", currentMaskSha256)
            || !InputEquals(fingerprint, "clean_sha256", currentCleanSha256)
            || !InputEquals(fingerprint, "cleaner_algorithm", CleanerAlgorithmVersion)
            || !InputEquals(fingerprint, "cleaner_options", CleanerOptionsVersion)
            || (StringComparer.Ordinal.Equals(
                    fingerprint.ContractVersion,
                    DetectionContractVersion)
                && !InputPresent(fingerprint, "regions_sha256")))
        {
            return false;
        }

        if (pipelineArtifact)
        {
            return InputEquals(fingerprint, "detector_id", "pipeline-artifact")
                   && InputEquals(fingerprint, "detector_algorithm", "pipeline-detection-v1")
                   && InputPresent(fingerprint, "pipeline_detection_sha256")
                   && InputPresent(fingerprint, "pipeline_mask_sha256")
                   && InputPresent(fingerprint, "pipeline_clean_sha256");
        }

        return InputEquals(fingerprint, "detector_id", currentDetector.DetectorId)
               && InputEquals(fingerprint, "detector_algorithm", currentDetector.AlgorithmVersion)
               && InputEquals(fingerprint, "detector_options_sha256", currentDetector.OptionsFingerprint)
               && (string.IsNullOrWhiteSpace(currentDetectorModelSha256)
                   || InputEquals(fingerprint, "detector_model_sha256", currentDetectorModelSha256));
    }

    public static string HashBytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string HashText(string? value) =>
        HashBytes(Encoding.UTF8.GetBytes(value ?? string.Empty));

    public static string HashJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return HashBytes(stream.ToArray());
    }

    public static async Task<bool> PipelineArtifactMatchesAsync(
        PipelinePageArtifact artifact,
        string sourceSha256,
        CancellationToken cancellationToken)
    {
        if (!artifact.HasCompleteContentIdentity
            || string.IsNullOrWhiteSpace(artifact.DetectionJsonPath)
            || !StringComparer.OrdinalIgnoreCase.Equals(artifact.SourceContentHash, sourceSha256))
        {
            return false;
        }

        return await FileMatchesAsync(
                   artifact.DetectionJsonPath,
                   artifact.DetectionContentHash!,
                   cancellationToken)
               && await FileMatchesAsync(
                   artifact.MaskImagePath,
                   artifact.MaskContentHash!,
                   cancellationToken)
               && await FileMatchesAsync(
                   artifact.CleanImagePath,
                   artifact.CleanContentHash!,
                   cancellationToken);
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!PlatformIoPath.FileExists(path)) return false;
        await using var stream = PlatformIoPath.OpenRead(
            path,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        return StringComparer.OrdinalIgnoreCase.Equals(actual, expectedSha256);
    }

    private static bool InputEquals(
        WorkflowStageFingerprint fingerprint,
        string key,
        string expected) =>
        fingerprint.Inputs.TryGetValue(key, out var value)
        && StringComparer.OrdinalIgnoreCase.Equals(value, expected);

    private static bool InputPresent(
        WorkflowStageFingerprint fingerprint,
        string key) =>
        fingerprint.Inputs.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
