using System.Text.Json.Serialization;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.BrowserInference;

public static class BrowserInferenceContractVersions
{
    public const string DetectionUpload = "1.0";
}

[JsonConverter(typeof(JsonStringEnumConverter<BrowserInferenceBackend>))]
public enum BrowserInferenceBackend
{
    [JsonStringEnumMemberName("webgpu")]
    WebGpu,
    [JsonStringEnumMemberName("wasm")]
    Wasm
}

public sealed record BrowserDetectionPoint(
    int X,
    int Y);

public sealed record BrowserDetectionRegion(
    int X0,
    int Y0,
    int X1,
    int Y1,
    DetectedTextOrientation Orientation,
    int FontSize,
    float Confidence,
    IReadOnlyList<BrowserDetectionPoint> Polygon);

public sealed record BrowserDetectionUploadMetadata(
    string SchemaVersion,
    string DetectorId,
    string ModelSha256,
    int Width,
    int Height,
    BrowserInferenceBackend Backend,
    string RuntimeVersion,
    IReadOnlyList<BrowserDetectionRegion> Regions,
    string SourceSha256,
    string AlgorithmVersion,
    string OptionsFingerprint);

public sealed record BrowserDetectionUploadResponse(
    string DetectorId,
    string DetectorCacheKey,
    string ModelSha256,
    string SourceSha256,
    string AlgorithmVersion,
    string OptionsFingerprint,
    string MaskAssetKey,
    string CleanAssetKey,
    int RegionCount,
    string FingerprintSha256,
    string Provenance);

internal sealed class BrowserDetectionValidationException(string message) : Exception(message);

internal sealed class BrowserDetectionConflictException(string message) : Exception(message);
