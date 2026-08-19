using SkiaSharp;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class PageImageEditRendererTests
{
    [Fact]
    public async Task ContentAwareEraseChangesOnlyThePaintedArea()
    {
        using var workspace = new TemporaryWorkspace();
        var baseKey = "clean/page.png";
        var outputKey = "working/page/clean.png";
        WriteTestImage(Path.Combine(workspace.Root, baseKey), 50, 50, (x, y) => x is >= 22 and <= 27 && y is >= 22 and <= 27 ? SKColors.Black : new SKColor(225, 225, 225));
        var document = """
            {"operations":[{"operation_id":"inpaint-1","tool":"inpaint","size_px":16,"hardness":35,"opacity":100,"flow":100,"spacing":15,"points":[{"x":25,"y":25}]}]}
            """;

        await PageImageEditRenderer.RenderAsync(workspace.Root, baseKey, document, outputKey, CancellationToken.None);

        using var output = SKBitmap.Decode(Path.Combine(workspace.Root, outputKey));
        Assert.True(output.GetPixel(25, 25).Red > 80);
        Assert.Equal(new SKColor(225, 225, 225), output.GetPixel(4, 4));
    }

    [Fact]
    public async Task ContentAwareErasePreservesOpaqueRgbImageOutsideThePaintedArea()
    {
        using var workspace = new TemporaryWorkspace();
        var baseKey = "clean/page.jpg";
        var outputKey = "working/page/clean.png";
        WriteTestJpeg(
            Path.Combine(workspace.Root, baseKey),
            50,
            50,
            (x, y) => x is >= 22 and <= 27 && y is >= 22 and <= 27
                ? SKColors.Black
                : new SKColor(225, 225, 225));
        var document = """
            {"operations":[{"operation_id":"inpaint-1","tool":"inpaint","size_px":16,"spacing":15,"points":[{"x":25,"y":25}]}]}
            """;

        using (var input = SKBitmap.Decode(Path.Combine(workspace.Root, baseKey)))
        {
            Assert.Equal(SKAlphaType.Opaque, input.AlphaType);
        }

        await PageImageEditRenderer.RenderAsync(workspace.Root, baseKey, document, outputKey, CancellationToken.None);

        using var output = SKBitmap.Decode(Path.Combine(workspace.Root, outputKey));
        Assert.True(output.GetPixel(25, 25).Red > 80);
        Assert.InRange(output.GetPixel(4, 4).Red, (byte)210, byte.MaxValue);
        Assert.InRange(output.GetPixel(4, 4).Green, (byte)210, byte.MaxValue);
        Assert.InRange(output.GetPixel(4, 4).Blue, (byte)210, byte.MaxValue);
    }

    [Fact]
    public async Task RepairAlwaysStartsFromDerivedCleanInsteadOfMaskOrOriginal()
    {
        using var workspace = new TemporaryWorkspace();
        var baseKey = "clean/page.png";
        var maskKey = "masks/page.png";
        var outputKey = "working/page/clean.png";
        WriteTestImage(Path.Combine(workspace.Root, baseKey), 40, 20, (_, _) => new SKColor(220, 40, 20));
        WriteTestImage(Path.Combine(workspace.Root, maskKey), 40, 20, (x, _) => x < 20 ? SKColors.White : SKColors.Black);
        var document = $$"""
            {"schema_version":"1.2.0","mask_base_asset_key":"{{maskKey}}","operations":[]}
            """;

        await PageImageEditRenderer.RenderAsync(workspace.Root, baseKey, document, outputKey, CancellationToken.None);

        using var output = SKBitmap.Decode(Path.Combine(workspace.Root, outputKey));
        Assert.Equal(new SKColor(220, 40, 20), output.GetPixel(8, 10));
        Assert.Equal(new SKColor(220, 40, 20), output.GetPixel(32, 10));
    }

    [Fact]
    public async Task UnsupportedLegacyToolsAreRejected()
    {
        using var workspace = new TemporaryWorkspace();
        var baseKey = "clean/page.png";
        var maskKey = "masks/page.png";
        var outputKey = "working/page/clean.png";
        WriteTestImage(Path.Combine(workspace.Root, baseKey), 40, 20, (_, _) => new SKColor(220, 40, 20));
        WriteTestImage(Path.Combine(workspace.Root, maskKey), 40, 20, (x, _) => x < 20 ? SKColors.White : SKColors.Black);
        var document = $$"""
            {"schema_version":"1.2.0","mask_base_asset_key":"{{maskKey}}","operations":[{"operation_id":"remove-1","tool":"mask-remove","size_px":10,"hardness":100,"opacity":100,"points":[{"x":8,"y":10}]}]}
            """;

        await Assert.ThrowsAsync<ArgumentException>(() => PageImageEditRenderer.RenderAsync(
            workspace.Root,
            baseKey,
            document,
            outputKey,
            CancellationToken.None));
    }

    private static void WriteTestImage(string path, int width, int height, Func<int, int, SKColor> color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void WriteTestJpeg(string path, int width, int height, Func<int, int, SKColor> color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"emt-image-edit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
