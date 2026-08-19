using System.Diagnostics;
using System.Text.Json;
using MangaTranslator.LocalDetection;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MangaTranslator.LocalDetection.Tests;

public sealed class OnnxTextMaskDetectorSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DetectAsync_runs_the_real_model_without_writing_files()
    {
        var modelPath = GetOptionalFileFromEnvironment("MANGA_TRANSLATOR_DETECTOR_MODEL");
        var imagePath = GetOptionalFileFromEnvironment("MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE");
        if (modelPath is null || imagePath is null)
        {
            output.WriteLine(
                "Real-model validation was not configured; set MANGA_TRANSLATOR_DETECTOR_MODEL and MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE.");
            return;
        }
        var model = await File.ReadAllBytesAsync(modelPath);
        var image = await File.ReadAllBytesAsync(imagePath);
        using var detector = new OnnxTextMaskDetector(model);

        var stopwatch = Stopwatch.StartNew();
        var result = await detector.DetectAsync(new DetectionInput(image, "image/png"));
        stopwatch.Stop();

        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.Equal(result.Width, result.Mask.Width);
        Assert.Equal(result.Height, result.Mask.Height);
        Assert.Equal(result.Width * result.Height, result.Mask.Pixels.Length);
        Assert.NotEmpty(result.Regions);
        Assert.Contains(result.Regions, region => region.Language == DetectedTextLanguage.Japanese);
        Assert.All(result.Regions, region =>
        {
            Assert.InRange(region.X0, 0, result.Width - 1);
            Assert.InRange(region.Y0, 0, result.Height - 1);
            Assert.InRange(region.X1, region.X0 + 1, result.Width);
            Assert.InRange(region.Y1, region.Y0 + 1, result.Height);
            Assert.True(region.FontSize >= 6);
            Assert.NotEmpty(region.Lines);
        });

        var foregroundPixels = 0;
        foreach (var value in result.Mask.Pixels.Span)
        {
            Assert.True(value is 0 or 255);
            if (value == 255)
            {
                foregroundPixels++;
            }
        }

        Assert.True(foregroundPixels > 0);
        var expectedMaskPath = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_EXPECTED_MASK");
        if (!string.IsNullOrWhiteSpace(expectedMaskPath) && File.Exists(expectedMaskPath))
        {
            var expectedBytes = await File.ReadAllBytesAsync(expectedMaskPath);
            using var expected = Cv2.ImDecode(expectedBytes, ImreadModes.Grayscale);
            Assert.Equal(result.Width, expected.Cols);
            Assert.Equal(result.Height, expected.Rows);
            expected.GetArray(out byte[] expectedPixels);

            var intersection = 0;
            var union = 0;
            var actualPixels = result.Mask.Pixels.Span;
            for (var index = 0; index < actualPixels.Length; index++)
            {
                var actualSet = actualPixels[index] > 0;
                var expectedSet = expectedPixels[index] > 0;
                if (actualSet && expectedSet)
                {
                    intersection++;
                }

                if (actualSet || expectedSet)
                {
                    union++;
                }
            }

            var maskIou = union == 0 ? 1d : intersection / (double)union;
            output.WriteLine(
                "Golden mask IoU: {0:F4}; intersection={1}; union={2}; expected={3}; actual={4}.",
                maskIou,
                intersection,
                union,
                expectedPixels.Count(value => value > 0),
                foregroundPixels);
            for (var regionIndex = 0; regionIndex < result.Regions.Count; regionIndex++)
            {
                var region = result.Regions[regionIndex];
                output.WriteLine(
                    "Region {0}: ({1},{2})-({3},{4}) {5} {6} font={7} lines={8}.",
                    regionIndex,
                    region.X0,
                    region.Y0,
                    region.X1,
                    region.Y1,
                    region.Language,
                    region.Orientation,
                    region.FontSize,
                    region.Lines.Count);
            }
            Assert.True(maskIou >= 0.98, $"Golden mask IoU was only {maskIou:F4}.");
        }

        var expectedBlocksPath = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_EXPECTED_BLOCKS");
        if (!string.IsNullOrWhiteSpace(expectedBlocksPath) && File.Exists(expectedBlocksPath))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(expectedBlocksPath));
            var expectedBlocks = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(expectedBlocks.Length, result.Regions.Count);
            for (var index = 0; index < expectedBlocks.Length; index++)
            {
                var expected = expectedBlocks[index];
                var xyxy = expected.GetProperty("xyxy")
                    .EnumerateArray()
                    .Select(value => value.GetInt32())
                    .ToArray();
                var actual = result.Regions[index];
                Assert.InRange(Math.Abs(actual.X0 - xyxy[0]), 0, 5);
                Assert.InRange(Math.Abs(actual.Y0 - xyxy[1]), 0, 5);
                Assert.InRange(Math.Abs(actual.X1 - xyxy[2]), 0, 5);
                Assert.InRange(Math.Abs(actual.Y1 - xyxy[3]), 0, 5);
                Assert.Equal(expected.GetProperty("vertical").GetBoolean(),
                    actual.Orientation == DetectedTextOrientation.Vertical);
                Assert.InRange(
                    Math.Abs(actual.FontSize - expected.GetProperty("font_size").GetInt32()),
                    0,
                    2);
            }
        }

        output.WriteLine(
            "Detected {0} regions and {1} mask pixels in {2:F3}s.",
            result.Regions.Count,
            foregroundPixels,
            stopwatch.Elapsed.TotalSeconds);
    }

    private static string? GetOptionalFileFromEnvironment(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return path;
    }
}
