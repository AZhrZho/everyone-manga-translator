using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MangaTranslator.Cleaning;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MangaTranslator.LocalDetection.Craft.Tests;

public sealed class CraftRealModelSmokeTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task RunsARealPageAndWritesReviewArtifacts()
    {
        var modelPath = GetOptionalFile("MANGA_TRANSLATOR_CRAFT_MODEL", "CRAFT_SMOKE_MODEL");
        var imagePath = GetOptionalFile("MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE", "CRAFT_SMOKE_IMAGE");
        if (modelPath is null || imagePath is null)
        {
            output.WriteLine(
                "Real CRAFT validation was not configured; set MANGA_TRANSLATOR_CRAFT_MODEL and MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE.");
            return;
        }

        var configuredOutput = FirstEnvironmentVariable(
            "MANGA_TRANSLATOR_CRAFT_ARTIFACTS",
            "CRAFT_SMOKE_OUTPUT");
        var outputDirectory = Path.GetFullPath(configuredOutput ?? Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            ".artifacts",
            "craft-poc",
            Path.GetFileNameWithoutExtension(imagePath)));
        Directory.CreateDirectory(outputDirectory);

        var modelBytes = await File.ReadAllBytesAsync(modelPath);
        var imageBytes = await File.ReadAllBytesAsync(imagePath);
        var modelSha256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();

        object modelContract;
        using (var session = new InferenceSession(modelBytes))
        {
            modelContract = new
            {
                Inputs = session.InputMetadata.Select(item => new
                {
                    item.Key,
                    ElementType = item.Value.ElementType.FullName,
                    item.Value.Dimensions
                }),
                Outputs = session.OutputMetadata.Select(item => new
                {
                    item.Key,
                    ElementType = item.Value.ElementType.FullName,
                    item.Value.Dimensions
                })
            };
        }

        var modelLoadStopwatch = Stopwatch.StartNew();
        using var detector = new CraftTextMaskDetector(modelBytes);
        modelLoadStopwatch.Stop();
        var inferenceStopwatch = Stopwatch.StartNew();
        var result = await detector.DetectAsync(new DetectionInput(imageBytes, "image/png"));
        inferenceStopwatch.Stop();

        var maskPixels = result.Mask.Pixels.ToArray();
        var foregroundPixels = maskPixels.Count(value => value != 0);
        using var input = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        using var mask = Mat.FromPixelData(result.Height, result.Width, MatType.CV_8UC1, maskPixels);
        using var overlay = input.Clone();
        foreach (var region in result.Regions)
        {
            foreach (var line in region.Lines)
            {
                var polygon = line.Polygon.Select(point => new Point(point.X, point.Y)).ToArray();
                Cv2.Polylines(overlay, [polygon], true, new Scalar(0, 165, 255), 2, LineTypes.AntiAlias);
            }
        }

        Assert.True(Cv2.ImWrite(Path.Combine(outputDirectory, "mask.png"), mask));
        Assert.True(Cv2.ImWrite(Path.Combine(outputDirectory, "actual-mask.png"), mask));
        Assert.True(Cv2.ImWrite(Path.Combine(outputDirectory, "input.png"), input));
        Assert.True(Cv2.ImWrite(Path.Combine(outputDirectory, "overlay.png"), overlay));
        var cleaner = new OpenCvTextCleaner();
        var cleaningStopwatch = Stopwatch.StartNew();
        var cleaned = await cleaner.CleanAsync(
            new TextCleaningRequest(
                imageBytes,
                result.Mask,
                new ImageClip(0, 0, result.Width, result.Height),
                "image/png"));
        cleaningStopwatch.Stop();
        await File.WriteAllBytesAsync(
            Path.Combine(outputDirectory, "clean.png"),
            cleaned.ImageBytes.ToArray());

        var summary = new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Model = new
            {
                FileName = Path.GetFileName(modelPath),
                SizeBytes = modelBytes.Length,
                Sha256 = modelSha256,
                Contract = modelContract
            },
            Input = new
            {
                FileName = Path.GetFileName(imagePath),
                SizeBytes = imageBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant(),
                result.Width,
                result.Height
            },
            Detection = new
            {
                ComponentCount = result.Regions.Count,
                LineCount = result.Regions.Sum(region => region.Lines.Count),
                MaskForegroundPixels = foregroundPixels,
                MaskCoverage = (double)foregroundPixels / maskPixels.Length,
                ModelLoadMilliseconds = modelLoadStopwatch.Elapsed.TotalMilliseconds,
                InferenceMilliseconds = inferenceStopwatch.Elapsed.TotalMilliseconds,
                CleaningMilliseconds = cleaningStopwatch.Elapsed.TotalMilliseconds,
                EndToEndMilliseconds = modelLoadStopwatch.Elapsed.TotalMilliseconds
                                       + inferenceStopwatch.Elapsed.TotalMilliseconds
                                       + cleaningStopwatch.Elapsed.TotalMilliseconds
            },
            Artifacts = new
            {
                Input = "input.png",
                Mask = "mask.png",
                ActualMask = "actual-mask.png",
                Overlay = "overlay.png",
                Clean = "clean.png"
            },
            Regions = result.Regions
        };
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.json"), json);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "detection-result.json"), json);

        Assert.Equal(input.Width, result.Width);
        Assert.Equal(input.Height, result.Height);
        Assert.Equal(result.Width * result.Height, maskPixels.Length);
        Assert.NotEmpty(result.Regions);
        Assert.True(foregroundPixels > 0);
        output.WriteLine(
            "Detected {0} components and {1} mask pixels in {2:F1} ms; artifacts: {3}",
            result.Regions.Count,
            foregroundPixels,
            inferenceStopwatch.Elapsed.TotalMilliseconds,
            outputDirectory);
    }

    private static string? GetOptionalFile(params string[] names)
    {
        var path = FirstEnvironmentVariable(names);
        return path is not null && File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? FirstEnvironmentVariable(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
