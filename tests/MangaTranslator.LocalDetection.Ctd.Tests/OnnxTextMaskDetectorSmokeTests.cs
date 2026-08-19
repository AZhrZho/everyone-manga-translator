using System.Diagnostics;
using OpenCvSharp;
using Xunit.Abstractions;
using Latest = MangaTranslator.LocalDetection.Ctd;

namespace MangaTranslator.LocalDetection.Ctd.Tests;

public sealed class OnnxTextMaskDetectorSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DetectAsync_runs_the_latest_ctd_path_in_memory()
    {
        var modelPath = GetOptionalFile("MANGA_TRANSLATOR_DETECTOR_MODEL");
        var imagePath = GetOptionalFile("MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE");
        if (modelPath is null || imagePath is null)
        {
            output.WriteLine(
                "Real-model validation was not configured; set MANGA_TRANSLATOR_DETECTOR_MODEL and MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE.");
            return;
        }

        var model = await File.ReadAllBytesAsync(modelPath);
        var image = await File.ReadAllBytesAsync(imagePath);
        using ITextMaskDetector detector = new Latest.OnnxTextMaskDetector(model);

        var stopwatch = Stopwatch.StartNew();
        var result = await detector.DetectAsync(new DetectionInput(image, "image/png"));
        stopwatch.Stop();

        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.Equal(result.Width, result.Mask.Width);
        Assert.Equal(result.Height, result.Mask.Height);
        Assert.NotEmpty(result.Regions);
        Assert.All(result.Regions, region =>
        {
            Assert.Equal(DetectedTextLanguage.Unknown, region.Language);
            Assert.Single(region.Lines);
            Assert.InRange(region.X0, 0, result.Width - 1);
            Assert.InRange(region.Y0, 0, result.Height - 1);
            Assert.InRange(region.X1, region.X0 + 1, result.Width);
            Assert.InRange(region.Y1, region.Y0 + 1, result.Height);
            Assert.True(region.FontSize > 0);
        });

        for (var index = 0; index < result.Regions.Count; index++)
        {
            var region = result.Regions[index];
            output.WriteLine(
                "Line {0}: ({1},{2})-({3},{4}) {5} confidence={6:F6} polygon={7}.",
                index,
                region.X0,
                region.Y0,
                region.X1,
                region.Y1,
                region.Orientation,
                region.Confidence,
                string.Join(' ', region.Lines[0].Polygon.Select(point => $"[{point.X},{point.Y}]")));
        }

        var foregroundPixels = 0;
        foreach (var value in result.Mask.Pixels.Span)
        {
            Assert.True(value is 0 or 255);
            foregroundPixels += value == 255 ? 1 : 0;
        }

        Assert.True(foregroundPixels > 0);
        MaskComparison? comparison = null;
        var expectedMaskPath = GetOptionalFile("MANGA_TRANSLATOR_LATEST_EXPECTED_MASK");
        if (expectedMaskPath is not null)
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
                var actual = actualPixels[index] > 0;
                var reference = expectedPixels[index] > 0;
                intersection += actual && reference ? 1 : 0;
                union += actual || reference ? 1 : 0;
            }

            var iou = union == 0 ? 1d : intersection / (double)union;
            comparison = new MaskComparison(expectedPixels, intersection, union, iou);
            output.WriteLine("Latest upstream mask IoU: {0:F4}.", iou);
            Assert.True(iou >= 0.98, $"Latest upstream mask IoU was only {iou:F4}.");
        }

        var artifactsPath = Environment.GetEnvironmentVariable(
            "MANGA_TRANSLATOR_DETECTOR_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifactsPath))
        {
            var exportedPath = await DetectionReviewArtifactWriter.WritePageAsync(
                artifactsPath,
                modelPath,
                DetectionReviewArtifactWriter.GetFileSha256(modelPath),
                imagePath,
                image,
                result,
                foregroundPixels,
                stopwatch.Elapsed,
                comparison);
            output.WriteLine("Review artifacts: {0}", exportedPath);
        }

        output.WriteLine(
            "Detected {0} text lines and {1} mask pixels in {2:F3}s.",
            result.Regions.Count,
            foregroundPixels,
            stopwatch.Elapsed.TotalSeconds);
    }

    private static string? GetOptionalFile(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }
}
