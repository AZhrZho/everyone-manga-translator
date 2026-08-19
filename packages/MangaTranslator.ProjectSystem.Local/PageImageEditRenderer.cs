using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.Cleaning;
using MangaTranslator.LocalDetection;
using SkiaSharp;

namespace MangaTranslator.ProjectSystem.Local;

internal static class PageImageEditRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string WorkingCleanAssetKey(string pageId) => $"working/{pageId}/clean.png";

    public static string NormalizeRepairDocument(string operationsJson)
    {
        ImageEditDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ImageEditDocument>(operationsJson, JsonOptions)
                ?? throw new JsonException("The repair document is empty.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("底图修复操作 JSON 无效。", nameof(operationsJson), exception);
        }

        if (document.Operations.Any(operation =>
                !string.Equals(NormalizeTool(operation.Tool), "inpaint", StringComparison.Ordinal)))
        {
            throw new ArgumentException("底图修复只支持智能清除操作。", nameof(operationsJson));
        }

        return JsonSerializer.Serialize(new ImageEditDocument
        {
            SchemaVersion = "2.0.0",
            Operations = document.Operations
        }, JsonOptions);
    }

    public static string NormalizeStoredRepairDocument(string? operationsJson)
    {
        if (string.IsNullOrWhiteSpace(operationsJson))
        {
            return "{\"schema_version\":\"2.0.0\",\"operations\":[]}";
        }
        try
        {
            var document = JsonSerializer.Deserialize<ImageEditDocument>(operationsJson, JsonOptions)
                ?? new ImageEditDocument();
            return JsonSerializer.Serialize(new ImageEditDocument
            {
                SchemaVersion = "2.0.0",
                Operations = document.Operations
                    .Where(operation => string.Equals(
                        NormalizeTool(operation.Tool),
                        "inpaint",
                        StringComparison.Ordinal))
                    .ToArray()
            }, JsonOptions);
        }
        catch (JsonException)
        {
            return "{\"schema_version\":\"2.0.0\",\"operations\":[]}";
        }
    }

    public static bool HasRepairOperations(string? operationsJson)
    {
        if (string.IsNullOrWhiteSpace(operationsJson)) return false;
        try
        {
            return JsonSerializer.Deserialize<ImageEditDocument>(operationsJson, JsonOptions)?.Operations.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task RenderAsync(
        string projectRoot,
        string baseAssetKey,
        string operationsJson,
        string outputAssetKey,
        CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Deserialize<ImageEditDocument>(operationsJson, JsonOptions)
            ?? new ImageEditDocument();
        if (document.Operations.Any(operation =>
                !string.Equals(NormalizeTool(operation.Tool), "inpaint", StringComparison.Ordinal)))
        {
            throw new ArgumentException("底图修复只支持智能清除操作。", nameof(operationsJson));
        }
        var basePath = LocalProjectLayout.ResolveAssetPath(projectRoot, baseAssetKey);
        var outputPath = LocalProjectLayout.ResolveAssetPath(projectRoot, outputAssetKey);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseBitmap = SKBitmap.Decode(basePath)
                ?? throw new InvalidOperationException($"无法读取底图资源 {baseAssetKey}。");
            using var repairMask = new SKBitmap(
                baseBitmap.Width,
                baseBitmap.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using var repairMaskCanvas = new SKCanvas(repairMask);
            repairMaskCanvas.Clear(SKColors.Transparent);
            foreach (var operation in document.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrawInpaintOperation(repairMaskCanvas, operation);
            }

            using var outputBitmap = RenderContentAwareRepair(
                basePath,
                baseBitmap,
                repairMask,
                cancellationToken);
            using var image = SKImage.FromBitmap(outputBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("无法编码底图修复结果。");
            var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    data.SaveTo(stream);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }, cancellationToken);
    }

    public static string? ResolveLegacyCandidateMaskAssetKey(string? operationsJson)
    {
        if (string.IsNullOrWhiteSpace(operationsJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ImageEditDocument>(operationsJson, JsonOptions)?.MaskBaseAssetKey;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void DeleteWorkingAsset(string projectRoot, string pageId)
    {
        var path = LocalProjectLayout.ResolveAssetPath(projectRoot, WorkingCleanAssetKey(pageId));
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DrawInpaintOperation(
        SKCanvas maskCanvas,
        ImageEditOperation operation)
    {
        if (operation.Points.Count == 0) return;
        var size = Math.Max(1f, operation.SizePx);
        var spacing = size * Math.Clamp(operation.Spacing ?? 18f, 8f, 100f) / 100f;
        var diameter = Math.Max(2, (int)MathF.Ceiling(size));
        using var brushMask = CreateBrushMask(diameter);
        foreach (var point in Interpolate(operation.Points, Math.Max(2f, spacing)))
        {
            maskCanvas.DrawBitmap(
                brushMask,
                new SKRect(
                    point.X - size / 2f,
                    point.Y - size / 2f,
                    point.X + size / 2f,
                    point.Y + size / 2f),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
    }

    private static SKBitmap RenderContentAwareRepair(
        string basePath,
        SKBitmap baseBitmap,
        SKBitmap repairMask,
        CancellationToken cancellationToken)
    {
        var maskPixels = new byte[checked(repairMask.Width * repairMask.Height)];
        var hasPaintedPixels = false;
        for (var y = 0; y < repairMask.Height; y++)
        {
            for (var x = 0; x < repairMask.Width; x++)
            {
                var alpha = repairMask.GetPixel(x, y).Alpha;
                maskPixels[y * repairMask.Width + x] = alpha;
                hasPaintedPixels |= alpha > 30;
            }
        }

        if (!hasPaintedPixels)
        {
            return baseBitmap.Copy()
                ?? throw new InvalidOperationException("无法复制底图资源。");
        }

        var cleaner = new OpenCvTextCleaner(new OpenCvTextCleanerOptions
        {
            MaskExpansionRadius = 0,
            BackgroundSampleGap = 3,
            BackgroundSampleRadius = 12,
            InpaintRadius = 3
        });
        var cleaned = cleaner.CleanAsync(
                new TextCleaningRequest(
                    File.ReadAllBytes(basePath),
                    new MaskImage(repairMask.Width, repairMask.Height, maskPixels),
                    new ImageClip(0, 0, repairMask.Width, repairMask.Height),
                    "image/png"),
                cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        using var cleanedBitmap = SKBitmap.Decode(cleaned.ImageBytes.Span)
            ?? throw new InvalidOperationException("无法读取智能清除结果。");
        using var repairedLayer = new SKBitmap(
            repairMask.Width,
            repairMask.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var repairedCanvas = new SKCanvas(repairedLayer))
        using (var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn })
        {
            // Pipeline clean assets can be opaque RGB or grayscale PNGs. Applying
            // DstIn directly to an opaque bitmap cannot make pixels outside the
            // brush transparent; Skia stores them as opaque black instead, which
            // then covers the entire base page. Always composite through an
            // alpha-capable layer before applying the repair mask.
            repairedCanvas.Clear(SKColors.Transparent);
            repairedCanvas.DrawBitmap(cleanedBitmap, 0, 0, SKSamplingOptions.Default);
            repairedCanvas.DrawBitmap(repairMask, 0, 0, SKSamplingOptions.Default, maskPaint);
        }

        var output = new SKBitmap(
            baseBitmap.Width,
            baseBitmap.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var outputCanvas = new SKCanvas(output))
        {
            outputCanvas.Clear(SKColors.Transparent);
            outputCanvas.DrawBitmap(baseBitmap, 0, 0, SKSamplingOptions.Default);
            outputCanvas.DrawBitmap(repairedLayer, 0, 0, SKSamplingOptions.Default);
        }
        return output;
    }

    private static SKBitmap CreateBrushMask(int diameter)
    {
        var mask = new SKBitmap(diameter, diameter, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(mask);
        canvas.Clear(SKColors.Transparent);
        var radius = diameter / 2f;
        var innerRadius = radius * 0.82f;
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(radius, radius),
            radius,
            [SKColors.White, SKColors.White, SKColors.Transparent],
            [0f, Math.Clamp(innerRadius / radius, 0f, 0.999f), 1f],
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, Shader = shader };
        canvas.DrawCircle(radius, radius, radius, paint);
        return mask;
    }

    private static string NormalizeTool(string? tool) => (tool ?? string.Empty).Replace('_', '-').ToLowerInvariant();

    private static IReadOnlyList<ImageEditPoint> Interpolate(IReadOnlyList<ImageEditPoint> points, float spacing)
    {
        if (points.Count <= 1) return points;
        var result = new List<ImageEditPoint> { points[0] };
        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            var distance = MathF.Sqrt(MathF.Pow(current.X - previous.X, 2) + MathF.Pow(current.Y - previous.Y, 2));
            var steps = Math.Max(1, (int)MathF.Ceiling(distance / spacing));
            for (var step = 1; step <= steps; step++)
            {
                result.Add(new ImageEditPoint(
                    previous.X + ((current.X - previous.X) * step / steps),
                    previous.Y + ((current.Y - previous.Y) * step / steps)));
            }
        }
        return result;
    }

    private sealed record ImageEditDocument
    {
        [JsonPropertyName("schema_version")]
        public string? SchemaVersion { get; init; }

        [JsonPropertyName("base_asset_key")]
        public string? BaseAssetKey { get; init; }

        [JsonPropertyName("mask_base_asset_key")]
        public string? MaskBaseAssetKey { get; init; }

        [JsonPropertyName("operations")]
        public IReadOnlyList<ImageEditOperation> Operations { get; init; } = [];
    }

    private sealed record ImageEditOperation
    {
        [JsonPropertyName("tool")]
        public string Tool { get; init; } = "clean";

        [JsonPropertyName("size_px")]
        public float SizePx { get; init; } = 1;

        [JsonPropertyName("points")]
        public IReadOnlyList<ImageEditPoint> Points { get; init; } = [];

        [JsonPropertyName("spacing")]
        public float? Spacing { get; init; }
    }

    private sealed record ImageEditPoint(
        [property: JsonPropertyName("x")] float X,
        [property: JsonPropertyName("y")] float Y);
}
