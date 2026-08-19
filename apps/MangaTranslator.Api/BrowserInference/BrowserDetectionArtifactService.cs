using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Cleaning;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Precheck;
using SkiaSharp;

namespace MangaTranslator.Api.BrowserInference;

public sealed class BrowserDetectionArtifactService(
    IProjectStore projects,
    IProjectAssetStore assets,
    IProjectAssetWriter assetWriter,
    IProjectArtifactStore artifacts,
    ITextDetectorRuntime detectorRuntime)
{
    private const int MaximumRegions = 1000;
    private const int MaximumPolygonPoints = 64;
    internal const long MaximumMaskBytes = 64L * 1024 * 1024;
    internal const long MaximumRequestBytes = MaximumMaskBytes + (4L * 1024 * 1024);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<BrowserDetectionUploadResponse> SaveAsync(
        string projectId,
        string pageId,
        BrowserDetectionUploadMetadata metadata,
        Stream maskContent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(maskContent);

        var project = await projects.GetAsync(projectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item =>
                string.Equals(item.PageId, pageId, StringComparison.Ordinal))
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        ValidatePageMetadata(page, metadata);

        var detectorId = TextDetectorIds.Normalize(metadata.DetectorId);
        if (!string.Equals(detectorId, TextDetectorIds.Craft, StringComparison.Ordinal))
        {
            throw new BrowserDetectionValidationException(
                "Browser detection uploads currently support only the CRAFT detector.");
        }

        var descriptor = detectorRuntime.GetDescriptor(detectorId);
        ValidateDetectorIdentity(descriptor, metadata);
        var modelSha256 = NormalizeSha256(metadata.ModelSha256, "modelSha256");

        var source = await ReadAssetAsync(
            projectId,
            page.SourceAssetKey,
            MaximumMaskBytes * 4,
            cancellationToken);
        var sourceSha256 = WorkflowCacheFingerprint.HashBytes(source.Data);
        if (!string.Equals(
                NormalizeSha256(metadata.SourceSha256, "sourceSha256"),
                sourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BrowserDetectionConflictException(
                "The project page changed after browser inference completed. Run detection again.");
        }

        var regions = ValidateAndConvertRegions(metadata.Regions, page.Width, page.Height);
        var uploadedMask = await ReadLimitedAsync(maskContent, MaximumMaskBytes, cancellationToken);
        var mask = DecodeAndNormalizeMask(uploadedMask, page.Width, page.Height);
        var encodedMask = LiveTranslationWorkflow.EncodeMaskPng(mask);

        var cleaned = await new OpenCvTextCleaner().CleanAsync(
            new TextCleaningRequest(
                source.Data,
                mask,
                new ImageClip(0, 0, page.Width, page.Height),
                source.MediaType),
            cancellationToken);

        var detector = new TextDetectorSelection(
            detectorId,
            descriptor,
            null,
            modelSha256);
        var dialoguePageId = await ResolveDialoguePageIdAsync(
            projectId,
            project,
            page,
            cancellationToken);
        var provenance = $"browser-model:{detectorId}:{modelSha256[..16]}";
        var dialogueRegions = LiveTranslationWorkflow.CreateDialogueRegions(dialoguePageId, regions);
        var fingerprint = WorkflowCacheFingerprint.Detection(
            sourceSha256,
            modelSha256,
            WorkflowCacheFingerprint.HashBytes(encodedMask),
            WorkflowCacheFingerprint.HashBytes(cleaned.ImageBytes.Span),
            dialoguePageId,
            descriptor,
            LiveTranslationWorkflow.HashRegions(dialogueRegions));
        var checkpointKey = BrowserCheckpointKey(fingerprint.Sha256);
        var maskAssetKey = $"workflow-cache/{page.PageId}/{checkpointKey}/mask.png";
        var cleanAssetKey = $"workflow-cache/{page.PageId}/{checkpointKey}/clean.png";
        var checkpoint = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
            page.SourceAssetKey,
            dialoguePageId,
            dialogueRegions,
            maskAssetKey,
            cleanAssetKey,
            provenance,
            fingerprint);

        await SaveBytesAsync(projectId, maskAssetKey, encodedMask, cancellationToken);
        await SaveBytesAsync(projectId, cleanAssetKey, cleaned.ImageBytes, cancellationToken);
        var checkpointJson = JsonSerializer.Serialize(checkpoint, JsonOptions);
        var immutableArtifactName = LiveTranslationWorkflow.DetectionArtifactName(
            page.PageId,
            checkpointKey);
        var existingImmutable = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(
                item.Name,
                immutableArtifactName,
                StringComparison.OrdinalIgnoreCase));
        if (existingImmutable is not null
            && !string.Equals(existingImmutable.Json, checkpointJson, StringComparison.Ordinal))
        {
            throw new BrowserDetectionConflictException(
                "A browser detection checkpoint with the same fingerprint already has different content.");
        }
        await artifacts.SaveAsync(
            projectId,
            immutableArtifactName,
            checkpointJson,
            cancellationToken);
        await artifacts.SaveAsync(
            projectId,
            LiveTranslationWorkflow.DetectionArtifactName(page.PageId, detector.CacheKey),
            checkpointJson,
            cancellationToken);
        await artifacts.SaveAsync(
            projectId,
            LiveTranslationWorkflow.DetectionArtifactName(page.PageId, BrowserLatestCacheKey),
            checkpointJson,
            cancellationToken);
        // Keep the historical no-model alias for LocalOrReusable CRAFT runs while
        // BrowserOnly runs use BrowserLatestCacheKey. Neither alias depends on the
        // model installed in the desktop host.
        await artifacts.SaveAsync(
            projectId,
            LiveTranslationWorkflow.DetectionArtifactName(
                page.PageId,
                new TextDetectorSelection(detectorId, descriptor, null, null).CacheKey),
            checkpointJson,
            cancellationToken);

        return new BrowserDetectionUploadResponse(
            detectorId,
            detector.CacheKey,
            modelSha256,
            sourceSha256,
            descriptor.AlgorithmVersion,
            descriptor.OptionsFingerprint,
            maskAssetKey,
            cleanAssetKey,
            regions.Count,
            checkpoint.Fingerprint.Sha256,
            $"{provenance}:{BackendName(metadata.Backend)}");
    }

    private static void ValidatePageMetadata(
        ProjectPageSnapshot page,
        BrowserDetectionUploadMetadata metadata)
    {
        if (!string.Equals(
                metadata.SchemaVersion,
                BrowserInferenceContractVersions.DetectionUpload,
                StringComparison.Ordinal))
        {
            throw new BrowserDetectionValidationException(
                $"Unsupported browser detection schema version: {metadata.SchemaVersion}.");
        }

        if (metadata.Width != page.Width || metadata.Height != page.Height)
        {
            throw new BrowserDetectionValidationException(
                $"Browser detection is {metadata.Width}x{metadata.Height}, but the project page is "
                + $"{page.Width}x{page.Height}.");
        }

        if (string.IsNullOrWhiteSpace(metadata.RuntimeVersion)
            || metadata.RuntimeVersion.Trim().Length > 128)
        {
            throw new BrowserDetectionValidationException(
                "runtimeVersion must contain between 1 and 128 characters.");
        }

        if (!Enum.IsDefined(metadata.Backend))
        {
            throw new BrowserDetectionValidationException("Unknown browser inference backend.");
        }
    }

    private static void ValidateDetectorIdentity(
        TextMaskDetectorDescriptor detector,
        BrowserDetectionUploadMetadata metadata)
    {
        if (!string.Equals(
                metadata.AlgorithmVersion,
                detector.AlgorithmVersion,
                StringComparison.Ordinal))
        {
            throw new BrowserDetectionConflictException(
                "The browser CRAFT algorithm version does not match the server contract.");
        }

        if (string.IsNullOrWhiteSpace(metadata.OptionsFingerprint)
            || !string.Equals(
                metadata.OptionsFingerprint,
                detector.OptionsFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BrowserDetectionConflictException(
                "The browser CRAFT options do not match the server contract.");
        }
    }

    private async Task<string> ResolveDialoguePageIdAsync(
        string projectId,
        ProjectSnapshot project,
        ProjectPageSnapshot page,
        CancellationToken cancellationToken)
    {
        if (project.WorkflowMode == ProjectWorkflowMode.Quick)
        {
            return page.PageId;
        }

        var manifestArtifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(
                item.Name,
                "book_manifest.json",
                StringComparison.OrdinalIgnoreCase))
            ?? throw new BrowserDetectionValidationException(
                "Expert projects require book_manifest.json before browser detection can be saved.");
        BookManifestDocument manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BookManifestDocument>(
                           manifestArtifact.Json,
                           JsonOptions)
                       ?? throw new JsonException("The manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new BrowserDetectionValidationException(
                $"Unable to read book_manifest.json: {exception.Message}");
        }

        return manifest.Pages.FirstOrDefault(item => item.Order == page.Index)?.PageId
               ?? page.PageId;
    }

    private static IReadOnlyList<DetectedTextRegion> ValidateAndConvertRegions(
        IReadOnlyList<BrowserDetectionRegion>? regions,
        int width,
        int height)
    {
        if (regions is null)
        {
            throw new BrowserDetectionValidationException("regions is required.");
        }

        if (regions.Count > MaximumRegions)
        {
            throw new BrowserDetectionValidationException(
                $"Browser detection returned more than {MaximumRegions} regions.");
        }

        var result = new DetectedTextRegion[regions.Count];
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index]
                ?? throw new BrowserDetectionValidationException($"regions[{index}] is null.");
            if (region.X0 < 0 || region.Y0 < 0
                || region.X1 <= region.X0 || region.Y1 <= region.Y0
                || region.X1 > width || region.Y1 > height)
            {
                throw new BrowserDetectionValidationException(
                    $"regions[{index}] has an invalid or out-of-page bounding box.");
            }

            if (!Enum.IsDefined(region.Orientation))
            {
                throw new BrowserDetectionValidationException(
                    $"regions[{index}] has an unknown orientation.");
            }

            if (region.FontSize < 1 || region.FontSize > Math.Max(width, height))
            {
                throw new BrowserDetectionValidationException(
                    $"regions[{index}] has an invalid fontSize.");
            }

            if (!float.IsFinite(region.Confidence) || region.Confidence is < 0 or > 1)
            {
                throw new BrowserDetectionValidationException(
                    $"regions[{index}] has an invalid confidence.");
            }

            if (region.Polygon is null
                || region.Polygon.Count is < 3 or > MaximumPolygonPoints)
            {
                throw new BrowserDetectionValidationException(
                    $"regions[{index}].polygon must contain 3 through {MaximumPolygonPoints} points.");
            }

            var polygon = new ImagePoint[region.Polygon.Count];
            for (var pointIndex = 0; pointIndex < region.Polygon.Count; pointIndex++)
            {
                var point = region.Polygon[pointIndex]
                    ?? throw new BrowserDetectionValidationException(
                        $"regions[{index}].polygon[{pointIndex}] is null.");
                if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
                {
                    throw new BrowserDetectionValidationException(
                        $"regions[{index}].polygon[{pointIndex}] is outside the page.");
                }
                polygon[pointIndex] = new ImagePoint(point.X, point.Y);
            }

            result[index] = new DetectedTextRegion(
                region.X0,
                region.Y0,
                region.X1,
                region.Y1,
                DetectedTextLanguage.Unknown,
                region.Orientation,
                region.FontSize,
                region.Confidence,
                [new DetectedTextLine(polygon, region.Confidence)]);
        }

        return result;
    }

    private static MaskImage DecodeAndNormalizeMask(byte[] encoded, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(encoded)
            ?? throw new BrowserDetectionValidationException("mask is not a supported image.");
        if (bitmap.Width != width || bitmap.Height != height)
        {
            throw new BrowserDetectionValidationException(
                $"Mask is {bitmap.Width}x{bitmap.Height}, but the project page is {width}x{height}.");
        }

        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var luminance = ((color.Red * 0.2126) + (color.Green * 0.7152) + (color.Blue * 0.0722))
                                * color.Alpha / 255d;
                pixels[y * width + x] = luminance > 30 ? (byte)255 : (byte)0;
            }
        }

        return new MaskImage(width, height, pixels);
    }

    private async Task SaveBytesAsync(
        string projectId,
        string assetKey,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(data.ToArray(), writable: false);
        await assetWriter.SaveAsync(projectId, assetKey, stream, cancellationToken);
    }

    private async Task<AssetBytes> ReadAssetAsync(
        string projectId,
        string assetKey,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, assetKey, cancellationToken);
        return new AssetBytes(
            asset.MediaType,
            await ReadLimitedAsync(asset.Content, maximumBytes, cancellationToken));
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var rented = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(rented, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new BrowserDetectionValidationException(
                    $"Uploaded content exceeds the {maximumBytes / 1024 / 1024} MiB limit.");
            }
            await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static string NormalizeSha256(string? value, string propertyName)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is null
            || normalized.Length != 64
            || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new BrowserDetectionValidationException(
                $"{propertyName} must be a 64-character SHA-256 hexadecimal digest.");
        }
        return normalized;
    }

    private static string BackendName(BrowserInferenceBackend backend) => backend switch
    {
        BrowserInferenceBackend.WebGpu => "webgpu",
        BrowserInferenceBackend.Wasm => "wasm",
        _ => throw new BrowserDetectionValidationException("Unknown browser inference backend.")
    };

    internal static string BrowserCheckpointKey(string fingerprintSha256) =>
        $"browser-{fingerprintSha256}";

    internal const string BrowserLatestCacheKey = "browser-craft-latest";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record AssetBytes(string MediaType, byte[] Data);
}
