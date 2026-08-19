using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd;

internal sealed class DetectionImage : IDisposable
{
    private DetectionImage(Mat original, Mat working, bool hasBorder)
    {
        Original = original;
        Working = working;
        HasBorder = hasBorder;
    }

    public Mat Original { get; }

    public Mat Working { get; }

    public bool HasBorder { get; }

    public static DetectionImage Decode(
        ReadOnlyMemory<byte> encodedImage,
        int minimumImageSize)
    {
        if (encodedImage.IsEmpty)
        {
            throw new ArgumentException("Image bytes must not be empty.", nameof(encodedImage));
        }

        var original = Cv2.ImDecode(encodedImage.Span, ImreadModes.Color);
        if (original.Empty())
        {
            original.Dispose();
            throw new ArgumentException("The supplied bytes are not a supported image.", nameof(encodedImage));
        }

        if (minimumImageSize <= 0 || Math.Min(original.Cols, original.Rows) >= minimumImageSize)
        {
            return new DetectionImage(original, original, hasBorder: false);
        }

        try
        {
            var side = Math.Max(Math.Max(original.Cols, original.Rows), minimumImageSize);
            var working = new Mat(side, side, MatType.CV_8UC3, Scalar.Black);
            using (var target = new Mat(working, new Rect(0, 0, original.Cols, original.Rows)))
            {
                original.CopyTo(target);
            }

            return new DetectionImage(original, working, hasBorder: true);
        }
        catch
        {
            original.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(Working, Original))
        {
            Working.Dispose();
        }

        Original.Dispose();
    }
}

internal sealed class PreparedModelImage(Mat image, int contentWidth, int contentHeight) : IDisposable
{
    public Mat Image { get; } = image;

    public int ContentWidth { get; } = contentWidth;

    public int ContentHeight { get; } = contentHeight;

    public void Dispose() => Image.Dispose();
}

internal static class ImagePreprocessor
{
    public static PreparedModelImage PrepareDirect(Mat imageBgr, int inputSize)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(imageBgr, rgb, ColorConversionCodes.BGR2RGB);

        var scale = Math.Min(
            inputSize / (double)rgb.Rows,
            inputSize / (double)rgb.Cols);
        var scaledWidth = Math.Max(1, (int)Math.Round(rgb.Cols * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(rgb.Rows * scale));

        using var resized = new Mat();
        Cv2.Resize(rgb, resized, new Size(scaledWidth, scaledHeight), interpolation: InterpolationFlags.Linear);

        var padded = new Mat(inputSize, inputSize, MatType.CV_8UC3, Scalar.Black);
        using (var target = new Mat(padded, new Rect(0, 0, scaledWidth, scaledHeight)))
        {
            resized.CopyTo(target);
        }

        return new PreparedModelImage(padded, scaledWidth, scaledHeight);
    }

    public static DenseTensor<float> ToTensor(Mat image)
    {
        if (image.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("The model image must be an 8-bit three-channel image.", nameof(image));
        }

        if (!image.IsContinuous())
        {
            image = image.Clone();
            try
            {
                return ToContinuousTensor(image);
            }
            finally
            {
                image.Dispose();
            }
        }

        return ToContinuousTensor(image);
    }

    private static DenseTensor<float> ToContinuousTensor(Mat image)
    {
        image.GetArray(out Vec3b[] pixels);
        var planeSize = checked(image.Rows * image.Cols);
        var values = new float[checked(planeSize * 3)];
        const float scale = 1f / 255f;
        for (var index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            values[index] = pixel.Item0 * scale;
            values[planeSize + index] = pixel.Item1 * scale;
            values[2 * planeSize + index] = pixel.Item2 * scale;
        }

        return new DenseTensor<float>(values, [1, 3, image.Rows, image.Cols]);
    }
}
