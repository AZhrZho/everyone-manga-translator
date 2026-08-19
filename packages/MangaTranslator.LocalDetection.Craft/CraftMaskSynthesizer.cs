using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Craft;

internal static class CraftMaskSynthesizer
{
    public static byte[] Build(
        Mat originalBgr,
        Mat textMap,
        IReadOnlyList<CraftDetection> detections,
        int contentWidth,
        int contentHeight,
        CraftTextMaskDetectorOptions options,
        CancellationToken cancellationToken)
    {
        using var heatmapMask = new Mat(textMap.Rows, textMap.Cols, MatType.CV_8UC1, Scalar.Black);
        using var globalSeed = new Mat();
        using var globalCandidate = new Mat();
        Cv2.Threshold(textMap, globalSeed, options.GlyphSeedThreshold, 255, ThresholdTypes.Binary);
        Cv2.Threshold(textMap, globalCandidate, options.GlyphCandidateThreshold, 255, ThresholdTypes.Binary);
        globalSeed.ConvertTo(globalSeed, MatType.CV_8UC1);
        globalCandidate.ConvertTo(globalCandidate, MatType.CV_8UC1);
        foreach (var detection in detections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var seed = new Mat();
            using var candidate = new Mat();
            Cv2.BitwiseAnd(globalSeed, detection.ComponentMask, seed);
            Cv2.BitwiseAnd(globalCandidate, detection.ComponentMask, candidate);
            if (Cv2.CountNonZero(seed) == 0)
            {
                continue;
            }

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
                if (stats.At<int>(label, 4) < options.MinimumGlyphArea)
                {
                    continue;
                }

                using var component = new Mat();
                Cv2.InRange(labels, new Scalar(label), new Scalar(label), component);
                // The accepted CRAFT component already contains a strong text
                // seed. Keep every weak text-score island inside that component,
                // including punctuation connected to the glyphs only by the
                // affinity map. Affinity pixels themselves never enter the mask.
                heatmapMask.SetTo(Scalar.White, component);
            }
        }

        if (options.GlyphMaskDilationRadius > 0)
        {
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(options.GlyphMaskDilationRadius * 2 + 1, options.GlyphMaskDilationRadius * 2 + 1));
            Cv2.Dilate(heatmapMask, heatmapMask, kernel);
        }

        using var contentMask = new Mat();
        Cv2.Resize(
            heatmapMask,
            contentMask,
            new Size(contentWidth, contentHeight),
            interpolation: InterpolationFlags.Linear);
        using var resized = new Mat();
        Cv2.Resize(
            contentMask,
            resized,
            new Size(originalBgr.Cols, originalBgr.Rows),
            interpolation: InterpolationFlags.Linear);
        Cv2.Threshold(resized, resized, 30, 255, ThresholdTypes.Binary);
        resized.GetArray(out byte[] result);
        return result;
    }
}
