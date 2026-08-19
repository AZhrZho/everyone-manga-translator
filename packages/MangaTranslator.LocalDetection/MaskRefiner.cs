using OpenCvSharp;

namespace MangaTranslator.LocalDetection;

internal static class MaskRefiner
{
    private const byte PredictionThreshold = 60;

    public static byte[] Refine(
        Mat originalBgr,
        byte[] rawMask,
        IReadOnlyList<DetectionPostProcessor.MutableBlock> blocks,
        bool keepUndetectedMask)
    {
        using var raw = Mat.FromPixelData(
            originalBgr.Rows,
            originalBgr.Cols,
            MatType.CV_8UC1,
            rawMask);
        using var refined = new Mat(
            originalBgr.Rows,
            originalBgr.Cols,
            MatType.CV_8UC1,
            Scalar.Black);

        foreach (var block in blocks)
        {
            var window = ExpandWindow(block.Box, originalBgr.Cols, originalBgr.Rows);
            if (window.Width <= 1 || window.Height <= 1)
            {
                continue;
            }

            using var imageRegion = new Mat(originalBgr, window);
            using var predictionRegion = new Mat(raw, window);
            using var refinedRegion = new Mat(refined, window);
            using var candidate = RefineRegion(imageRegion, predictionRegion);
            Cv2.BitwiseOr(refinedRegion, candidate, refinedRegion);
        }

        if (keepUndetectedMask)
        {
            KeepUndetectedComponents(raw, refined);
        }

        Cv2.Threshold(refined, refined, 1, 255, ThresholdTypes.Binary);
        refined.GetArray(out byte[] result);
        return result;
    }

    private static Rect ExpandWindow(
        DetectionPostProcessor.IntBox box,
        int imageWidth,
        int imageHeight)
    {
        var padding = (int)Math.Round(
            (Math.Max(box.Width, box.Height) * 0.25
                + Math.Min(box.Width, box.Height) * 0.75)
            / 16d);
        var x0 = Math.Max(0, box.X0 - padding);
        var y0 = Math.Max(0, box.Y0 - padding);
        var x1 = Math.Min(imageWidth, box.X1 + padding);
        var y1 = Math.Min(imageHeight, box.Y1 + padding);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static Mat RefineRegion(Mat imageBgr, Mat prediction)
    {
        var candidates = GetColorCandidates(imageBgr, prediction);
        try
        {
            candidates.Add(GetBestOtsuCandidate(imageBgr, prediction));
            candidates.Sort((left, right) =>
                XorDistance(left, prediction).CompareTo(XorDistance(right, prediction)));
            return MergeUsefulComponents(candidates, prediction);
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    private static List<Mat> GetColorCandidates(Mat imageBgr, Mat prediction)
    {
        using var gray = new Mat();
        Cv2.CvtColor(imageBgr, gray, ColorConversionCodes.BGR2GRAY);
        using var eroded = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Erode(prediction, eroded, kernel, iterations: 1);
        gray.GetArray(out byte[] grayPixels);
        eroded.GetArray(out byte[] erodedPixels);

        var histogram = new int[256];
        var sampleCount = 0;
        for (var index = 0; index < grayPixels.Length; index++)
        {
            if (erodedPixels[index] <= 127)
            {
                continue;
            }

            histogram[grayPixels[index]]++;
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return [];
        }

        var colors = new List<int>(3);
        foreach (var color in Enumerable.Range(0, histogram.Length)
            .OrderByDescending(color => histogram[color]))
        {
            if (histogram[color] < sampleCount * 0.001)
            {
                break;
            }

            if (colors.Count == 0 || colors.All(existing => Math.Abs(existing - color) > 10))
            {
                colors.Add(color);
            }

            if (colors.Count >= 3)
            {
                break;
            }
        }

        var candidates = new List<Mat>(colors.Count);
        foreach (var color in colors)
        {
            var upper = Math.Min(color + 30, 255);
            var lower = Math.Max(0, upper - 60);
            using var ranged = new Mat();
            Cv2.InRange(gray, new Scalar(lower), new Scalar(upper), ranged);
            candidates.Add(ChoosePolarity(ranged, prediction));
        }

        return candidates;
    }

    private static Mat GetBestOtsuCandidate(Mat imageBgr, Mat prediction)
    {
        var channels = Cv2.Split(imageBgr);
        try
        {
            Mat? best = null;
            var bestDistance = int.MaxValue;
            foreach (var channel in channels)
            {
                using var thresholded = new Mat();
                Cv2.Threshold(
                    channel,
                    thresholded,
                    1,
                    255,
                    ThresholdTypes.Binary | ThresholdTypes.Otsu);
                var candidate = ChoosePolarity(thresholded, prediction);
                var distance = XorDistance(candidate, prediction);
                if (distance < bestDistance)
                {
                    best?.Dispose();
                    best = candidate;
                    bestDistance = distance;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            return best ?? new Mat(imageBgr.Rows, imageBgr.Cols, MatType.CV_8UC1, Scalar.Black);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private static Mat ChoosePolarity(Mat thresholded, Mat prediction)
    {
        using var inverted = new Mat();
        Cv2.BitwiseNot(thresholded, inverted);
        return XorDistance(inverted, prediction) < XorDistance(thresholded, prediction)
            ? inverted.Clone()
            : thresholded.Clone();
    }

    private static Mat MergeUsefulComponents(IReadOnlyList<Mat> candidates, Mat prediction)
    {
        using var predictionFiltered = new Mat();
        using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Erode(prediction, predictionFiltered, erodeKernel, iterations: 1);
        Cv2.Threshold(
            predictionFiltered,
            predictionFiltered,
            PredictionThreshold,
            255,
            ThresholdTypes.Binary);

        var merged = new Mat(prediction.Rows, prediction.Cols, MatType.CV_8UC1, Scalar.Black);
        var currentDistance = XorDistance(merged, predictionFiltered);
        foreach (var candidate in candidates)
        {
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(
                candidate,
                labels,
                stats,
                centroids,
                PixelConnectivity.Connectivity8,
                MatType.CV_32S);

            for (var label = 1; label < count; label++)
            {
                var width = stats.At<int>(label, 2);
                var height = stats.At<int>(label, 3);
                if (width * height < 3)
                {
                    continue;
                }

                using var component = new Mat();
                Cv2.InRange(labels, new Scalar(label), new Scalar(label), component);
                using var proposed = new Mat();
                Cv2.BitwiseOr(merged, component, proposed);
                var proposedDistance = XorDistance(proposed, predictionFiltered);
                if (proposedDistance < currentDistance)
                {
                    proposed.CopyTo(merged);
                    currentDistance = proposedDistance;
                }
            }
        }

        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(merged, merged, dilateKernel, iterations: 1);
        FillUsefulHoles(merged, predictionFiltered);
        return merged;
    }

    private static void FillUsefulHoles(Mat merged, Mat prediction)
    {
        using var inverted = new Mat();
        Cv2.BitwiseNot(merged, inverted);
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
        var areas = Enumerable.Range(0, count)
            .Select(label => stats.At<int>(label, 4))
            .OrderBy(area => area)
            .ToArray();
        if (areas.Length == 0)
        {
            return;
        }

        var areaThreshold = areas.Length > 1 ? areas[^2] : areas[^1];
        var currentDistance = XorDistance(merged, prediction);
        for (var label = 0; label < count; label++)
        {
            if (stats.At<int>(label, 4) >= areaThreshold)
            {
                continue;
            }

            using var component = new Mat();
            Cv2.InRange(labels, new Scalar(label), new Scalar(label), component);
            using var proposed = new Mat();
            Cv2.BitwiseOr(merged, component, proposed);
            var proposedDistance = XorDistance(proposed, prediction);
            if (proposedDistance < currentDistance)
            {
                proposed.CopyTo(merged);
                currentDistance = proposedDistance;
            }
        }
    }

    private static int XorDistance(Mat left, Mat right)
    {
        using var difference = new Mat();
        Cv2.BitwiseXor(left, right, difference);
        return Cv2.CountNonZero(difference);
    }

    private static void KeepUndetectedComponents(Mat raw, Mat refined)
    {
        using var rawBinary = new Mat();
        Cv2.Threshold(raw, rawBinary, 30, 255, ThresholdTypes.Binary);
        using var invertedRefined = new Mat();
        Cv2.BitwiseNot(refined, invertedRefined);
        Cv2.BitwiseAnd(rawBinary, invertedRefined, rawBinary);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            rawBinary,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);

        using var retained = new Mat(raw.Rows, raw.Cols, MatType.CV_8UC1, Scalar.Black);
        for (var label = 1; label < count; label++)
        {
            if (stats.At<int>(label, 4) <= 50)
            {
                continue;
            }

            using var component = new Mat();
            Cv2.InRange(labels, new Scalar(label), new Scalar(label), component);
            Cv2.BitwiseOr(retained, component, retained);
        }

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Dilate(retained, retained, kernel, iterations: 1);
        Cv2.BitwiseOr(refined, retained, refined);
    }
}
