using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection;

internal sealed class PreparedDetectionInput : IDisposable
{
    public PreparedDetectionInput(
        Mat original,
        DenseTensor<float> tensor,
        int scaledWidth,
        int scaledHeight)
    {
        Original = original;
        Tensor = tensor;
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
    }

    public Mat Original { get; }

    public DenseTensor<float> Tensor { get; }

    public int ScaledWidth { get; }

    public int ScaledHeight { get; }

    public void Dispose() => Original.Dispose();
}

internal static class ImagePreprocessor
{
    public static PreparedDetectionInput Prepare(ReadOnlyMemory<byte> encodedImage, int inputSize)
    {
        if (encodedImage.IsEmpty)
        {
            throw new ArgumentException("Image bytes must not be empty.", nameof(encodedImage));
        }

        if (inputSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSize));
        }

        var original = Cv2.ImDecode(encodedImage.Span, ImreadModes.Color);
        if (original.Empty())
        {
            original.Dispose();
            throw new ArgumentException("The supplied bytes are not a supported image.", nameof(encodedImage));
        }

        try
        {
            var scale = Math.Min(
                inputSize / (double)original.Rows,
                inputSize / (double)original.Cols);
            var scaledWidth = Math.Max(1, (int)Math.Round(original.Cols * scale));
            var scaledHeight = Math.Max(1, (int)Math.Round(original.Rows * scale));

            using var rgb = new Mat();
            Cv2.CvtColor(original, rgb, ColorConversionCodes.BGR2RGB);

            using var resized = new Mat();
            Cv2.Resize(
                rgb,
                resized,
                new Size(scaledWidth, scaledHeight),
                interpolation: InterpolationFlags.Linear);

            using var padded = new Mat(
                inputSize,
                inputSize,
                MatType.CV_8UC3,
                Scalar.Black);
            using (var target = new Mat(padded, new Rect(0, 0, scaledWidth, scaledHeight)))
            {
                resized.CopyTo(target);
            }

            var tensor = new DenseTensor<float>([1, 3, inputSize, inputSize]);
            for (var y = 0; y < inputSize; y++)
            {
                for (var x = 0; x < inputSize; x++)
                {
                    var pixel = padded.At<Vec3b>(y, x);
                    tensor[0, 0, y, x] = pixel.Item0 / 255f;
                    tensor[0, 1, y, x] = pixel.Item1 / 255f;
                    tensor[0, 2, y, x] = pixel.Item2 / 255f;
                }
            }

            return new PreparedDetectionInput(original, tensor, scaledWidth, scaledHeight);
        }
        catch
        {
            original.Dispose();
            throw;
        }
    }
}
