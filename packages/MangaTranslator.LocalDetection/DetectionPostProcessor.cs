using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection;

internal static class DetectionPostProcessor
{
    private const float BlockLineOverlapThreshold = 0.4f;
    private const float MaskLineOverlapThreshold = 0.1f;

    public static TextDetectionResult Process(
        PreparedDetectionInput input,
        Tensor<float> blockTensor,
        Tensor<float> segmentationTensor,
        Tensor<float> lineTensor,
        OnnxTextMaskDetectorOptions options)
    {
        ValidateOutputShapes(blockTensor, segmentationTensor, lineTensor, options.InputSize);

        var blocks = DecodeBlocks(
            blockTensor,
            input.Original.Cols,
            input.Original.Rows,
            input.ScaledWidth,
            input.ScaledHeight,
            options);
        var rawMask = DecodeSegmentationMask(
            segmentationTensor,
            input.Original.Cols,
            input.Original.Rows,
            input.ScaledWidth,
            input.ScaledHeight,
            options.InputSize);
        var lines = DecodeLines(
            lineTensor,
            input.Original.Cols,
            input.Original.Rows,
            input.ScaledWidth,
            input.ScaledHeight,
            options);

        var grouped = GroupLines(blocks, lines, rawMask, input.Original.Cols, input.Original.Rows);
        var refinedMask = MaskRefiner.Refine(
            input.Original,
            rawMask,
            grouped,
            options.KeepUndetectedMask);

        var regions = grouped
            .Where(block => options.IncludeUnknownLanguageBlocks
                || block.Language != DetectedTextLanguage.Unknown)
            .Select(ToPublicRegion)
            .ToArray();

        return new TextDetectionResult(
            input.Original.Cols,
            input.Original.Rows,
            regions,
            new MaskImage(input.Original.Cols, input.Original.Rows, refinedMask));
    }

    private static void ValidateOutputShapes(
        Tensor<float> blocks,
        Tensor<float> segmentation,
        Tensor<float> lines,
        int inputSize)
    {
        if (blocks.Dimensions.Length != 3 || blocks.Dimensions[0] != 1 || blocks.Dimensions[2] < 7)
        {
            throw new InvalidOperationException("The ONNX 'blk' output has an unexpected shape.");
        }

        if (segmentation.Dimensions.Length != 4
            || segmentation.Dimensions[0] != 1
            || segmentation.Dimensions[1] != 1
            || segmentation.Dimensions[2] != inputSize
            || segmentation.Dimensions[3] != inputSize)
        {
            throw new InvalidOperationException("The ONNX 'seg' output has an unexpected shape.");
        }

        if (lines.Dimensions.Length != 4
            || lines.Dimensions[0] != 1
            || lines.Dimensions[1] < 1
            || lines.Dimensions[2] != inputSize
            || lines.Dimensions[3] != inputSize)
        {
            throw new InvalidOperationException("The ONNX 'det' output has an unexpected shape.");
        }
    }

    private static List<MutableBlock> DecodeBlocks(
        Tensor<float> tensor,
        int imageWidth,
        int imageHeight,
        int scaledWidth,
        int scaledHeight,
        OnnxTextMaskDetectorOptions options)
    {
        var xScale = imageWidth / (float)scaledWidth;
        var yScale = imageHeight / (float)scaledHeight;
        var candidates = new List<BlockCandidate>();

        for (var index = 0; index < tensor.Dimensions[1]; index++)
        {
            var objectConfidence = tensor[0, index, 4];
            if (objectConfidence <= options.BlockConfidenceThreshold)
            {
                continue;
            }

            var classId = 0;
            var classConfidence = tensor[0, index, 5];
            for (var classIndex = 1; classIndex < tensor.Dimensions[2] - 5; classIndex++)
            {
                var candidate = tensor[0, index, 5 + classIndex];
                if (candidate > classConfidence)
                {
                    classId = classIndex;
                    classConfidence = candidate;
                }
            }

            var confidence = objectConfidence * classConfidence;
            if (confidence <= options.BlockConfidenceThreshold)
            {
                continue;
            }

            var centerX = tensor[0, index, 0];
            var centerY = tensor[0, index, 1];
            var width = tensor[0, index, 2];
            var height = tensor[0, index, 3];
            var x0 = Math.Clamp((centerX - width / 2f) * xScale, 0, imageWidth - 1);
            var y0 = Math.Clamp((centerY - height / 2f) * yScale, 0, imageHeight - 1);
            var x1 = Math.Clamp((centerX + width / 2f) * xScale, 0, imageWidth);
            var y1 = Math.Clamp((centerY + height / 2f) * yScale, 0, imageHeight);
            if (x1 - x0 < 2 || y1 - y0 < 2)
            {
                continue;
            }

            candidates.Add(new BlockCandidate(
                new FloatBox(x0, y0, x1, y1),
                confidence,
                classId));
        }

        var selected = new List<BlockCandidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
        {
            var suppressed = selected.Any(existing =>
                existing.ClassId == candidate.ClassId
                && IntersectionOverUnion(existing.Box, candidate.Box) > options.BlockIouThreshold);
            if (suppressed)
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= options.MaxDetections)
            {
                break;
            }
        }

        return selected
            .Select(item => new MutableBlock(
                item.Box.ToIntBox(imageWidth, imageHeight),
                ToLanguage(item.ClassId),
                item.Confidence))
            .ToList();
    }

    private static byte[] DecodeSegmentationMask(
        Tensor<float> tensor,
        int imageWidth,
        int imageHeight,
        int scaledWidth,
        int scaledHeight,
        int inputSize)
    {
        var networkMask = new byte[checked(inputSize * inputSize)];
        for (var y = 0; y < inputSize; y++)
        {
            for (var x = 0; x < inputSize; x++)
            {
                networkMask[y * inputSize + x] = (byte)Math.Clamp(
                    (int)Math.Round(tensor[0, 0, y, x] * 255f),
                    0,
                    255);
            }
        }

        using var network = Mat.FromPixelData(inputSize, inputSize, MatType.CV_8UC1, networkMask);
        using var cropped = new Mat(network, new Rect(0, 0, scaledWidth, scaledHeight));
        using var resized = new Mat();
        Cv2.Resize(cropped, resized, new Size(imageWidth, imageHeight), interpolation: InterpolationFlags.Linear);
        resized.GetArray(out byte[] result);
        return result;
    }

    private static List<LineCandidate> DecodeLines(
        Tensor<float> tensor,
        int imageWidth,
        int imageHeight,
        int scaledWidth,
        int scaledHeight,
        OnnxTextMaskDetectorOptions options)
    {
        var inputSize = options.InputSize;
        var bitmap = new byte[checked(inputSize * inputSize)];
        for (var y = 0; y < inputSize; y++)
        {
            for (var x = 0; x < inputSize; x++)
            {
                bitmap[y * inputSize + x] = tensor[0, 0, y, x] > options.LineBitmapThreshold
                    ? byte.MaxValue
                    : byte.MinValue;
            }
        }

        using var binary = Mat.FromPixelData(inputSize, inputSize, MatType.CV_8UC1, bitmap);
        var contours = Cv2.FindContoursAsArray(
            binary,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);
        var xScale = imageWidth / (float)scaledWidth;
        var yScale = imageHeight / (float)scaledHeight;
        var result = new List<LineCandidate>();

        foreach (var contour in contours.Take(1000))
        {
            if (contour.Length < 3 || Math.Abs(Cv2.ContourArea(contour)) < 2)
            {
                continue;
            }

            var rect = Cv2.MinAreaRect(contour);
            if (Math.Min(rect.Size.Width, rect.Size.Height) < 2)
            {
                continue;
            }

            var bounds = Cv2.BoundingRect(contour);
            var score = AverageLineScore(tensor, bounds, inputSize);
            if (score <= options.LineScoreThreshold)
            {
                continue;
            }

            var perimeter = 2d * (rect.Size.Width + rect.Size.Height);
            var offset = perimeter <= 0
                ? 0
                : rect.Size.Width * rect.Size.Height * 1.5d / perimeter;
            var expandedRect = new RotatedRect(
                rect.Center,
                new Size2f(
                    Math.Max(1, rect.Size.Width + (float)(2 * offset)),
                    Math.Max(1, rect.Size.Height + (float)(2 * offset))),
                rect.Angle);
            var points = expandedRect.Points()
                .Select(point => new ImagePoint(
                    Math.Clamp((int)Math.Round(point.X * xScale), 0, imageWidth - 1),
                    Math.Clamp((int)Math.Round(point.Y * yScale), 0, imageHeight - 1)))
                .ToArray();
            result.Add(new LineCandidate(points, score));
        }

        return result;
    }

    private static float AverageLineScore(Tensor<float> tensor, Rect bounds, int inputSize)
    {
        var x0 = Math.Clamp(bounds.X, 0, inputSize - 1);
        var y0 = Math.Clamp(bounds.Y, 0, inputSize - 1);
        var x1 = Math.Clamp(bounds.Right, x0 + 1, inputSize);
        var y1 = Math.Clamp(bounds.Bottom, y0 + 1, inputSize);
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                sum += tensor[0, 0, y, x];
                count++;
            }
        }

        return count == 0 ? 0 : (float)(sum / count);
    }

    private static List<MutableBlock> GroupLines(
        List<MutableBlock> blocks,
        List<LineCandidate> lines,
        byte[] mask,
        int imageWidth,
        int imageHeight)
    {
        foreach (var line in lines)
        {
            var lineBox = IntBox.FromPoints(line.Points, imageWidth, imageHeight);
            var lineArea = Math.Max(1, lineBox.Area);
            var bestScore = -1f;
            MutableBlock? bestBlock = null;
            foreach (var block in blocks)
            {
                var score = IntersectionArea(block.Box, lineBox) / (float)lineArea;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBlock = block;
                }
            }

            if (bestBlock is not null && bestScore > BlockLineOverlapThreshold)
            {
                bestBlock.Lines.Add(line);
                continue;
            }

            if (MeanMask(mask, imageWidth, lineBox) > MaskLineOverlapThreshold)
            {
                var scattered = new MutableBlock(lineBox, DetectedTextLanguage.Unknown, line.Confidence);
                scattered.Lines.Add(line);
                blocks.Add(scattered);
            }
        }

        foreach (var block in blocks)
        {
            if (block.Lines.Count == 0)
            {
                block.Lines.Add(new LineCandidate(
                    [
                        new ImagePoint(block.Box.X0, block.Box.Y0),
                        new ImagePoint(block.Box.X1, block.Box.Y0),
                        new ImagePoint(block.Box.X1, block.Box.Y1),
                        new ImagePoint(block.Box.X0, block.Box.Y1)
                    ],
                    block.Confidence));
            }

            block.UpdateGeometry();
        }

        return blocks
            .OrderBy(block => block.Box.Y0)
            .ThenByDescending(block => block.Box.X0)
            .ToList();
    }

    private static DetectedTextRegion ToPublicRegion(MutableBlock block)
    {
        return new DetectedTextRegion(
            block.Box.X0,
            block.Box.Y0,
            block.Box.X1,
            block.Box.Y1,
            block.Language,
            block.Orientation,
            block.FontSize,
            block.Confidence,
            block.Lines
                .Select(line => new DetectedTextLine(line.Points, line.Confidence))
                .ToArray());
    }

    private static DetectedTextLanguage ToLanguage(int classId) => classId switch
    {
        0 => DetectedTextLanguage.English,
        1 => DetectedTextLanguage.Japanese,
        _ => DetectedTextLanguage.Unknown
    };

    private static float MeanMask(byte[] mask, int width, IntBox box)
    {
        long sum = 0;
        var count = 0;
        for (var y = box.Y0; y < box.Y1; y++)
        {
            var offset = y * width;
            for (var x = box.X0; x < box.X1; x++)
            {
                sum += mask[offset + x];
                count++;
            }
        }

        return count == 0 ? 0 : sum / (255f * count);
    }

    private static float IntersectionOverUnion(FloatBox left, FloatBox right)
    {
        var intersection = Math.Max(0, Math.Min(left.X1, right.X1) - Math.Max(left.X0, right.X0))
            * Math.Max(0, Math.Min(left.Y1, right.Y1) - Math.Max(left.Y0, right.Y0));
        if (intersection <= 0)
        {
            return 0;
        }

        var union = left.Area + right.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static int IntersectionArea(IntBox left, IntBox right)
    {
        var width = Math.Max(0, Math.Min(left.X1, right.X1) - Math.Max(left.X0, right.X0));
        var height = Math.Max(0, Math.Min(left.Y1, right.Y1) - Math.Max(left.Y0, right.Y0));
        return width * height;
    }

    private sealed record BlockCandidate(FloatBox Box, float Confidence, int ClassId);

    private readonly record struct FloatBox(float X0, float Y0, float X1, float Y1)
    {
        public float Area => Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0);

        public IntBox ToIntBox(int imageWidth, int imageHeight) => new(
            Math.Clamp((int)Math.Floor(X0), 0, imageWidth - 1),
            Math.Clamp((int)Math.Floor(Y0), 0, imageHeight - 1),
            Math.Clamp((int)Math.Ceiling(X1), 1, imageWidth),
            Math.Clamp((int)Math.Ceiling(Y1), 1, imageHeight));
    }

    internal readonly record struct IntBox(int X0, int Y0, int X1, int Y1)
    {
        public int Width => Math.Max(0, X1 - X0);

        public int Height => Math.Max(0, Y1 - Y0);

        public int Area => Width * Height;

        public static IntBox FromPoints(
            IReadOnlyList<ImagePoint> points,
            int imageWidth,
            int imageHeight)
        {
            return new IntBox(
                Math.Clamp(points.Min(point => point.X), 0, imageWidth - 1),
                Math.Clamp(points.Min(point => point.Y), 0, imageHeight - 1),
                Math.Clamp(points.Max(point => point.X) + 1, 1, imageWidth),
                Math.Clamp(points.Max(point => point.Y) + 1, 1, imageHeight));
        }
    }

    internal sealed record LineCandidate(IReadOnlyList<ImagePoint> Points, float Confidence);

    internal sealed class MutableBlock
    {
        public MutableBlock(IntBox box, DetectedTextLanguage language, float confidence)
        {
            Box = box;
            Language = language;
            Confidence = confidence;
        }

        public IntBox Box { get; private set; }

        public DetectedTextLanguage Language { get; }

        public float Confidence { get; }

        public List<LineCandidate> Lines { get; } = [];

        public DetectedTextOrientation Orientation { get; private set; }

        public int FontSize { get; private set; }

        public void UpdateGeometry()
        {
            var points = Lines.SelectMany(line => line.Points).ToArray();
            var x0 = Math.Min(Box.X0, points.Min(point => point.X));
            var y0 = Math.Min(Box.Y0, points.Min(point => point.Y));
            var x1 = Math.Max(Box.X1, points.Max(point => point.X) + 1);
            var y1 = Math.Max(Box.Y1, points.Max(point => point.Y) + 1);
            Box = new IntBox(x0, y0, x1, y1);

            var verticalVotes = Lines.Count(line =>
            {
                var lineBox = IntBox.FromPoints(line.Points, int.MaxValue, int.MaxValue);
                return lineBox.Height > lineBox.Width * (Language == DetectedTextLanguage.Japanese ? 1f : 2f);
            });
            Orientation = verticalVotes > Lines.Count / 2
                ? DetectedTextOrientation.Vertical
                : DetectedTextOrientation.Horizontal;

            var sizes = Lines.Select(line =>
            {
                var lineBox = IntBox.FromPoints(line.Points, int.MaxValue, int.MaxValue);
                return Orientation == DetectedTextOrientation.Vertical
                    ? lineBox.Width
                    : lineBox.Height;
            });
            FontSize = Math.Max(6, (int)Math.Round(sizes.Average()));
        }
    }
}
