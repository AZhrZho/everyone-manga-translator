using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaTranslator.Cleaning;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;
using MangaTranslator.ProjectSystem;
using SkiaSharp;

namespace MangaTranslator.Api.Projects;

public sealed class LayoutAwareCleaningService(
    IProjectAssetStore assets,
    IProjectAssetWriter assetWriter,
    IProjectStore projects,
    IProjectArtifactStore? artifacts = null)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string CandidateMaskAssetKey(string pageId) =>
        $"workflow-cache/{pageId}/mask.png";

    private async Task<string?> ResolveCandidateMaskAssetKeyAsync(
        string projectId,
        ProjectPageSnapshot page,
        CancellationToken cancellationToken)
    {
        var imageEdit = await projects.GetPageImageEditAsync(
            projectId,
            page.PageId,
            cancellationToken);
        foreach (var candidate in new[]
                 {
                     imageEdit.CandidateMaskAssetKey,
                     CandidateMaskAssetKey(page.PageId),
                     $"workflow-cache/{page.PageId}/cached-candidate-mask.png"
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && await assets.ExistsAsync(projectId, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        if (artifacts is null)
        {
            return null;
        }

        var workflowMask = (await artifacts.ListAsync(projectId, cancellationToken))
            .Where(artifact =>
                artifact.Name.StartsWith(
                    $"translation_detection_{Sanitize(page.PageId)}_",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => artifact.UpdatedAt)
            .Select(artifact => TryReadDetectionMaskAssetKey(artifact.Json))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!string.IsNullOrWhiteSpace(workflowMask)
            && await assets.ExistsAsync(projectId, workflowMask, cancellationToken))
        {
            return workflowMask;
        }

        return null;
    }

    public async Task<LayoutCleaningResult?> PrepareCurrentBackgroundAsync(
        string projectId,
        string pageId,
        string layoutJson,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        if (project.Revision != expectedRevision)
        {
            throw new ProjectRevisionConflictException(project.Revision);
        }
        var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        var candidateMaskAssetKey = await ResolveCandidateMaskAssetKeyAsync(projectId, page, cancellationToken);
        if (candidateMaskAssetKey is null) return null;
        return await RebuildAsync(
            projectId,
            page,
            page.SourceAssetKey,
            candidateMaskAssetKey,
            layoutJson,
            PreparedOutputPrefix(pageId, layoutJson, expectedRevision),
            cancellationToken);
    }

    private static string PreparedOutputPrefix(
        string pageId,
        string layoutJson,
        int expectedRevision)
    {
        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(layoutJson)))
            .ToLowerInvariant()[..16];
        return $"derived/{pageId}/layout-cleaning/r{expectedRevision + 1}-{fingerprint}";
    }

    private static string? TryReadDetectionMaskAssetKey(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("maskAssetKey", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_').ToArray());

    public async Task<LayoutCleaningResult> RebuildAsync(
        string projectId,
        ProjectPageSnapshot page,
        string sourceAssetKey,
        string candidateMaskAssetKey,
        string layoutJson,
        string outputAssetPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAssetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateMaskAssetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputAssetPrefix);

        var regions = ReadRenderableRegions(layoutJson, page.Width, page.Height);
        var source = await ReadAssetAsync(projectId, sourceAssetKey, cancellationToken);
        var candidateMask = await ReadMaskAsync(
            projectId,
            candidateMaskAssetKey,
            page.Width,
            page.Height,
            cancellationToken);
        var effectiveMask = ClipMaskToRegions(candidateMask, regions);
        var cleaner = new OpenCvTextCleaner();
        var cleaned = await cleaner.CleanAsync(
            new TextCleaningRequest(
                source.Data,
                effectiveMask,
                new ImageClip(0, 0, page.Width, page.Height),
                source.MediaType),
            cancellationToken);

        var maskAssetKey = $"{outputAssetPrefix.TrimEnd('/')}/mask.png";
        var cleanAssetKey = $"{outputAssetPrefix.TrimEnd('/')}/clean.png";
        await SaveBytesAsync(
            projectId,
            maskAssetKey,
            LiveMaskEncoder.Encode(effectiveMask),
            cancellationToken);
        await SaveBytesAsync(
            projectId,
            cleanAssetKey,
            cleaned.ImageBytes,
            cancellationToken);
        return new LayoutCleaningResult(
            cleanAssetKey,
            maskAssetKey,
            candidateMaskAssetKey,
            regions.Count,
            cleaned.AppliedMaskPixels);
    }

    internal static IReadOnlyList<TextLayoutBox> ReadRenderableTextBoxes(
        string layoutJson,
        int pageWidth,
        int pageHeight) => ReadRenderableRegions(layoutJson, pageWidth, pageHeight)
        .Select(region => region.Box)
        .ToArray();

    internal static IReadOnlyList<LayoutMaskRegion> ReadRenderableRegions(
        string layoutJson,
        int pageWidth,
        int pageHeight)
    {
        using var document = JsonDocument.Parse(layoutJson);
        if (!document.RootElement.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The page layout does not contain a segments array.");
        }

        var regions = new List<LayoutMaskRegion>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "unreadable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!segment.TryGetProperty("translated_text", out var translatedText)
                || translatedText.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(translatedText.GetString())
                || !segment.TryGetProperty("layout", out var layout))
            {
                continue;
            }

            var textLayout = JsonSerializer.Deserialize<DialogueTextLayout>(layout.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException("A page layout segment contains an unreadable layout object.");
            var normalizedRotation = DialogueBoxRotationCoordinates.NormalizeLegacyPageRelativeCenter(
                textLayout.Rotation,
                textLayout.TextBox);
            var placementGroups = textLayout.Advanced?.PlacementGroups;
            if (placementGroups is { Count: > 0 })
            {
                foreach (var group in placementGroups)
                {
                    var groupRotation = normalizedRotation is null
                        ? null
                        : normalizedRotation with
                        {
                            CenterXPx = normalizedRotation.CenterXPx - (group.TextBox.X0 - textLayout.TextBox.X0),
                            CenterYPx = normalizedRotation.CenterYPx - (group.TextBox.Y0 - textLayout.TextBox.Y0)
                        };
                    regions.Add(new LayoutMaskRegion(
                        Clamp(group.TextBox, pageWidth, pageHeight),
                        groupRotation));
                }
            }
            else
            {
                regions.Add(new LayoutMaskRegion(
                    Clamp(textLayout.TextBox, pageWidth, pageHeight),
                    normalizedRotation));
            }
        }
        return regions.Where(region => region.Box.X1 > region.Box.X0 && region.Box.Y1 > region.Box.Y0).ToArray();
    }

    internal static MaskImage ClipMaskToTextBoxes(
        MaskImage candidateMask,
        IReadOnlyList<TextLayoutBox> boxes) => ClipMaskToRegions(
        candidateMask,
        boxes.Select(box => new LayoutMaskRegion(box, null)).ToArray());

    internal static MaskImage ClipMaskToRegions(
        MaskImage candidateMask,
        IReadOnlyList<LayoutMaskRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(candidateMask);
        ArgumentNullException.ThrowIfNull(regions);
        var output = new byte[candidateMask.Pixels.Length];
        var input = candidateMask.Pixels.Span;
        foreach (var region in regions)
        {
            var box = region.Box;
            if (region.Rotation is { AngleDeg: not 0 } rotation)
            {
                CopyRotatedRegion(input, output, candidateMask.Width, candidateMask.Height, box, rotation);
                continue;
            }
            var x0 = Math.Clamp(box.X0, 0, candidateMask.Width);
            var y0 = Math.Clamp(box.Y0, 0, candidateMask.Height);
            var x1 = Math.Clamp(box.X1, 0, candidateMask.Width);
            var y1 = Math.Clamp(box.Y1, 0, candidateMask.Height);
            for (var y = y0; y < y1; y++)
            {
                var offset = y * candidateMask.Width;
                input.Slice(offset + x0, x1 - x0).CopyTo(output.AsSpan(offset + x0, x1 - x0));
            }
        }
        return new MaskImage(candidateMask.Width, candidateMask.Height, output);
    }

    private static void CopyRotatedRegion(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int width,
        int height,
        TextLayoutBox box,
        DialogueBoxRotation rotation)
    {
        var centerX = box.X0 + rotation.CenterXPx;
        var centerY = box.Y0 + rotation.CenterYPx;
        var radians = rotation.AngleDeg * MathF.PI / 180f;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        var corners = new[]
        {
            Rotate(box.X0, box.Y0, centerX, centerY, cosine, sine),
            Rotate(box.X1, box.Y0, centerX, centerY, cosine, sine),
            Rotate(box.X0, box.Y1, centerX, centerY, cosine, sine),
            Rotate(box.X1, box.Y1, centerX, centerY, cosine, sine)
        };
        var x0 = Math.Clamp((int)MathF.Floor(corners.Min(point => point.X)), 0, width);
        var y0 = Math.Clamp((int)MathF.Floor(corners.Min(point => point.Y)), 0, height);
        var x1 = Math.Clamp((int)MathF.Ceiling(corners.Max(point => point.X)), 0, width);
        var y1 = Math.Clamp((int)MathF.Ceiling(corners.Max(point => point.Y)), 0, height);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var dx = x + 0.5f - centerX;
                var dy = y + 0.5f - centerY;
                var unrotatedX = centerX + (dx * cosine) + (dy * sine);
                var unrotatedY = centerY - (dx * sine) + (dy * cosine);
                if (unrotatedX >= box.X0 && unrotatedX < box.X1
                    && unrotatedY >= box.Y0 && unrotatedY < box.Y1)
                {
                    output[y * width + x] = input[y * width + x];
                }
            }
        }
    }

    private static (float X, float Y) Rotate(
        float x,
        float y,
        float centerX,
        float centerY,
        float cosine,
        float sine)
    {
        var dx = x - centerX;
        var dy = y - centerY;
        return (
            centerX + (dx * cosine) - (dy * sine),
            centerY + (dx * sine) + (dy * cosine));
    }

    private async Task<AssetBytes> ReadAssetAsync(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, assetKey, cancellationToken);
        await using var buffer = new MemoryStream();
        await asset.Content.CopyToAsync(buffer, cancellationToken);
        return new AssetBytes(asset.MediaType, buffer.ToArray());
    }

    private async Task<MaskImage> ReadMaskAsync(
        string projectId,
        string assetKey,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        var asset = await ReadAssetAsync(projectId, assetKey, cancellationToken);
        using var bitmap = SKBitmap.Decode(asset.Data)
            ?? throw new InvalidOperationException($"Unable to decode candidate mask '{assetKey}'.");
        if (bitmap.Width != expectedWidth || bitmap.Height != expectedHeight)
        {
            throw new InvalidOperationException(
                $"Candidate mask is {bitmap.Width}x{bitmap.Height}, but page '{assetKey}' is {expectedWidth}x{expectedHeight}.");
        }

        var pixels = new byte[checked(bitmap.Width * bitmap.Height)];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                pixels[y * bitmap.Width + x] = (byte)Math.Clamp(
                    (int)Math.Round(
                        ((color.Red * 0.2126) + (color.Green * 0.7152) + (color.Blue * 0.0722))
                        * color.Alpha / 255d),
                    0,
                    255);
            }
        }
        return new MaskImage(bitmap.Width, bitmap.Height, pixels);
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

    private static TextLayoutBox Clamp(TextLayoutBox box, int width, int height) => new(
        Math.Clamp(box.X0, 0, width),
        Math.Clamp(box.Y0, 0, height),
        Math.Clamp(box.X1, 0, width),
        Math.Clamp(box.Y1, 0, height));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        return options;
    }

    private sealed record AssetBytes(string MediaType, byte[] Data);
}

internal sealed record LayoutMaskRegion(
    TextLayoutBox Box,
    DialogueBoxRotation? Rotation);

public sealed record LayoutCleaningResult(
    string CleanAssetKey,
    string MaskAssetKey,
    string CandidateMaskAssetKey,
    int TextBoxCount,
    int AppliedMaskPixels);

internal static class LiveMaskEncoder
{
    public static byte[] Encode(MaskImage mask)
    {
        var pixels = mask.Pixels.ToArray();
        using var bitmap = new SKBitmap(mask.Width, mask.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode the effective text mask.");
        return data.ToArray();
    }
}
