using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd.Tests;

internal static class DetectionReviewArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<string> WritePageAsync(
        string outputDirectory,
        string modelPath,
        string modelSha256,
        string imagePath,
        byte[] encodedImage,
        TextDetectionResult result,
        int foregroundPixels,
        TimeSpan elapsed,
        MaskComparison? comparison = null,
        bool includeInput = true)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        using var input = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (input.Empty())
        {
            throw new InvalidDataException("The review input could not be decoded by OpenCV.");
        }

        var actualPixels = result.Mask.Pixels.ToArray();
        using var actualMask = Mat.FromPixelData(
            result.Height,
            result.Width,
            MatType.CV_8UC1,
            actualPixels);

        if (includeInput)
        {
            WriteImage(Path.Combine(outputDirectory, "input.png"), input);
        }

        WriteImage(Path.Combine(outputDirectory, "actual-mask.png"), actualMask);

        using var overlay = CreateOverlay(input, result);
        WriteImage(Path.Combine(outputDirectory, "detection-overlay.png"), overlay);

        int? referenceOnlyPixels = null;
        int? actualOnlyPixels = null;
        if (comparison is not null)
        {
            using var upstreamMask = Mat.FromPixelData(
                result.Height,
                result.Width,
                MatType.CV_8UC1,
                comparison.ExpectedPixels);
            WriteImage(Path.Combine(outputDirectory, "upstream-mask.png"), upstreamMask);

            var differencePixels = new byte[checked(actualPixels.Length * 3)];
            var actualOnly = 0;
            var referenceOnly = 0;
            for (var index = 0; index < actualPixels.Length; index++)
            {
                var actual = actualPixels[index] > 0;
                var reference = comparison.ExpectedPixels[index] > 0;
                var target = index * 3;
                if (actual && reference)
                {
                    differencePixels[target] = 255;
                    differencePixels[target + 1] = 255;
                    differencePixels[target + 2] = 255;
                }
                else if (actual)
                {
                    differencePixels[target + 2] = 255;
                    actualOnly++;
                }
                else if (reference)
                {
                    differencePixels[target] = 255;
                    referenceOnly++;
                }
            }

            actualOnlyPixels = actualOnly;
            referenceOnlyPixels = referenceOnly;
            using var difference = Mat.FromPixelData(
                result.Height,
                result.Width,
                MatType.CV_8UC3,
                differencePixels);
            WriteImage(Path.Combine(outputDirectory, "mask-diff.png"), difference);
        }

        var detectionJson = new
        {
            result.Width,
            result.Height,
            Regions = result.Regions.Select((region, index) => new
            {
                Index = index,
                region.X0,
                region.Y0,
                region.X1,
                region.Y1,
                region.Language,
                region.Orientation,
                region.FontSize,
                region.Confidence,
                Lines = region.Lines.Select(line => new
                {
                    line.Confidence,
                    Polygon = line.Polygon.Select(point => new { point.X, point.Y })
                })
            }),
            Mask = new
            {
                result.Mask.Width,
                result.Mask.Height,
                Encoding = "PNG; source API data is row-major 8-bit binary pixels",
                File = "actual-mask.png",
                ForegroundPixels = foregroundPixels,
                Sha256 = Convert.ToHexString(SHA256.HashData(actualPixels)).ToLowerInvariant()
            }
        };
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "detection-result.json"),
            detectionJson);

        var summaryJson = new
        {
            Upstream = new
            {
                Repository = "https://github.com/zyddnys/manga-image-translator",
                Commit = "95227a2bb0fd306cd4f0c104d57284026f991b3a"
            },
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Model = new
            {
                Path = modelPath,
                Sha256 = modelSha256
            },
            Input = new
            {
                Path = imagePath,
                File = includeInput ? "input.png" : null,
                Sha256 = Convert.ToHexString(SHA256.HashData(encodedImage)).ToLowerInvariant(),
                result.Width,
                result.Height
            },
            Detection = new
            {
                TextLineCount = result.Regions.Count,
                MaskForegroundPixels = foregroundPixels,
                ElapsedSeconds = elapsed.TotalSeconds
            },
            ReferenceComparison = comparison is null
                ? null
                : new
                {
                    comparison.Intersection,
                    comparison.Union,
                    comparison.Iou,
                    ActualOnlyPixels = actualOnlyPixels,
                    ReferenceOnlyPixels = referenceOnlyPixels,
                    DifferenceLegend = new
                    {
                        White = "present in both masks",
                        Red = "C# mask only",
                        Blue = "upstream Python mask only",
                        Black = "absent from both masks"
                    }
                }
        };
        await WriteJsonAsync(Path.Combine(outputDirectory, "summary.json"), summaryJson);

        return outputDirectory;
    }

    public static async Task WriteFailureAsync(
        string outputDirectory,
        string imagePath,
        TimeSpan elapsed,
        Exception exception)
    {
        Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "failure.json"),
            new
            {
                ImagePath = imagePath,
                ElapsedSeconds = elapsed.TotalSeconds,
                ExceptionType = exception.GetType().FullName,
                exception.Message,
                exception.StackTrace
            });
    }

    public static async Task WriteJsonAsync(string path, object value)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    public static string GetFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Mat CreateOverlay(Mat input, TextDetectionResult result)
    {
        var overlay = input.Clone();
        for (var index = 0; index < result.Regions.Count; index++)
        {
            var region = result.Regions[index];
            Cv2.Rectangle(
                overlay,
                new Point(region.X0, region.Y0),
                new Point(region.X1 - 1, region.Y1 - 1),
                new Scalar(0, 255, 0),
                2,
                LineTypes.AntiAlias);

            foreach (var line in region.Lines)
            {
                var polygon = line.Polygon
                    .Select(point => new Point(point.X, point.Y))
                    .ToArray();
                if (polygon.Length >= 2)
                {
                    Cv2.Polylines(
                        overlay,
                        [polygon],
                        true,
                        new Scalar(0, 165, 255),
                        2,
                        LineTypes.AntiAlias);
                }
            }

            var labelY = Math.Max(region.Y0 - 5, 14);
            Cv2.PutText(
                overlay,
                $"#{index} {region.Confidence:F3}",
                new Point(region.X0, labelY),
                HersheyFonts.HersheySimplex,
                0.45,
                new Scalar(255, 0, 255),
                1,
                LineTypes.AntiAlias);
        }

        return overlay;
    }

    private static void WriteImage(string path, Mat image)
    {
        if (!Cv2.ImWrite(path, image))
        {
            throw new IOException($"OpenCV could not write review image '{path}'.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record MaskComparison(
    byte[] ExpectedPixels,
    int Intersection,
    int Union,
    double Iou);
