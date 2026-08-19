using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.LocalDetection;
using MangaTranslator.Precheck;
using MangaTranslator.Rendering;
using SkiaSharp;

namespace MangaTranslator.Pipeline.Integration.Tests;

internal sealed class PipelineTestArtifactWriter : IGlobalSemanticArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ConcurrentQueue<PrecheckProgress> precheckProgress = new();

    public PipelineTestArtifactWriter(string testName, string runId, string bookId)
    {
        var workspaceRoot = FindWorkspaceRoot();
        WorkspaceRoot = workspaceRoot;
        BookDirectory = ToIoPath(Path.Combine(
            workspaceRoot,
            "TestResults",
            "MangaTranslator.Pipeline.Integration.Tests",
            Sanitize(testName),
            "runs",
            Sanitize(runId),
            "books",
            Sanitize(bookId)));
        SourceDirectory = Path.Combine(BookDirectory, "source");
        MaskDirectory = Path.Combine(BookDirectory, "mask");
        RenderDirectory = Path.Combine(BookDirectory, "render");
        MixedDirectory = Path.Combine(BookDirectory, "mixed");
        PrecheckDirectory = Path.Combine(BookDirectory, "precheck");
        PagesDirectory = Path.Combine(BookDirectory, "pages");

        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(MaskDirectory);
        Directory.CreateDirectory(RenderDirectory);
        Directory.CreateDirectory(MixedDirectory);
        Directory.CreateDirectory(PrecheckDirectory);
        Directory.CreateDirectory(PagesDirectory);
    }

    public string WorkspaceRoot { get; }

    public string BookDirectory { get; }

    public string SourceDirectory { get; }

    public string MaskDirectory { get; }

    public string RenderDirectory { get; }

    public string MixedDirectory { get; }

    public string PrecheckDirectory { get; }

    public string PagesDirectory { get; }

    public static string? FindLatestResumableRunId(string testName, string bookId)
    {
        var runsDirectory = Path.Combine(
            FindWorkspaceRoot(),
            "TestResults",
            "MangaTranslator.Pipeline.Integration.Tests",
            Sanitize(testName),
            "runs");
        runsDirectory = ToIoPath(runsDirectory);
        if (!Directory.Exists(runsDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(runsDirectory)
            .Select(path => new DirectoryInfo(path))
            .Where(directory =>
                File.Exists(Path.Combine(
                    directory.FullName,
                    "books",
                    Sanitize(bookId),
                    "precheck",
                    "checkpoint.json"))
                && !File.Exists(Path.Combine(
                    directory.FullName,
                    "books",
                    Sanitize(bookId),
                    "summary.json")))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Select(directory => directory.Name)
            .FirstOrDefault();
    }

    public IPrecheckProgressReporter CreateProgressReporter() =>
        new CapturingProgressReporter(precheckProgress);

    public string GetPageDirectory(string pageId)
    {
        var path = Path.Combine(PagesDirectory, Sanitize(pageId));
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetMaskPath(string fileName) =>
        Path.Combine(MaskDirectory, Path.ChangeExtension(fileName, ".png"));

    public string GetRenderPath(string fileName) =>
        Path.Combine(RenderDirectory, Path.ChangeExtension(fileName, ".png"));

    public string GetMixedPath(string fileName) =>
        Path.Combine(MixedDirectory, Path.ChangeExtension(fileName, ".png"));

    public string GetPageStagePath(string pageId, string stage, string fileName) =>
        Path.Combine(GetPageDirectory(pageId), Sanitize(stage), fileName);

    public async Task CopySourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(SourceDirectory, Path.GetFileName(sourcePath));
        var data = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await WriteBytesAtomicallyAsync(destination, data, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMaskAsync(
        string fileName,
        MaskImage mask,
        CancellationToken cancellationToken)
    {
        var png = EncodeMaskPng(mask);
        await WriteBytesAtomicallyAsync(
            GetMaskPath(fileName),
            png,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteRenderAsync(
        string fileName,
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken) =>
        await WriteBytesAtomicallyAsync(
            GetRenderPath(fileName),
            png.ToArray(),
            cancellationToken).ConfigureAwait(false);

    public async Task WriteMixedAsync(
        string sourcePath,
        string fileName,
        PageCompositionData renderOverlay,
        float renderLayerOpacity,
        bool debug,
        CancellationToken cancellationToken)
    {
        var sourceData = await File.ReadAllBytesAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        var mixedData = ComposeMixedPng(
            sourceData,
            renderOverlay,
            renderLayerOpacity,
            debug,
            fileName);
        await WriteBytesAtomicallyAsync(
            GetMixedPath(fileName),
            mixedData,
            cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] ComposeMixedPng(
        byte[] sourceData,
        PageCompositionData renderOverlay,
        float renderLayerOpacity,
        bool debug,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(sourceData);
        ArgumentNullException.ThrowIfNull(renderOverlay);
        ArgumentNullException.ThrowIfNull(renderOverlay.Placements);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (renderLayerOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(renderLayerOpacity),
                renderLayerOpacity,
                "The render layer opacity must be between zero and one.");
        }

        using var sourceBitmap = SKBitmap.Decode(sourceData)
            ?? throw new InvalidDataException(
                $"Unable to decode source image for {fileName}.");
        if (sourceBitmap.Width != renderOverlay.Width
            || sourceBitmap.Height != renderOverlay.Height)
        {
            throw new InvalidDataException(
                $"Source and render-overlay dimensions differ for {fileName}: "
                + $"source={sourceBitmap.Width}x{sourceBitmap.Height}, "
                + $"render={renderOverlay.Width}x{renderOverlay.Height}.");
        }

        using var surface = SKSurface.Create(new SKImageInfo(
            sourceBitmap.Width,
            sourceBitmap.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul))
            ?? throw new InvalidOperationException("SkiaSharp could not create the mixed surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(sourceBitmap, 0, 0, SKSamplingOptions.Default, null);
        using var layerPaint = new SKPaint
        {
            BlendMode = SKBlendMode.SrcOver,
            Color = SKColors.White.WithAlpha((byte)Math.Round(255 * renderLayerOpacity))
        };
        foreach (var placement in renderOverlay.Placements)
        {
            DrawOverlayBlock(canvas, placement, layerPaint);
        }

        if (debug)
        {
            DrawOverlayDebugBorders(
                canvas,
                renderOverlay,
                (byte)Math.Round(255 * renderLayerOpacity));
        }

        canvas.Flush();

        using var mixedImage = surface.Snapshot();
        using var mixedData = mixedImage.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode the mixed PNG.");
        return mixedData.ToArray();
    }

    private static void DrawOverlayBlock(
        SKCanvas canvas,
        TextBlockPlacement placement,
        SKPaint layerPaint)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(placement.Block);
        using var bitmap = SKBitmap.Decode(placement.Block.PngData.ToArray())
            ?? throw new InvalidDataException(
                $"Unable to decode transparent render block '{placement.Block.BlockId}'.");
        if (bitmap.Width != placement.Block.Width || bitmap.Height != placement.Block.Height)
        {
            throw new InvalidDataException(
                $"Transparent render block '{placement.Block.BlockId}' dimensions differ "
                + "from its metadata.");
        }

        canvas.Save();
        ApplyOverlayRotation(canvas, placement);
        canvas.DrawBitmap(bitmap, placement.X, placement.Y, SKSamplingOptions.Default, layerPaint);
        canvas.Restore();
    }

    private static void DrawOverlayDebugBorders(
        SKCanvas canvas,
        PageCompositionData renderOverlay,
        byte layerAlpha)
    {
        using var pagePaint = new SKPaint
        {
            Color = new SKColor(37, 99, 235, layerAlpha),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false,
            BlendMode = SKBlendMode.SrcOver
        };
        canvas.DrawRect(
            new SKRect(
                0.5f,
                0.5f,
                renderOverlay.Width - 0.5f,
                renderOverlay.Height - 0.5f),
            pagePaint);

        using var blockPaint = new SKPaint
        {
            Color = new SKColor(220, 38, 38, layerAlpha),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false,
            BlendMode = SKBlendMode.SrcOver
        };
        foreach (var placement in renderOverlay.Placements)
        {
            canvas.Save();
            ApplyOverlayRotation(canvas, placement);
            canvas.DrawRect(
                new SKRect(
                    placement.X + 0.5f,
                    placement.Y + 0.5f,
                    placement.X + placement.Block.Width - 0.5f,
                    placement.Y + placement.Block.Height - 0.5f),
                blockPaint);
            canvas.Restore();
        }
    }

    private static void ApplyOverlayRotation(
        SKCanvas canvas,
        TextBlockPlacement placement)
    {
        var rotation = placement.Rotation;
        if (rotation is null || rotation.AngleDeg == 0)
        {
            return;
        }

        if (!float.IsFinite(rotation.CenterXPx)
            || !float.IsFinite(rotation.CenterYPx)
            || !float.IsFinite(rotation.AngleDeg))
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement),
                "Text-block rotation values must be finite.");
        }

        var centerX = placement.X + rotation.CenterXPx;
        var centerY = placement.Y + rotation.CenterYPx;
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees(rotation.AngleDeg);
        canvas.Translate(-centerX, -centerY);
    }

    public Task WriteBookJsonAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(BookDirectory, relativePath), value, cancellationToken);

    public void DeleteBookFileIfExists(string fileName) =>
        DeleteFileIfExists(BookDirectory, fileName);

    public void DeletePageStageFileIfExists(
        string pageId,
        string stage,
        string fileName) =>
        DeleteFileIfExists(
            Path.Combine(GetPageDirectory(pageId), Sanitize(stage)),
            fileName);

    public Task WritePrecheckJsonAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(PrecheckDirectory, relativePath), value, cancellationToken);

    public Task<T?> TryReadPrecheckJsonAsync<T>(
        string relativePath,
        CancellationToken cancellationToken)
        where T : class =>
        TryReadJsonAsync<T>(
            Path.Combine(PrecheckDirectory, relativePath),
            cancellationToken);

    public Task WritePageJsonAsync<T>(
        string pageId,
        string relativePath,
        T value,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            Path.Combine(GetPageDirectory(pageId), relativePath),
            value,
            cancellationToken);

    public Task WritePageStageJsonAsync<T>(
        string pageId,
        string stage,
        string fileName,
        T value,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            Path.Combine(GetPageDirectory(pageId), Sanitize(stage), fileName),
            value,
            cancellationToken);

    public Task WritePrecheckProgressAsync(CancellationToken cancellationToken) =>
        WritePrecheckJsonAsync(
            "progress.json",
            precheckProgress.ToArray(),
            cancellationToken);

    public Task<PrecheckCheckpoint?> TryLoadCheckpointAsync(
        CancellationToken cancellationToken) =>
        TryReadJsonAsync<PrecheckCheckpoint>(
            Path.Combine(PrecheckDirectory, "checkpoint.json"),
            cancellationToken);

    public Task<T?> TryReadPageStageJsonAsync<T>(
        string pageId,
        string stage,
        string fileName,
        CancellationToken cancellationToken)
        where T : class =>
        TryReadJsonAsync<T>(
            GetPageStagePath(pageId, stage, fileName),
            cancellationToken);

    public async ValueTask<JsonElement?> TryLoadAsync(
        string runId,
        string artifactName,
        CancellationToken cancellationToken = default)
    {
        _ = runId;
        var path = GetSemanticArtifactPath(artifactName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async ValueTask SaveAsync(
        string runId,
        string artifactName,
        JsonElement value,
        CancellationToken cancellationToken = default)
    {
        _ = runId;
        await WriteJsonAsync(
            GetSemanticArtifactPath(artifactName),
            value,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                data.ToArray(),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<T?> TryReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private string GetSemanticArtifactPath(string artifactName) =>
        Path.Combine(
            PrecheckDirectory,
            "semantic-artifacts",
            Sanitize(artifactName) + ".json");

    private static byte[] EncodeMaskPng(MaskImage mask)
    {
        var pixels = mask.Pixels.ToArray();
        using var bitmap = new SKBitmap(new SKImageInfo(
            mask.Width,
            mask.Height,
            SKColorType.Gray8,
            SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode the mask as PNG.");
        return data.ToArray();
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the workspace root containing MangaTranslator.slnx.");
    }

    /// <summary>
    /// The test-result convention intentionally includes the full test name and can exceed
    /// MAX_PATH on Windows. Use the Win32 extended path form for physical I/O while keeping
    /// the visible directory layout unchanged.
    /// </summary>
    private static string ToIoPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()
            || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return @"\\?\" + fullPath;
    }

    private static void DeleteFileIfExists(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!StringComparer.Ordinal.Equals(Path.GetFileName(fileName), fileName))
        {
            throw new ArgumentException(
                "The generated artifact file name must not contain a directory path.",
                nameof(fileName));
        }

        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class CapturingProgressReporter(
        ConcurrentQueue<PrecheckProgress> progress) : IPrecheckProgressReporter
    {
        public void Report(PrecheckProgress item)
        {
            ArgumentNullException.ThrowIfNull(item);
            progress.Enqueue(item);
        }
    }
}
