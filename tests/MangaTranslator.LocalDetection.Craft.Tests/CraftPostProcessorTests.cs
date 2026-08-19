using MangaTranslator.LocalDetection.Craft;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Craft.Tests;

public sealed class CraftPostProcessorTests
{
    [Fact]
    public void CropsCanvasPaddingAndKeepsRegionAndMaskInTheSamePageSpace()
    {
        using var source = new Mat(77, 101, MatType.CV_8UC3, Scalar.White);
        Assert.True(Cv2.ImEncode(".png", source, out var encoded));
        var options = new CraftTextMaskDetectorOptions
        {
            CanvasSize = 128,
            MagnificationRatio = 1,
            MinimumComponentArea = 1,
            MinimumGlyphArea = 1,
            GlyphMaskDilationRadius = 0
        };
        using var input = CraftModelInput.Prepare(encoded, options);
        Assert.Equal(101, input.ContentWidth);
        Assert.Equal(77, input.ContentHeight);

        // The padded 128x96 input produces a 64x48 map. Only the first 51x39
        // cells describe content. The second blob is deliberately in padding.
        var scores = new DenseTensor<float>(new[] { 1, 48, 64, 2 });
        FillTextScore(scores, x0: 45, y0: 15, width: 4, height: 4);
        FillTextScore(scores, x0: 58, y0: 15, width: 4, height: 4);

        var result = CraftPostProcessor.Process(input, scores, options, CancellationToken.None);

        var region = Assert.Single(result.Regions);
        Assert.InRange(region.X0, 80, 100);
        Assert.InRange(region.X1, region.X0 + 1, 101);

        var nonZero = result.Mask.Pixels.Span
            .ToArray()
            .Select((value, index) => (value, x: index % result.Width, y: index / result.Width))
            .Where(item => item.value != 0)
            .ToArray();
        Assert.NotEmpty(nonZero);
        Assert.All(nonZero, pixel =>
        {
            Assert.InRange(pixel.x, region.X0, region.X1 - 1);
            Assert.InRange(pixel.y, region.Y0, region.Y1 - 1);
        });
    }

    [Fact]
    public void KeepsWeakPunctuationConnectedToStrongTextByAffinityWithoutMaskingAffinity()
    {
        using var source = new Mat(64, 128, MatType.CV_8UC3, Scalar.White);
        Assert.True(Cv2.ImEncode(".png", source, out var encoded));
        var options = new CraftTextMaskDetectorOptions
        {
            CanvasSize = 128,
            MagnificationRatio = 1,
            MinimumComponentArea = 1,
            MinimumGlyphArea = 1
        };
        using var input = CraftModelInput.Prepare(encoded, options);
        var scores = new DenseTensor<float>(new[] { 1, 32, 64, 2 });

        FillTextScore(scores, x0: 10, y0: 10, width: 5, height: 10, score: 0.9f);
        FillTextScore(scores, x0: 22, y0: 11, width: 2, height: 2, score: 0.45f);
        FillTextScore(scores, x0: 22, y0: 15, width: 2, height: 2, score: 0.45f);
        FillLinkScore(scores, x0: 15, y0: 11, width: 7, height: 6, score: 0.9f);

        var result = CraftPostProcessor.Process(input, scores, options, CancellationToken.None);
        var mask = result.Mask.Pixels.Span;

        Assert.Single(result.Regions);
        Assert.True(AnyForeground(mask, result.Width, x0: 43, y0: 21, width: 7, height: 7));
        Assert.True(AnyForeground(mask, result.Width, x0: 43, y0: 29, width: 7, height: 7));
        Assert.False(AnyForeground(mask, result.Width, x0: 34, y0: 24, width: 7, height: 7));
    }

    private static void FillTextScore(
        DenseTensor<float> scores,
        int x0,
        int y0,
        int width,
        int height,
        float score = 0.9f)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                scores[0, y, x, 0] = score;
            }
        }
    }

    private static void FillLinkScore(
        DenseTensor<float> scores,
        int x0,
        int y0,
        int width,
        int height,
        float score)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                scores[0, y, x, 1] = score;
            }
        }
    }

    private static bool AnyForeground(
        ReadOnlySpan<byte> mask,
        int imageWidth,
        int x0,
        int y0,
        int width,
        int height)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                if (mask[x + y * imageWidth] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
