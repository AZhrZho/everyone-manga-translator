using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Craft;

internal sealed class CraftModelInput : IDisposable
{
    private CraftModelInput(
        Mat originalBgr,
        DenseTensor<float> tensor,
        int contentWidth,
        int contentHeight)
    {
        OriginalBgr = originalBgr;
        Tensor = tensor;
        ContentWidth = contentWidth;
        ContentHeight = contentHeight;
    }

    public Mat OriginalBgr { get; }

    public DenseTensor<float> Tensor { get; }

    public int ContentWidth { get; }

    public int ContentHeight { get; }

    public static CraftModelInput Prepare(
        ReadOnlyMemory<byte> encodedImage,
        CraftTextMaskDetectorOptions options)
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

        try
        {
            var longestSide = Math.Max(original.Cols, original.Rows);
            var targetSize = Math.Min(options.MagnificationRatio * longestSide, options.CanvasSize);
            var scale = targetSize / longestSide;
            var contentWidth = Math.Max(1, (int)(original.Cols * scale));
            var contentHeight = Math.Max(1, (int)(original.Rows * scale));
            var canvasWidth = RoundUpTo32(contentWidth);
            var canvasHeight = RoundUpTo32(contentHeight);

            using var rgb = new Mat();
            Cv2.CvtColor(original, rgb, ColorConversionCodes.BGR2RGB);
            using var resized = new Mat();
            Cv2.Resize(rgb, resized, new Size(contentWidth, contentHeight), interpolation: InterpolationFlags.Linear);
            using var canvas = new Mat(canvasHeight, canvasWidth, MatType.CV_8UC3, Scalar.Black);
            using (var target = new Mat(canvas, new Rect(0, 0, contentWidth, contentHeight)))
            {
                resized.CopyTo(target);
            }

            return new CraftModelInput(
                original,
                ToNormalizedTensor(canvas),
                contentWidth,
                contentHeight);
        }
        catch
        {
            original.Dispose();
            throw;
        }
    }

    public void Dispose() => OriginalBgr.Dispose();

    private static DenseTensor<float> ToNormalizedTensor(Mat rgb)
    {
        rgb.GetArray(out Vec3b[] pixels);
        var planeSize = checked(rgb.Rows * rgb.Cols);
        var values = new float[checked(planeSize * 3)];
        ReadOnlySpan<float> mean = [0.485f, 0.456f, 0.406f];
        ReadOnlySpan<float> deviation = [0.229f, 0.224f, 0.225f];
        for (var index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            values[index] = (pixel.Item0 / 255f - mean[0]) / deviation[0];
            values[planeSize + index] = (pixel.Item1 / 255f - mean[1]) / deviation[1];
            values[2 * planeSize + index] = (pixel.Item2 / 255f - mean[2]) / deviation[2];
        }

        return new DenseTensor<float>(values, [1, 3, rgb.Rows, rgb.Cols]);
    }

    private static int RoundUpTo32(int value) => checked((value + 31) / 32 * 32);
}
