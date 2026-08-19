using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd;

internal static class CtdPostProcessor
{
    private const double UnclipRatio = 1.5;

    public static TextDetectionResult Process(
        Mat workingBgr,
        Mat originalBgr,
        bool hasBorder,
        ModelInferenceMaps maps,
        OnnxTextMaskDetectorOptions options,
        CancellationToken cancellationToken)
    {
        ValidateMaps(maps);
        var rawMask = DecodeMask(maps, workingBgr.Cols, workingBgr.Rows);
        var workingLines = DecodeLines(maps, workingBgr.Cols, workingBgr.Rows, options);
        cancellationToken.ThrowIfCancellationRequested();

        var refinedMask = LatestMaskRefiner.Refine(
            workingBgr,
            rawMask,
            workingLines,
            options.KeepUndetectedMask);

        var resultLines = hasBorder
            ? RestoreOriginalGeometry(workingLines, originalBgr.Cols, originalBgr.Rows)
            : workingLines;
        var resultMask = hasBorder
            ? CropMask(refinedMask, workingBgr.Cols, originalBgr.Cols, originalBgr.Rows)
            : refinedMask;

        var regions = options.IncludeUnknownLanguageBlocks
            ? resultLines.Select(line => ToRegion(line, originalBgr.Cols, originalBgr.Rows)).ToArray()
            : [];

        return new TextDetectionResult(
            originalBgr.Cols,
            originalBgr.Rows,
            regions,
            new MaskImage(originalBgr.Cols, originalBgr.Rows, resultMask));
    }

    private static void ValidateMaps(ModelInferenceMaps maps)
    {
        if (maps.LineChannels < 1
            || maps.LineHeight <= 0
            || maps.LineWidth <= 0
            || maps.Lines.Length != checked(maps.LineChannels * maps.LineHeight * maps.LineWidth))
        {
            throw new InvalidOperationException("The CTD line map has invalid dimensions.");
        }

        if (maps.MaskHeight <= 0
            || maps.MaskWidth <= 0
            || maps.Mask.Length != checked(maps.MaskHeight * maps.MaskWidth))
        {
            throw new InvalidOperationException("The CTD segmentation map has invalid dimensions.");
        }
    }

    private static byte[] DecodeMask(ModelInferenceMaps maps, int imageWidth, int imageHeight)
    {
        var pixels = new byte[maps.Mask.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)Math.Clamp((int)(maps.Mask[index] * 255f), 0, 255);
        }

        using var source = Mat.FromPixelData(maps.MaskHeight, maps.MaskWidth, MatType.CV_8UC1, pixels);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(imageWidth, imageHeight), interpolation: InterpolationFlags.Linear);
        resized.GetArray(out byte[] result);
        return result;
    }

    private static List<CtdTextLine> DecodeLines(
        ModelInferenceMaps maps,
        int imageWidth,
        int imageHeight,
        OnnxTextMaskDetectorOptions options)
    {
        var planeSize = checked(maps.LineHeight * maps.LineWidth);
        var prediction = new float[planeSize];
        Array.Copy(maps.Lines, prediction, planeSize);

        var bitmap = new byte[planeSize];
        for (var index = 0; index < planeSize; index++)
        {
            bitmap[index] = prediction[index] > options.LineBitmapThreshold
                ? byte.MaxValue
                : byte.MinValue;
        }

        using var predictionMat = Mat.FromPixelData(
            maps.LineHeight,
            maps.LineWidth,
            MatType.CV_32FC1,
            prediction);
        using var bitmapMat = Mat.FromPixelData(
            maps.LineHeight,
            maps.LineWidth,
            MatType.CV_8UC1,
            bitmap);
        var contours = Cv2.FindContoursAsArray(
            bitmapMat,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        var lines = new List<CtdTextLine>(Math.Min(contours.Length, options.MaxDetections));
        foreach (var contour in contours.Take(options.MaxDetections))
        {
            if (contour.Length < 3)
            {
                continue;
            }

            var rect = Cv2.MinAreaRect(contour);
            if (Math.Min(rect.Size.Width, rect.Size.Height) < 2)
            {
                continue;
            }

            var confidence = PolygonMean(predictionMat, contour);
            if (confidence <= options.LineScoreThreshold)
            {
                continue;
            }

            var perimeter = 2d * (rect.Size.Width + rect.Size.Height);
            var distance = perimeter <= 0
                ? 0
                : rect.Size.Width * rect.Size.Height * UnclipRatio / perimeter;
            var integerMiniBox = rect.Points()
                .Select(point => new Point((int)point.X, (int)point.Y))
                .ToArray();
            var quantizedRect = Cv2.MinAreaRect(integerMiniBox);
            var integerOffset = Math.Round(distance);
            var expanded = new RotatedRect(
                quantizedRect.Center,
                new Size2f(
                    Math.Max(1, quantizedRect.Size.Width + (float)(2 * integerOffset)),
                    Math.Max(1, quantizedRect.Size.Height + (float)(2 * integerOffset))),
                quantizedRect.Angle);

            var points = expanded.Points()
                .Select(point => new ImagePoint(
                    Math.Clamp(
                        (int)Math.Round(point.X / maps.LineWidth * imageWidth),
                        0,
                        imageWidth),
                    Math.Clamp(
                        (int)Math.Round(point.Y / maps.LineHeight * imageHeight),
                        0,
                        imageHeight)))
                .ToArray();
            var line = CreateLine(points, confidence, imageWidth, imageHeight);
            if (line is not null && PolygonArea(line.Points) > 1)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static float PolygonMean(Mat prediction, Point[] contour)
    {
        var bounds = Cv2.BoundingRect(contour);
        bounds = bounds.Intersect(new Rect(0, 0, prediction.Cols, prediction.Rows));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return 0;
        }

        var shifted = contour
            .Select(point => new Point(point.X - bounds.X, point.Y - bounds.Y))
            .ToArray();
        using var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(mask, [shifted], Scalar.White);
        using var region = new Mat(prediction, bounds);
        return (float)Cv2.Mean(region, mask).Val0;
    }

    private static List<CtdTextLine> RestoreOriginalGeometry(
        IEnumerable<CtdTextLine> lines,
        int imageWidth,
        int imageHeight)
    {
        var restored = new List<CtdTextLine>();
        foreach (var line in lines)
        {
            if (line.Box.X0 >= imageWidth && line.Box.Y0 >= imageHeight)
            {
                continue;
            }

            var points = line.Points
                .Select(point => new ImagePoint(
                    Math.Clamp(point.X, 0, imageWidth),
                    Math.Clamp(point.Y, 0, imageHeight)))
                .ToArray();
            var clipped = CreateLine(points, line.Confidence, imageWidth, imageHeight);
            if (clipped is not null && PolygonArea(clipped.Points) > 1)
            {
                restored.Add(clipped);
            }
        }

        return restored;
    }

    private static CtdTextLine? CreateLine(
        ImagePoint[] points,
        float confidence,
        int imageWidth,
        int imageHeight)
    {
        if (points.Length != 4)
        {
            return null;
        }

        var sorted = SortPoints(points, out var orientation);
        var x0 = Math.Clamp(sorted.Min(point => point.X), 0, Math.Max(0, imageWidth - 1));
        var y0 = Math.Clamp(sorted.Min(point => point.Y), 0, Math.Max(0, imageHeight - 1));
        var x1 = Math.Clamp(sorted.Max(point => point.X) + 1, x0 + 1, imageWidth);
        var y1 = Math.Clamp(sorted.Max(point => point.Y) + 1, y0 + 1, imageHeight);
        var fontSize = ComputeFontSize(sorted);
        return new CtdTextLine(
            sorted,
            confidence,
            orientation,
            Math.Max(1, (int)Math.Round(fontSize)),
            new IntBox(x0, y0, x1, y1));
    }

    private static ImagePoint[] SortPoints(
        IReadOnlyList<ImagePoint> points,
        out DetectedTextOrientation orientation)
    {
        var pairs = new List<(double Length, double X, double Y)>(16);
        for (var left = 0; left < 4; left++)
        {
            for (var right = 0; right < 4; right++)
            {
                var x = points[left].X - points[right].X;
                var y = points[left].Y - points[right].Y;
                pairs.Add((Math.Sqrt(x * x + y * y), x, y));
            }
        }

        var orderedVectors = pairs.OrderBy(pair => pair.Length).ToArray();
        var first = orderedVectors[8];
        var second = orderedVectors[10];
        if (first.X * second.X + first.Y * second.Y < 0)
        {
            first = (first.Length, -first.X, -first.Y);
        }

        var structureX = Math.Abs((first.X + second.X) / 2d);
        var structureY = Math.Abs((first.Y + second.Y) / 2d);
        var vertical = structureX <= structureY;
        orientation = vertical
            ? DetectedTextOrientation.Vertical
            : DetectedTextOrientation.Horizontal;

        if (vertical)
        {
            var byY = points.OrderBy(point => point.Y).ThenBy(point => point.X).ToArray();
            var top = byY[..2].OrderBy(point => point.X);
            var bottom = byY[2..].OrderByDescending(point => point.X);
            return top.Concat(bottom).ToArray();
        }

        var byX = points.OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        var result = new ImagePoint[4];
        var leftPair = byX[..2].OrderBy(point => point.Y).ToArray();
        var rightPair = byX[2..].OrderBy(point => point.Y).ToArray();
        result[0] = leftPair[0];
        result[3] = leftPair[1];
        result[1] = rightPair[0];
        result[2] = rightPair[1];
        return result;
    }

    private static double ComputeFontSize(IReadOnlyList<ImagePoint> points)
    {
        var p1 = Midpoint(points[0], points[1]);
        var p2 = Midpoint(points[2], points[3]);
        var p3 = Midpoint(points[1], points[2]);
        var p4 = Midpoint(points[3], points[0]);
        return Math.Min(Distance(p1, p2), Distance(p3, p4));
    }

    private static (double X, double Y) Midpoint(ImagePoint left, ImagePoint right) =>
        ((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static double Distance((double X, double Y) left, (double X, double Y) right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static double PolygonArea(IReadOnlyList<ImagePoint> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            area += points[index].X * points[next].Y - points[next].X * points[index].Y;
        }

        return Math.Abs(area) / 2d;
    }

    private static DetectedTextRegion ToRegion(CtdTextLine line, int imageWidth, int imageHeight)
    {
        var x0 = Math.Clamp(line.Box.X0, 0, imageWidth - 1);
        var y0 = Math.Clamp(line.Box.Y0, 0, imageHeight - 1);
        var x1 = Math.Clamp(line.Box.X1, x0 + 1, imageWidth);
        var y1 = Math.Clamp(line.Box.Y1, y0 + 1, imageHeight);
        return new DetectedTextRegion(
            x0,
            y0,
            x1,
            y1,
            DetectedTextLanguage.Unknown,
            line.Orientation,
            line.FontSize,
            line.Confidence,
            [new DetectedTextLine(line.Points, line.Confidence)]);
    }

    private static byte[] CropMask(byte[] source, int sourceWidth, int width, int height)
    {
        var result = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(source, y * sourceWidth, result, y * width, width);
        }

        return result;
    }
}
