using MangaTranslator.Dialogue;
using MangaTranslator.Rendering;
using SkiaSharp;

namespace MangaTranslator.Pipeline.Integration.Tests;

public sealed class MixedCompositionTests
{
    [Fact]
    public void ComposeMixedPngDrawsOnlyTransparentRenderContentAboveSourceAtEightyPercentOpacity()
    {
        const float renderTransparency = 0.2f;
        const float renderOpacity = 1f - renderTransparency;
        var sourceColor = new SKColor(20, 80, 160);
        var source = CreateSolidPng(12, 10, sourceColor);
        var block = new RenderedTextBlock(
            "block-1",
            5,
            5,
            CreateTransparentWhiteTextBlockPng());
        var overlay = new PageCompositionData(
            "page-1",
            12,
            10,
            [new TextBlockPlacement(block, 2, 2)]);

        var mixed = PipelineTestArtifactWriter.ComposeMixedPng(
            source,
            overlay,
            renderOpacity,
            debug: true,
            "pixel-test.png");

        using var bitmap = SKBitmap.Decode(mixed);
        Assert.NotNull(bitmap);
        Assert.Equal(12, bitmap.Width);
        Assert.Equal(10, bitmap.Height);

        // The white render canvas is not part of the overlay. Both a page-background pixel
        // and a transparent pixel inside the rendered block remain byte-for-byte source color.
        Assert.Equal(sourceColor, bitmap.GetPixel(8, 5));
        Assert.Equal(sourceColor, bitmap.GetPixel(5, 5));

        // White glyph fill and its black contrast outline both survive the transparent overlay.
        AssertPixelNear(bitmap.GetPixel(4, 4), new SKColor(208, 220, 236));
        AssertPixelNear(bitmap.GetPixel(3, 4), new SKColor(4, 16, 32));

        // Debug block and page borders are also render-layer content at 80% opacity.
        AssertPixelNear(bitmap.GetPixel(2, 2), new SKColor(180, 46, 62));
        AssertPixelNear(bitmap.GetPixel(0, 0), new SKColor(34, 95, 220));
    }

    [Fact]
    public void ComposeMixedPngAppliesRotatedTransparentBlockAndDebugBorderAtEightyPercentOpacity()
    {
        const float renderTransparency = 0.2f;
        const float renderOpacity = 1f - renderTransparency;
        var sourceColor = new SKColor(20, 80, 160);
        var source = CreateSolidPng(12, 12, sourceColor);
        var block = new RenderedTextBlock(
            "rotated-block",
            4,
            4,
            CreateAsymmetricTransparentMarkerBlockPng());
        var overlay = new PageCompositionData(
            "rotated-page",
            12,
            12,
            [new TextBlockPlacement(
                block,
                4,
                4,
                new DialogueBoxRotation(2, 2, 90))]);

        var mixed = PipelineTestArtifactWriter.ComposeMixedPng(
            source,
            overlay,
            renderOpacity,
            debug: true,
            "rotated-pixel-test.png");

        using var bitmap = SKBitmap.Decode(mixed);
        Assert.NotNull(bitmap);

        // The two asymmetric marker pixels move clockwise around the block-local pivot.
        AssertPixelNear(bitmap.GetPixel(6, 6), new SKColor(208, 220, 236));
        AssertPixelNear(bitmap.GetPixel(5, 6), new SKColor(4, 16, 32));

        // The rotated block border follows the same pivot and is composited at 80% opacity.
        AssertPixelNear(bitmap.GetPixel(7, 6), new SKColor(180, 46, 62));
        AssertPixelNear(bitmap.GetPixel(0, 0), new SKColor(34, 95, 220));
        Assert.Equal(sourceColor, bitmap.GetPixel(9, 9));
    }

    private static byte[] CreateTransparentWhiteTextBlockPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            5,
            5,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(2, 2, SKColors.White);
        bitmap.SetPixel(1, 2, SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode a test block PNG.");
        return data.ToArray();
    }

    private static byte[] CreateAsymmetricTransparentMarkerBlockPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(2, 1, SKColors.White);
        bitmap.SetPixel(2, 2, SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode a rotated test block PNG.");
        return data.ToArray();
    }

    private static byte[] CreateSolidPng(int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul))
            ?? throw new InvalidOperationException("SkiaSharp could not create a test surface.");
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode a test PNG.");
        return data.ToArray();
    }

    private static void AssertPixelNear(SKColor actual, SKColor expected)
    {
        Assert.InRange(actual.Red, expected.Red - 1, expected.Red + 1);
        Assert.InRange(actual.Green, expected.Green - 1, expected.Green + 1);
        Assert.InRange(actual.Blue, expected.Blue - 1, expected.Blue + 1);
        Assert.Equal(byte.MaxValue, actual.Alpha);
    }
}
