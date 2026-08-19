using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd;

/// <summary>
/// Port of manga-image-translator detection/ctd_utils/textmask.py as used by
/// the latest CTD detector with refine_mode=None.
/// </summary>
internal static class LatestMaskRefiner
{
    private const byte PredictionThreshold = 60;

    public static byte[] Refine(
        Mat imageBgr,
        byte[] rawMask,
        IReadOnlyList<CtdTextLine> lines,
        bool keepUndetectedMask)
    {
        using var prediction = Mat.FromPixelData(
            imageBgr.Rows,
            imageBgr.Cols,
            MatType.CV_8UC1,
            rawMask);
        using var refined = RefineLines(imageBgr, prediction, lines);

        if (keepUndetectedMask)
        {
            RecoverUndetectedMask(imageBgr, prediction, refined, lines);
        }

        Cv2.Threshold(refined, refined, 1, 255, ThresholdTypes.Binary);
        refined.GetArray(out byte[] result);
        return result;
    }

    private static Mat RefineLines(
        Mat imageBgr,
        Mat prediction,
        IReadOnlyList<CtdTextLine> lines)
    {
        var refined = new Mat(imageBgr.Rows, imageBgr.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var line in lines)
        {
            var window = EnlargeWindow(line.Box, imageBgr.Cols, imageBgr.Rows);
            if (window.Width <= 0 || window.Height <= 0)
            {
                continue;
            }

            using var imageRegion = new Mat(imageBgr, window);
            using var predictionRegion = new Mat(prediction, window);
            using var refinedRegion = new Mat(refined, window);
            var masks = GetTopKMaskList(imageRegion, predictionRegion);
            try
            {
                masks.Add(GetBestOtsuMask(imageRegion, predictionRegion));
                using var merged = MergeMaskList(masks, predictionRegion);
                Cv2.BitwiseOr(refinedRegion, merged, refinedRegion);
            }
            finally
            {
                foreach (var candidate in masks)
                {
                    candidate.Mask.Dispose();
                }
            }
        }

        return refined;
    }

    private static Rect EnlargeWindow(IntBox box, int imageWidth, int imageHeight)
    {
        const double ratio = 2.5;
        const double aspectRatio = 1.0;
        var width = box.Width;
        var height = box.Height;
        var b = width + height * aspectRatio;
        var c = (1 - ratio) * width * height;
        var discriminant = Math.Max(0, b * b - 4 * aspectRatio * c);
        var positiveRoot = (-b + Math.Sqrt(discriminant)) / (2 * aspectRatio);
        var delta = (int)Math.Round(positiveRoot / 2d);
        var deltaWidth = (int)(delta * aspectRatio);
        deltaWidth = Math.Min(Math.Min(box.X0, imageWidth - box.X1), deltaWidth);
        delta = Math.Min(Math.Min(box.Y0, imageHeight - box.Y1), delta);

        var x0 = Math.Clamp(box.X0 - Math.Max(0, deltaWidth), 0, imageWidth);
        var y0 = Math.Clamp(box.Y0 - Math.Max(0, delta), 0, imageHeight);
        var x1 = Math.Clamp(box.X1 + Math.Max(0, deltaWidth), x0, imageWidth);
        var y1 = Math.Clamp(box.Y1 + Math.Max(0, delta), y0, imageHeight);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static List<MaskCandidate> GetTopKMaskList(Mat imageBgr, Mat prediction)
    {
        using var gray = new Mat();
        Cv2.CvtColor(imageBgr, gray, ColorConversionCodes.BGR2GRAY);
        using var eroded = new Mat();
        using var kernel = new Mat(3, 3, MatType.CV_8UC1, Scalar.All(1));
        Cv2.Erode(prediction, eroded, kernel, iterations: 1);

        gray.GetArray(out byte[] grayPixels);
        eroded.GetArray(out byte[] erodedPixels);
        var sampled = new List<byte>();
        for (var index = 0; index < grayPixels.Length; index++)
        {
            if (erodedPixels[index] > 127)
            {
                sampled.Add(grayPixels[index]);
            }
        }

        var colors = GetTopColors(sampled, bins: 255, count: 3, colorVariance: 10, binTolerance: 0.001);
        var result = new List<MaskCandidate>(colors.Count);
        foreach (var color in colors)
        {
            var top = Math.Min(color + 30, 255);
            var bottom = top - 60;
            using var thresholded = new Mat();
            Cv2.InRange(gray, new Scalar(bottom), new Scalar(top), thresholded);
            result.Add(ChoosePolarity(thresholded, prediction));
        }

        return result;
    }

    private static IReadOnlyList<double> GetTopColors(
        IReadOnlyList<byte> samples,
        int bins,
        int count,
        double colorVariance,
        double binTolerance)
    {
        double minimum;
        double maximum;
        if (samples.Count == 0)
        {
            minimum = 0;
            maximum = 1;
        }
        else
        {
            minimum = samples.Min();
            maximum = samples.Max();
            if (minimum == maximum)
            {
                minimum -= 0.5;
                maximum += 0.5;
            }
        }

        var histogram = new int[bins];
        var width = (maximum - minimum) / bins;
        foreach (var sample in samples)
        {
            var index = sample == maximum
                ? bins - 1
                : Math.Clamp((int)((sample - minimum) / width), 0, bins - 1);
            histogram[index]++;
        }

        var edges = Enumerable.Range(0, bins + 1)
            .Select(index => minimum + index * width)
            .ToArray();
        var ordered = Enumerable.Range(0, bins)
            .OrderByDescending(index => histogram[index])
            .ToArray();
        var colors = new List<double> { edges[ordered[0]] };
        var tolerance = histogram.Sum() * binTolerance;
        foreach (var index in ordered.Skip(1))
        {
            var color = edges[index];
            if (colors.All(existing => Math.Abs(existing - color) > colorVariance))
            {
                colors.Add(color);
            }

            if (colors.Count >= count || histogram[index] < tolerance)
            {
                break;
            }
        }

        return colors;
    }

    private static MaskCandidate GetBestOtsuMask(Mat imageBgr, Mat prediction)
    {
        var channels = Cv2.Split(imageBgr);
        try
        {
            MaskCandidate? best = null;
            foreach (var channel in channels)
            {
                using var thresholded = new Mat();
                Cv2.Threshold(
                    channel,
                    thresholded,
                    1,
                    255,
                    ThresholdTypes.Otsu | ThresholdTypes.Binary);
                var candidate = ChoosePolarity(thresholded, prediction);
                if (best is null || candidate.XorSum < best.XorSum)
                {
                    best?.Mask.Dispose();
                    best = candidate;
                }
                else
                {
                    candidate.Mask.Dispose();
                }
            }

            return best ?? new MaskCandidate(
                new Mat(imageBgr.Rows, imageBgr.Cols, MatType.CV_8UC1, Scalar.Black),
                double.MaxValue);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private static MaskCandidate ChoosePolarity(Mat thresholded, Mat prediction)
    {
        using var negative = new Mat();
        Cv2.Subtract(Scalar.All(255), thresholded, negative);
        var negativeXor = XorSum(negative, prediction);
        var positiveXor = XorSum(thresholded, prediction);
        return negativeXor < positiveXor
            ? new MaskCandidate(negative.Clone(), negativeXor)
            : new MaskCandidate(thresholded.Clone(), positiveXor);
    }

    private static Mat MergeMaskList(
        List<MaskCandidate> candidates,
        Mat prediction)
    {
        candidates.Sort((left, right) => left.XorSum.CompareTo(right.XorSum));
        using var filteredPrediction = new Mat();
        using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Erode(prediction, filteredPrediction, erodeKernel, iterations: 1);
        Cv2.Threshold(
            filteredPrediction,
            filteredPrediction,
            PredictionThreshold,
            255,
            ThresholdTypes.Binary);

        var merged = new Mat(prediction.Rows, prediction.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var candidate in candidates)
        {
            MergeUsefulComponents(candidate.Mask, filteredPrediction, merged, PixelConnectivity.Connectivity8);
        }

        FillUsefulHoles(merged, filteredPrediction);
        return merged;
    }

    private static void MergeUsefulComponents(
        Mat candidate,
        Mat prediction,
        Mat merged,
        PixelConnectivity connectivity)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            candidate,
            labels,
            stats,
            centroids,
            connectivity,
            MatType.CV_32S);

        for (var label = 1; label < count; label++)
        {
            var x = stats.At<int>(label, 0);
            var y = stats.At<int>(label, 1);
            var width = stats.At<int>(label, 2);
            var height = stats.At<int>(label, 3);
            if (width * height < 3)
            {
                continue;
            }

            var bounds = new Rect(x, y, width, height);
            using var labelsRegion = new Mat(labels, bounds);
            using var component = new Mat();
            Cv2.InRange(labelsRegion, new Scalar(label), new Scalar(label), component);
            using var mergedRegion = new Mat(merged, bounds);
            using var predictionRegion = new Mat(prediction, bounds);
            using var proposed = new Mat();
            Cv2.BitwiseOr(mergedRegion, component, proposed);
            if (XorSum(proposed, predictionRegion) < XorSum(mergedRegion, predictionRegion))
            {
                proposed.CopyTo(mergedRegion);
            }
        }
    }

    private static void FillUsefulHoles(Mat merged, Mat prediction)
    {
        using var inverted = new Mat();
        Cv2.Subtract(Scalar.All(255), merged, inverted);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            inverted,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);
        if (count == 0)
        {
            return;
        }

        var sortedAreas = Enumerable.Range(0, count)
            .Select(label => stats.At<int>(label, 4))
            .OrderBy(area => area)
            .ToArray();
        var areaThreshold = sortedAreas.Length > 1 ? sortedAreas[^2] : sortedAreas[^1];

        for (var label = 0; label < count; label++)
        {
            var area = stats.At<int>(label, 4);
            if (area >= areaThreshold)
            {
                continue;
            }

            var bounds = new Rect(
                stats.At<int>(label, 0),
                stats.At<int>(label, 1),
                stats.At<int>(label, 2),
                stats.At<int>(label, 3));
            using var labelsRegion = new Mat(labels, bounds);
            using var component = new Mat();
            Cv2.InRange(labelsRegion, new Scalar(label), new Scalar(label), component);
            using var mergedRegion = new Mat(merged, bounds);
            using var predictionRegion = new Mat(prediction, bounds);
            using var proposed = new Mat();
            Cv2.BitwiseOr(mergedRegion, component, proposed);
            if (XorSum(proposed, predictionRegion) < XorSum(mergedRegion, predictionRegion))
            {
                proposed.CopyTo(mergedRegion);
            }
        }
    }

    private static void RecoverUndetectedMask(
        Mat imageBgr,
        Mat prediction,
        Mat refined,
        IReadOnlyList<CtdTextLine> lines)
    {
        using var remaining = prediction.Clone();
        using var detected = new Mat();
        Cv2.Threshold(refined, detected, 30, 255, ThresholdTypes.Binary);
        remaining.SetTo(Scalar.Black, detected);
        Cv2.Threshold(remaining, remaining, 30, 255, ThresholdTypes.Binary);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            remaining,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity4,
            MatType.CV_32S);

        var recoveredLines = new List<CtdTextLine>();
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, 4);
            if (area <= 50)
            {
                continue;
            }

            var x = stats.At<int>(label, 0);
            var y = stats.At<int>(label, 1);
            var width = stats.At<int>(label, 2);
            var height = stats.At<int>(label, 3);
            var box = new IntBox(x, y, x + width, y + height);
            var bestIntersection = lines.Count == 0
                ? -1
                : lines.Max(line => IntersectionArea(line.Box, box));
            if (bestIntersection / (double)Math.Max(1, width * height) >= 0.5)
            {
                continue;
            }

            var points = new[]
            {
                new ImagePoint(box.X0, box.Y0),
                new ImagePoint(box.X1, box.Y0),
                new ImagePoint(box.X1, box.Y1),
                new ImagePoint(box.X0, box.Y1)
            };
            recoveredLines.Add(new CtdTextLine(
                points,
                -1,
                width >= height ? DetectedTextOrientation.Horizontal : DetectedTextOrientation.Vertical,
                Math.Max(1, Math.Min(width, height)),
                box));
        }

        if (recoveredLines.Count == 0)
        {
            return;
        }

        using var recovered = RefineLines(imageBgr, remaining, recoveredLines);
        Cv2.BitwiseOr(refined, recovered, refined);
    }

    private static int IntersectionArea(IntBox left, IntBox right)
    {
        var width = Math.Max(0, Math.Min(left.X1, right.X1) - Math.Max(left.X0, right.X0));
        var height = Math.Max(0, Math.Min(left.Y1, right.Y1) - Math.Max(left.Y0, right.Y0));
        return width * height;
    }

    private static double XorSum(Mat left, Mat right)
    {
        using var difference = new Mat();
        Cv2.BitwiseXor(left, right, difference);
        return Cv2.Sum(difference).Val0;
    }

    private sealed record MaskCandidate(Mat Mask, double XorSum);
}
