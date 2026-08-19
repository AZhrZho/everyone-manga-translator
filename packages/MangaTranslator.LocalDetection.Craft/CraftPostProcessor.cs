using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Craft;

internal static class CraftPostProcessor
{
    public static TextDetectionResult Process(
        CraftModelInput input,
        Tensor<float> scores,
        CraftTextMaskDetectorOptions options,
        CancellationToken cancellationToken)
    {
        var maps = ReadMaps(scores);
        var usableMapWidth = Math.Min(maps.Width, (input.ContentWidth + 1) / 2);
        var usableMapHeight = Math.Min(maps.Height, (input.ContentHeight + 1) / 2);
        using var textMap = CreateMap(maps.Text, maps.Width, maps.Height, usableMapWidth, usableMapHeight);
        using var linkMap = CreateMap(maps.Link, maps.Width, maps.Height, usableMapWidth, usableMapHeight);
        var detections = DetectRegions(textMap, linkMap, options, cancellationToken);
        try
        {
            var regions = detections
                .Take(options.MaximumRegions)
                .Select(item => ToRegion(item, input, textMap.Cols, textMap.Rows))
                .ToArray();
            var mask = CraftMaskSynthesizer.Build(
                input.OriginalBgr,
                textMap,
                detections,
                input.ContentWidth,
                input.ContentHeight,
                options,
                cancellationToken);
            return new TextDetectionResult(
                input.OriginalBgr.Cols,
                input.OriginalBgr.Rows,
                regions,
                new MaskImage(input.OriginalBgr.Cols, input.OriginalBgr.Rows, mask));
        }
        finally
        {
            foreach (var detection in detections)
            {
                detection.Dispose();
            }
        }
    }

    private static CraftScoreMaps ReadMaps(Tensor<float> scores)
    {
        if (scores.Dimensions.Length != 4 || scores.Dimensions[0] != 1)
        {
            throw new InvalidOperationException("The CRAFT score output must have four dimensions and batch size one.");
        }

        var channelsLast = scores.Dimensions[3] == 2;
        var channelsFirst = scores.Dimensions[1] == 2;
        if (channelsLast == channelsFirst)
        {
            throw new InvalidOperationException("The CRAFT score output must contain exactly one two-channel dimension.");
        }

        var height = channelsLast ? scores.Dimensions[1] : scores.Dimensions[2];
        var width = channelsLast ? scores.Dimensions[2] : scores.Dimensions[3];
        if (height <= 0 || width <= 0)
        {
            throw new InvalidOperationException("The CRAFT score output has invalid spatial dimensions.");
        }

        var text = new float[checked(width * height)];
        var link = new float[text.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                text[index] = channelsLast ? scores[0, y, x, 0] : scores[0, 0, y, x];
                link[index] = channelsLast ? scores[0, y, x, 1] : scores[0, 1, y, x];
            }
        }

        return new CraftScoreMaps(text, link, width, height);
    }

    private static Mat CreateMap(float[] pixels, int width, int height, int usableWidth, int usableHeight)
    {
        using var complete = Mat.FromPixelData(height, width, MatType.CV_32FC1, pixels);
        return new Mat(complete, new Rect(0, 0, usableWidth, usableHeight)).Clone();
    }

    private static List<CraftDetection> DetectRegions(
        Mat textMap,
        Mat linkMap,
        CraftTextMaskDetectorOptions options,
        CancellationToken cancellationToken)
    {
        using var textScore = new Mat();
        using var linkScore = new Mat();
        Cv2.Threshold(textMap, textScore, options.LowTextThreshold, 1, ThresholdTypes.Binary);
        Cv2.Threshold(linkMap, linkScore, options.LinkThreshold, 1, ThresholdTypes.Binary);
        textScore.ConvertTo(textScore, MatType.CV_8UC1);
        linkScore.ConvertTo(linkScore, MatType.CV_8UC1);
        using var combined = new Mat();
        Cv2.Add(textScore, linkScore, combined);
        Cv2.Threshold(combined, combined, 0, 1, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            combined,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity4,
            MatType.CV_32S);
        var result = new List<CraftDetection>(Math.Min(count, options.MaximumRegions));
        try
        {
            for (var label = 1; label < count && result.Count < options.MaximumRegions; label++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = stats.At<int>(label, 4);
                if (size < options.MinimumComponentArea)
                {
                    continue;
                }

                using var labelMask = new Mat();
                Cv2.InRange(labels, new Scalar(label), new Scalar(label), labelMask);
                Cv2.MinMaxLoc(textMap, out _, out var peak, out _, out _, labelMask);
                if (peak < options.TextThreshold)
                {
                    continue;
                }

                using var segment = labelMask.Clone();
                using (var linkOnly = new Mat())
                {
                    using var notText = new Mat();
                    Cv2.Compare(textScore, Scalar.All(0), notText, CmpTypes.EQ);
                    Cv2.BitwiseAnd(linkScore, notText, linkOnly);
                    segment.SetTo(Scalar.Black, linkOnly);
                }

                var x = stats.At<int>(label, 0);
                var y = stats.At<int>(label, 1);
                var width = stats.At<int>(label, 2);
                var height = stats.At<int>(label, 3);
                var iterations = (int)(Math.Sqrt(size * Math.Min(width, height) / (double)(width * height)) * 2);
                var roi = Intersect(
                    new Rect(x - iterations, y - iterations, width + iterations * 2 + 1, height + iterations * 2 + 1),
                    textMap.Cols,
                    textMap.Rows);
                using (var segmentRoi = new Mat(segment, roi))
                using (var kernel = Cv2.GetStructuringElement(
                           MorphShapes.Rect,
                           new Size(1 + iterations, 1 + iterations)))
                {
                    Cv2.Dilate(segmentRoi, segmentRoi, kernel);
                }

                using var pointIndices = new Mat();
                Cv2.FindNonZero(segment, pointIndices);
                if (pointIndices.Empty() || pointIndices.Rows < 3)
                {
                    continue;
                }

                pointIndices.GetArray(out Point[] points);
                var box = Cv2.MinAreaRect(points);
                var corners = box.Points();
                var sideA = Distance(corners[0], corners[1]);
                var sideB = Distance(corners[1], corners[2]);
                if (Math.Abs(1 - Math.Max(sideA, sideB) / (Math.Min(sideA, sideB) + 1e-5)) <= 0.1)
                {
                    var bounds = Cv2.BoundingRect(points);
                    corners =
                    [
                        new Point2f(bounds.Left, bounds.Top),
                        new Point2f(bounds.Right, bounds.Top),
                        new Point2f(bounds.Right, bounds.Bottom),
                        new Point2f(bounds.Left, bounds.Bottom)
                    ];
                }

                result.Add(new CraftDetection(
                    OrderClockwise(corners),
                    Math.Clamp((float)peak, 0, 1),
                    labelMask.Clone()));
            }

            return result;
        }
        catch
        {
            foreach (var detection in result)
            {
                detection.Dispose();
            }

            throw;
        }
    }

    private static DetectedTextRegion ToRegion(
        CraftDetection detection,
        CraftModelInput input,
        int mapWidth,
        int mapHeight)
    {
        var points = detection.Box
            .Select(point => new ImagePoint(
                CraftCoordinateMapper.MapX(point.X, mapWidth, input.OriginalBgr.Cols),
                CraftCoordinateMapper.MapY(point.Y, mapHeight, input.OriginalBgr.Rows)))
            .ToArray();
        var x0 = points.Min(point => point.X);
        var y0 = points.Min(point => point.Y);
        var x1 = Math.Min(input.OriginalBgr.Cols, points.Max(point => point.X) + 1);
        var y1 = Math.Min(input.OriginalBgr.Rows, points.Max(point => point.Y) + 1);
        var orientation = y1 - y0 > x1 - x0
            ? DetectedTextOrientation.Vertical
            : DetectedTextOrientation.Horizontal;
        var fontSize = Math.Max(1, orientation == DetectedTextOrientation.Vertical ? x1 - x0 : y1 - y0);
        return new DetectedTextRegion(
            x0,
            y0,
            Math.Max(x0 + 1, x1),
            Math.Max(y0 + 1, y1),
            DetectedTextLanguage.Unknown,
            orientation,
            fontSize,
            detection.Confidence,
            [new DetectedTextLine(points, detection.Confidence)]);
    }

    private static Point2f[] OrderClockwise(Point2f[] points)
    {
        var start = Enumerable.Range(0, points.Length)
            .MinBy(index => points[index].X + points[index].Y);
        return points.Skip(start).Concat(points.Take(start)).ToArray();
    }

    private static Rect Intersect(Rect rect, int width, int height)
    {
        var x0 = Math.Clamp(rect.X, 0, width);
        var y0 = Math.Clamp(rect.Y, 0, height);
        var x1 = Math.Clamp(rect.Right, x0, width);
        var y1 = Math.Clamp(rect.Bottom, y0, height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static double Distance(Point2f first, Point2f second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record CraftScoreMaps(float[] Text, float[] Link, int Width, int Height);
}

internal sealed record CraftDetection(Point2f[] Box, float Confidence, Mat ComponentMask) : IDisposable
{
    public void Dispose() => ComponentMask.Dispose();
}
