using System.Runtime.InteropServices;
using MangaTranslator.Cleaning;
using MangaTranslator.LocalDetection;
using OpenCvSharp;

namespace MangaTranslator.Cleaning.Tests;

public sealed class TextCleanerTests
{
    [Fact]
    public async Task AppliesOnlyTheMaskInsideClip()
    {
        using var source = new Mat(40, 80, MatType.CV_8UC3, new Scalar(255, 255, 255));
        Cv2.Rectangle(source, new Rect(10, 10, 10, 10), Scalar.Black, -1);
        Cv2.Rectangle(source, new Rect(50, 10, 10, 10), Scalar.Black, -1);
        var maskPixels = CreateMaskPixels(80, 40, new Rect(10, 10, 10, 10), new Rect(50, 10, 10, 10));
        var cleaner = new OpenCvTextCleaner();

        var result = await cleaner.CleanAsync(new TextCleaningRequest(
            Encode(source),
            new MaskImage(80, 40, maskPixels),
            new ImageClip(0, 0, 30, 40),
            "image/png"));

        using var cleaned = Cv2.ImDecode(result.ImageBytes.ToArray(), ImreadModes.Color);
        Assert.Equal(255, cleaned.At<Vec3b>(15, 15).Item0);
        Assert.Equal(0, cleaned.At<Vec3b>(15, 55).Item0);
        Assert.True(result.AppliedMaskPixels > 0);

        var sourceRows = source.Rows;
        var sourceCols = source.Cols;
        for (var y = 0; y < sourceRows; y++)
        {
            for (var x = 30; x < sourceCols; x++)
            {
                Assert.Equal(source.At<Vec3b>(y, x), cleaned.At<Vec3b>(y, x));
            }
        }
    }

    [Fact]
    public async Task UsesTheLocalBackgroundColorForDarkPanels()
    {
        var panelColor = new Scalar(42, 58, 73);
        using var source = new Mat(60, 90, MatType.CV_8UC3, panelColor);
        Cv2.Rectangle(source, new Rect(30, 20, 20, 15), new Scalar(245, 245, 245), -1);
        var maskPixels = CreateMaskPixels(90, 60, new Rect(30, 20, 20, 15));
        var cleaner = new OpenCvTextCleaner(new OpenCvTextCleanerOptions
        {
            FlatBackgroundMaximumDeviation = 2,
            BackgroundSampleRadius = 4
        });

        var result = await cleaner.CleanAsync(new TextCleaningRequest(
            Encode(source),
            new MaskImage(90, 60, maskPixels),
            new ImageClip(20, 10, 40, 35)));

        using var cleaned = Cv2.ImDecode(result.ImageBytes.ToArray(), ImreadModes.Color);
        var pixel = cleaned.At<Vec3b>(27, 37);
        Assert.InRange(pixel.Item0, 40, 44);
        Assert.InRange(pixel.Item1, 56, 60);
        Assert.InRange(pixel.Item2, 71, 75);
        Assert.Equal(1, result.SolidFillComponents);
        Assert.Equal(0, result.InpaintedComponents);
    }

    [Fact]
    public async Task RemovesAntialiasedEdgesAroundTheDetectorCore()
    {
        using var source = new Mat(90, 140, MatType.CV_8UC3, Scalar.White);
        Cv2.PutText(
            source,
            "EDGE",
            new Point(12, 62),
            HersheyFonts.HersheySimplex,
            1.25,
            Scalar.Black,
            3,
            LineTypes.AntiAlias);
        using var gray = new Mat();
        using var detectorMask = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, detectorMask, 48, 255, ThresholdTypes.BinaryInv);
        var cleaner = new OpenCvTextCleaner();

        var result = await cleaner.CleanAsync(new TextCleaningRequest(
            Encode(source),
            new MaskImage(140, 90, ReadMaskPixels(detectorMask)),
            new ImageClip(0, 0, 140, 90)));

        using var cleaned = Cv2.ImDecode(result.ImageBytes.ToArray(), ImreadModes.Grayscale);
        using var remainingInk = new Mat();
        Cv2.Threshold(cleaned, remainingInk, 245, 255, ThresholdTypes.BinaryInv);
        Assert.Equal(0, Cv2.CountNonZero(remainingInk));
    }

    [Fact]
    public async Task RestoresDenseScreentoneWithoutWhiteOutlineBlocks()
    {
        const int width = 180;
        const int height = 120;
        using var background = new Mat(height, width, MatType.CV_8UC3);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(((x + y) & 3) == 0 ? 92 : 168);
                background.Set(y, x, new Vec3b(value, value, value));
            }
        }

        using var source = background.Clone();
        using var detectorMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        Cv2.PutText(
            source,
            "TEXT",
            new Point(15, 78),
            HersheyFonts.HersheySimplex,
            1.6,
            Scalar.White,
            9,
            LineTypes.AntiAlias);
        Cv2.PutText(
            source,
            "TEXT",
            new Point(15, 78),
            HersheyFonts.HersheySimplex,
            1.6,
            Scalar.Black,
            3,
            LineTypes.AntiAlias);
        Cv2.PutText(
            detectorMask,
            "TEXT",
            new Point(15, 78),
            HersheyFonts.HersheySimplex,
            1.6,
            Scalar.White,
            3,
            LineTypes.AntiAlias);
        Cv2.Threshold(detectorMask, detectorMask, 48, 255, ThresholdTypes.Binary);
        var cleaner = new OpenCvTextCleaner();

        var result = await cleaner.CleanAsync(new TextCleaningRequest(
            Encode(source),
            new MaskImage(width, height, ReadMaskPixels(detectorMask)),
            new ImageClip(0, 0, width, height),
            "image/png",
            TextBackgroundType.RandomScreentone));

        using var cleaned = Cv2.ImDecode(result.ImageBytes.ToArray(), ImreadModes.Color);
        using var affected = new Mat();
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(11, 11)))
        {
            Cv2.Dilate(detectorMask, affected, kernel);
        }

        var affectedPixels = Cv2.CountNonZero(affected);
        var totalError = 0L;
        var whiteArtifacts = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (affected.At<byte>(y, x) == 0)
                {
                    continue;
                }

                var expected = background.At<Vec3b>(y, x).Item0;
                var actual = cleaned.At<Vec3b>(y, x).Item0;
                totalError += Math.Abs(actual - expected);
                if (actual > 235 && expected < 200)
                {
                    whiteArtifacts++;
                }
            }
        }

        Assert.True(result.InpaintedComponents > 0);
        Assert.True(totalError / (double)affectedPixels < 32);
        Assert.True(whiteArtifacts / (double)affectedPixels < 0.01);
    }

    [Fact]
    public async Task RejectsMismatchedMaskDimensions()
    {
        using var source = new Mat(20, 20, MatType.CV_8UC3, Scalar.White);
        var cleaner = new OpenCvTextCleaner();

        await Assert.ThrowsAsync<TextCleaningException>(() => cleaner.CleanAsync(new TextCleaningRequest(
            Encode(source),
            new MaskImage(10, 10, new byte[100]),
            new ImageClip(0, 0, 20, 20))).AsTask());
    }

    private static byte[] CreateMaskPixels(int width, int height, params Rect[] rectangles)
    {
        var pixels = new byte[checked(width * height)];
        foreach (var rectangle in rectangles)
        {
            for (var y = rectangle.Y; y < rectangle.Bottom; y++)
            {
                for (var x = rectangle.X; x < rectangle.Right; x++)
                {
                    pixels[y * width + x] = 255;
                }
            }
        }

        return pixels;
    }

    private static byte[] Encode(Mat image)
    {
        Assert.True(Cv2.ImEncode(".png", image, out var bytes));
        return bytes;
    }

    private static byte[] ReadMaskPixels(Mat mask)
    {
        var pixels = new byte[checked(mask.Rows * mask.Cols)];
        Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
        return pixels;
    }

}
