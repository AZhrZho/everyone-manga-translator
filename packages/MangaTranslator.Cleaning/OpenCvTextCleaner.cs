using System.Runtime.InteropServices;
using MangaTranslator.LocalDetection;
using OpenCvSharp;

namespace MangaTranslator.Cleaning;

/// <summary>
/// Removes the foreground pixels selected by a detector mask. The clip is
/// applied before every mutation, so a caller can safely use only a portion of
/// a full-page mask.
/// </summary>
public sealed class OpenCvTextCleaner : ITextCleaner
{
    private readonly OpenCvTextCleanerOptions _options;

    public OpenCvTextCleaner(OpenCvTextCleanerOptions? options = null)
    {
        _options = options ?? new OpenCvTextCleanerOptions();
        _options.Validate();
    }

    public ValueTask<TextCleaningResult> CleanAsync(
        TextCleaningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ImageBytes.IsEmpty)
        {
            throw new ArgumentException("Image bytes cannot be empty.", nameof(request));
        }

        if (request.Mask is null)
        {
            throw new ArgumentNullException(nameof(request), "A mask is required.");
        }

        if (!Enum.IsDefined(request.BackgroundType))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown text background type.");
        }

        if (request.Clip.Width < 0 || request.Clip.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Clip dimensions cannot be negative.");
        }

        try
        {
            var result = Clean(request, cancellationToken);
            return ValueTask.FromResult(result);
        }
        catch (TextCleaningException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OpenCVException or InvalidDataException)
        {
            throw new TextCleaningException("OpenCV could not clean the source image.", exception);
        }
    }

    private TextCleaningResult Clean(
        TextCleaningRequest request,
        CancellationToken cancellationToken)
    {
        using var source = Cv2.ImDecode(request.ImageBytes.Span, ImreadModes.Unchanged);
        if (source.Empty())
        {
            throw new TextCleaningException("The source image could not be decoded.");
        }

        var width = source.Cols;
        var height = source.Rows;
        if (request.Mask.Width != width || request.Mask.Height != height)
        {
            throw new TextCleaningException(
                $"Mask is {request.Mask.Width}x{request.Mask.Height}, but the image is {width}x{height}.");
        }

        var clip = IntersectWithImage(request.Clip, width, height);
        if (clip.IsEmpty)
        {
            return new TextCleaningResult(
                request.ImageBytes,
                request.MediaType ?? "application/octet-stream",
                width,
                height,
                clip,
                0,
                0,
                0);
        }

        var maskPixels = request.Mask.Pixels.ToArray();
        using var fullMask = Mat.FromPixelData(height, width, MatType.CV_8UC1, maskPixels);
        using var binaryMask = new Mat();
        Cv2.Threshold(fullMask, binaryMask, 30, 255, ThresholdTypes.Binary);
        using var preparedMask = ExpandMask(binaryMask, _options.MaskExpansionRadius);
        using var effectiveMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        using (var maskRoi = new Mat(preparedMask, ToRect(clip)))
        using (var effectiveRoi = new Mat(effectiveMask, ToRect(clip)))
        {
            maskRoi.CopyTo(effectiveRoi);
        }

        var appliedPixels = Cv2.CountNonZero(effectiveMask);
        if (appliedPixels == 0)
        {
            return new TextCleaningResult(
                request.ImageBytes,
                request.MediaType ?? "application/octet-stream",
                width,
                height,
                clip,
                0,
                0,
                0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var sourceBgr = ToBgr(source);
        using var cleanedBgr = sourceBgr.Clone();
        using var appliedMask = effectiveMask.Clone();
        using var inpaintMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(
            effectiveMask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);

        var solidFillComponents = 0;
        var inpaintedComponents = 0;
        var texturedComponents = new List<TextureComponent>();
        for (var label = 1; label < componentCount; label++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = new Rect(
                stats.At<int>(label, 0),
                stats.At<int>(label, 1),
                stats.At<int>(label, 2),
                stats.At<int>(label, 3));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var context = Expand(
                bounds,
                _options.BackgroundSampleGap + _options.BackgroundSampleRadius + 2,
                width,
                height);
            using var labelsContext = new Mat(labels, context);
            using var component = new Mat();
            Cv2.InRange(labelsContext, new Scalar(label), new Scalar(label), component);

            using var innerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(_options.BackgroundSampleGap * 2 + 1, _options.BackgroundSampleGap * 2 + 1));
            using var inner = new Mat();
            Cv2.Dilate(component, inner, innerKernel);
            var outerRadius = _options.BackgroundSampleGap + _options.BackgroundSampleRadius;
            using var outerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(outerRadius * 2 + 1, outerRadius * 2 + 1));
            using var outer = new Mat();
            Cv2.Dilate(component, outer, outerKernel);
            using var sourceMaskContext = new Mat(preparedMask, context);
            using var notMask = new Mat();
            Cv2.BitwiseNot(sourceMaskContext, notMask);
            using var notInner = new Mat();
            Cv2.BitwiseNot(inner, notInner);
            using var ring = new Mat();
            Cv2.BitwiseAnd(outer, notInner, ring);
            Cv2.BitwiseAnd(ring, notMask, ring);
            using var sourceContext = new Mat(sourceBgr, context);

            if (request.BackgroundType == TextBackgroundType.SolidColor
                && TryEstimateFlatBackground(sourceContext, ring, out var background, out _))
            {
                using var cleanedContext = new Mat(cleanedBgr, context);
                cleanedContext.SetTo(background, component);
                solidFillComponents++;
            }
            else
            {
                if (request.BackgroundType is TextBackgroundType.RegularTexture
                    or TextBackgroundType.RandomScreentone
                    or TextBackgroundType.ComplexPattern)
                {
                    using var repairMask = BuildTextureRepairMask(
                        component,
                        context,
                        ToRect(clip),
                        request.BackgroundType);
                    var localBounds = Cv2.BoundingRect(repairMask);
                    if (localBounds.Width <= 0 || localBounds.Height <= 0)
                    {
                        continue;
                    }

                    var repairBounds = new Rect(
                        context.X + localBounds.X,
                        context.Y + localBounds.Y,
                        localBounds.Width,
                        localBounds.Height);
                    using var repairMaskCrop = new Mat(repairMask, localBounds);
                    using (var inpaintContext = new Mat(inpaintMask, repairBounds))
                    using (var appliedContext = new Mat(appliedMask, repairBounds))
                    {
                        inpaintContext.SetTo(Scalar.White, repairMaskCrop);
                        repairMaskCrop.CopyTo(appliedContext, repairMaskCrop);
                    }

                    texturedComponents.Add(new TextureComponent(
                        repairBounds,
                        ReadMaskPixels(repairMaskCrop),
                        request.BackgroundType));
                }
                else
                {
                    using var inpaintContext = new Mat(inpaintMask, context);
                    inpaintContext.SetTo(Scalar.White, component);
                }
                inpaintedComponents++;
            }
        }

        appliedPixels = Cv2.CountNonZero(appliedMask);

        if (inpaintedComponents > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var restored = new Mat();
            Cv2.Inpaint(cleanedBgr, inpaintMask, restored, _options.InpaintRadius, InpaintTypes.Telea);
            restored.CopyTo(cleanedBgr, inpaintMask);
        }

        var textureRestore = default(TextureRestoreSummary);
        if (texturedComponents.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            textureRestore = RestoreTextureResiduals(
                sourceBgr,
                cleanedBgr,
                preparedMask,
                texturedComponents,
                cancellationToken);
        }

        using var output = RestoreChannels(cleanedBgr, source);
        if (!Cv2.ImEncode(".png", output, out var encoded))
        {
            throw new TextCleaningException("OpenCV could not encode the cleaned image as PNG.");
        }

        return new TextCleaningResult(
            encoded,
            "image/png",
            width,
            height,
            clip,
            appliedPixels,
            solidFillComponents,
            inpaintedComponents,
            textureRestore.DonorComponents,
            textureRestore.FallbackComponents);
    }

    private bool TryEstimateFlatBackground(
        Mat sourceBgr,
        Mat ring,
        out Scalar background,
        out double highFrequency)
    {
        background = default;
        highFrequency = double.PositiveInfinity;
        var sampleCount = Cv2.CountNonZero(ring);
        if (sampleCount < _options.MinimumBackgroundSamples)
        {
            return false;
        }

        var blue = new byte[sampleCount];
        var green = new byte[sampleCount];
        var red = new byte[sampleCount];
        var index = 0;
        var rows = ring.Rows;
        var cols = ring.Cols;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                if (ring.At<byte>(y, x) == 0)
                {
                    continue;
                }

                var pixel = sourceBgr.At<Vec3b>(y, x);
                blue[index] = pixel.Item0;
                green[index] = pixel.Item1;
                red[index] = pixel.Item2;
                index++;
            }
        }

        Array.Sort(blue);
        Array.Sort(green);
        Array.Sort(red);
        var medianBlue = Median(blue);
        var medianGreen = Median(green);
        var medianRed = Median(red);
        var deviation = 0d;
        for (var i = 0; i < sampleCount; i++)
        {
            deviation += Math.Abs(blue[i] - medianBlue);
            deviation += Math.Abs(green[i] - medianGreen);
            deviation += Math.Abs(red[i] - medianRed);
        }

        deviation /= sampleCount * 3d;
        using var gray = new Mat();
        using var blurred = new Mat();
        using var highPass = new Mat();
        Cv2.CvtColor(sourceBgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(0, 0), _options.TextureLowPassSigma);
        Cv2.Absdiff(gray, blurred, highPass);
        highFrequency = Cv2.Mean(highPass, ring).Val0;
        if (deviation > _options.FlatBackgroundMaximumDeviation
            || highFrequency >= _options.TextureMinimumHighFrequency)
        {
            return false;
        }

        background = new Scalar(medianBlue, medianGreen, medianRed);
        return true;
    }

    private TextureRestoreSummary RestoreTextureResiduals(
        Mat sourceBgr,
        Mat cleanedBgr,
        Mat preparedMask,
        IReadOnlyList<TextureComponent> components,
        CancellationToken cancellationToken)
    {
        using var lowPass = new Mat();
        using var source16 = new Mat();
        using var lowPass16 = new Mat();
        using var sourceGray = new Mat();
        using var lowPassGray = new Mat();
        using var gradientX = new Mat();
        using var gradientY = new Mat();
        using var absoluteGradientX = new Mat();
        using var absoluteGradientY = new Mat();
        using var structure = new Mat();
        Cv2.GaussianBlur(sourceBgr, lowPass, new Size(0, 0), _options.TextureLowPassSigma);
        sourceBgr.ConvertTo(source16, MatType.CV_16SC3);
        lowPass.ConvertTo(lowPass16, MatType.CV_16SC3);
        Cv2.CvtColor(sourceBgr, sourceGray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(sourceGray, lowPassGray, new Size(0, 0), _options.TextureLowPassSigma);
        Cv2.Sobel(lowPassGray, gradientX, MatType.CV_16SC1, 1, 0);
        Cv2.Sobel(lowPassGray, gradientY, MatType.CV_16SC1, 0, 1);
        Cv2.ConvertScaleAbs(gradientX, absoluteGradientX);
        Cv2.ConvertScaleAbs(gradientY, absoluteGradientY);
        Cv2.AddWeighted(absoluteGradientX, 0.5, absoluteGradientY, 0.5, 0, structure);
        using var donorForbidden = ExpandMask(preparedMask, _options.TextureDonorExclusionRadius);
        var donorComponents = 0;
        var fallbackComponents = 0;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repairMask = Mat.FromPixelData(
                component.Bounds.Height,
                component.Bounds.Width,
                MatType.CV_8UC1,
                component.MaskPixels);
            using var ringForbidden = donorForbidden.Clone();
            var targetRingBounds = Expand(
                component.Bounds,
                _options.TextureDonorExclusionRadius + 3,
                sourceBgr.Cols,
                sourceBgr.Rows);
            using (var targetRing = new Mat(ringForbidden, targetRingBounds))
            {
                targetRing.SetTo(Scalar.Black);
            }

            var offset = FindTextureDonorOffset(
                sourceBgr,
                source16,
                lowPass16,
                lowPassGray,
                structure,
                donorForbidden,
                ringForbidden,
                component.Bounds,
                repairMask,
                component.BackgroundType);
            if (offset is null)
            {
                fallbackComponents++;
                continue;
            }

            var bounds = component.Bounds;
            var donorBounds = new Rect(bounds.X + offset.Value.X, bounds.Y + offset.Value.Y, bounds.Width, bounds.Height);
            using var donorPatch = new Mat(sourceBgr, donorBounds);
            // A donor is already selected for local tone and structure. Preserve
            // its actual texture with a masked copy; Poisson blending blurred
            // screentones and produced glyph-shaped gray ghosts.
            using var targetPatch = new Mat(cleanedBgr, bounds);
            donorPatch.CopyTo(targetPatch, repairMask);
            donorComponents++;
        }

        return new TextureRestoreSummary(donorComponents, fallbackComponents);
    }

    private Point? FindTextureDonorOffset(
        Mat sourceBgr,
        Mat source16,
        Mat lowPass16,
        Mat lowPassGray,
        Mat structure,
        Mat donorForbidden,
        Mat ringForbidden,
        Rect bounds,
        Mat repairMask,
        TextBackgroundType backgroundType)
    {
        var componentPoints = CollectMaskPoints(repairMask, bounds, 256);
        var ringPoints = CollectMaskRingPoints(
            repairMask,
            bounds,
            3,
            sourceBgr.Cols,
            sourceBgr.Rows,
            ringForbidden,
            384);
        if (componentPoints.Count == 0 || ringPoints.Count == 0)
        {
            return null;
        }

        var targetTone = SampleStatistics(lowPassGray, ringPoints);
        var targetStructure = SampleStatistics(structure, ringPoints);
        var toneTolerance = Math.Max(20d, targetTone.StandardDeviation * 2.5 + 10);

        var bestScore = double.PositiveInfinity;
        Point? bestOffset = null;
        var radius = _options.TextureDonorSearchRadius;
        var sourceWidth = sourceBgr.Cols;
        var sourceHeight = sourceBgr.Rows;
        for (var dy = -radius; dy <= radius; dy += 4)
        {
            for (var dx = -radius; dx <= radius; dx += 4)
            {
                if (Math.Abs(dx) + Math.Abs(dy) < Math.Max(12, _options.MaskExpansionRadius * 3))
                {
                    continue;
                }

                if (bounds.X + dx < 0
                    || bounds.Y + dy < 0
                    || bounds.Right + dx > sourceWidth
                    || bounds.Bottom + dy > sourceHeight)
                {
                    continue;
                }

                var valid = true;
                foreach (var point in componentPoints)
                {
                    var donorX = point.X + dx;
                    var donorY = point.Y + dy;
                    if (donorX < 0 || donorY < 0 || donorX >= sourceWidth || donorY >= sourceHeight
                        || donorForbidden.At<byte>(donorY, donorX) != 0)
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    continue;
                }

                var donorTone = SampleStatistics(lowPassGray, componentPoints, dx, dy);
                var meanDifference = Math.Abs(donorTone.Mean - targetTone.Mean);
                var stochasticTexture = backgroundType is TextBackgroundType.RandomScreentone or TextBackgroundType.ComplexPattern;
                var meanLimit = _options.TextureDonorMaximumMeanDifference
                    + (stochasticTexture ? 8 : 0)
                    + targetTone.StandardDeviation * 0.5;
                if (meanDifference > meanLimit)
                {
                    continue;
                }

                var outliers = 0;
                foreach (var point in componentPoints)
                {
                    var donorToneValue = lowPassGray.At<byte>(point.Y + dy, point.X + dx);
                    if (Math.Abs(donorToneValue - targetTone.Mean) > toneTolerance)
                    {
                        outliers++;
                    }
                }

                var maximumOutlierFraction = stochasticTexture
                    ? Math.Max(_options.TextureDonorMaximumOutlierFraction, 0.18)
                    : _options.TextureDonorMaximumOutlierFraction;
                if (outliers / (double)componentPoints.Count > maximumOutlierFraction)
                {
                    continue;
                }

                var donorStructure = SampleStatistics(structure, componentPoints, dx, dy);
                var structureLimit = Math.Max(
                    8d,
                    targetStructure.Mean * _options.TextureDonorMaximumStructureRatio + 2);
                if (donorStructure.Mean > structureLimit)
                {
                    continue;
                }

                var residualScore = 0d;
                var fullColorScore = 0d;
                var comparisons = 0;
                foreach (var point in ringPoints)
                {
                    var donorX = point.X + dx;
                    var donorY = point.Y + dy;
                    if (donorX < 0 || donorY < 0 || donorX >= sourceWidth || donorY >= sourceHeight
                        || donorForbidden.At<byte>(donorY, donorX) != 0)
                    {
                        continue;
                    }

                    var targetResidual = source16.At<Vec3s>(point.Y, point.X) - lowPass16.At<Vec3s>(point.Y, point.X);
                    var donorResidual = source16.At<Vec3s>(donorY, donorX) - lowPass16.At<Vec3s>(donorY, donorX);
                    residualScore += Math.Abs(targetResidual.Item0 - donorResidual.Item0)
                        + Math.Abs(targetResidual.Item1 - donorResidual.Item1)
                        + Math.Abs(targetResidual.Item2 - donorResidual.Item2);
                    var targetPixel = sourceBgr.At<Vec3b>(point.Y, point.X);
                    var donorPixel = sourceBgr.At<Vec3b>(donorY, donorX);
                    fullColorScore += Math.Abs(targetPixel.Item0 - donorPixel.Item0)
                        + Math.Abs(targetPixel.Item1 - donorPixel.Item1)
                        + Math.Abs(targetPixel.Item2 - donorPixel.Item2);
                    comparisons++;
                }

                if (comparisons < 16)
                {
                    continue;
                }

                var residualWeight = stochasticTexture ? 1.5 : 1;
                var colorWeight = backgroundType == TextBackgroundType.RegularTexture ? 3 : 1.5;
                var score = residualScore / (comparisons * 3d) * residualWeight
                    + fullColorScore / (comparisons * 3d) * colorWeight
                    + meanDifference * 1.5
                    + Math.Abs(donorTone.StandardDeviation - targetTone.StandardDeviation) * 0.5
                    + Math.Max(0, donorStructure.Mean - targetStructure.Mean) * 0.5
                    + Math.Sqrt(dx * dx + dy * dy) * 0.02;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = new Point(dx, dy);
                }
            }
        }

        return bestOffset;
    }

    private static SampleStatisticsResult SampleStatistics(
        Mat values,
        List<Point> points,
        int offsetX = 0,
        int offsetY = 0)
    {
        var sum = 0d;
        var sumOfSquares = 0d;
        foreach (var point in points)
        {
            var value = values.At<byte>(point.Y + offsetY, point.X + offsetX);
            sum += value;
            sumOfSquares += value * value;
        }

        var mean = sum / points.Count;
        var variance = Math.Max(0, sumOfSquares / points.Count - mean * mean);
        return new SampleStatisticsResult(mean, Math.Sqrt(variance));
    }

    private Mat BuildTextureRepairMask(
        Mat component,
        Rect context,
        Rect clip,
        TextBackgroundType backgroundType)
    {
        if (backgroundType is TextBackgroundType.RandomScreentone or TextBackgroundType.ComplexPattern)
        {
            // Avoid joining nearby glyphs into a broad blob when the donor
            // search fails; inpainting that blob creates petal-shaped blur.
            return component.Clone();
        }

        using var closeKernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse,
            new Size(
                _options.TextureMaskSmoothingRadius * 2 + 1,
                _options.TextureMaskSmoothingRadius * 2 + 1));
        using var smoothed = new Mat();
        Cv2.MorphologyEx(component, smoothed, MorphTypes.Close, closeKernel);
        using var expanded = ExpandMask(smoothed, _options.TextureRegionPadding);
        var clipLocal = Intersect(
            new Rect(0, 0, context.Width, context.Height),
            new Rect(clip.X - context.X, clip.Y - context.Y, clip.Width, clip.Height));
        using var clipMask = new Mat(context.Height, context.Width, MatType.CV_8UC1, Scalar.Black);
        if (clipLocal.Width > 0 && clipLocal.Height > 0)
        {
            Cv2.Rectangle(clipMask, clipLocal, Scalar.White, -1);
        }

        var repairMask = new Mat();
        Cv2.BitwiseAnd(expanded, clipMask, repairMask);
        return repairMask;
    }

    private static List<Point> CollectMaskPoints(Mat mask, Rect bounds, int maximum)
    {
        var points = new List<Point>();
        var total = Cv2.CountNonZero(mask);
        var stride = Math.Max(1, total / maximum);
        var seen = 0;
        var rows = mask.Rows;
        var cols = mask.Cols;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                if (mask.At<byte>(y, x) == 0 || seen++ % stride != 0)
                {
                    continue;
                }

                points.Add(new Point(bounds.X + x, bounds.Y + y));
                if (points.Count >= maximum)
                {
                    return points;
                }
            }
        }

        return points;
    }

    private static List<Point> CollectMaskRingPoints(
        Mat mask,
        Rect bounds,
        int width,
        int imageWidth,
        int imageHeight,
        Mat forbidden,
        int maximum)
    {
        var outer = Expand(bounds, width, imageWidth, imageHeight);
        using var outerMask = new Mat(outer.Height, outer.Width, MatType.CV_8UC1, Scalar.Black);
        using var maskRoi = new Mat(outerMask, new Rect(bounds.X - outer.X, bounds.Y - outer.Y, mask.Cols, mask.Rows));
        mask.CopyTo(maskRoi);
        using var dilated = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(width * 2 + 1, width * 2 + 1));
        Cv2.Dilate(outerMask, dilated, kernel);
        using var notMask = new Mat();
        Cv2.BitwiseNot(outerMask, notMask);
        using var ring = new Mat();
        Cv2.BitwiseAnd(dilated, notMask, ring);
        var points = new List<Point>();
        for (var y = outer.Y; y < outer.Bottom; y++)
        {
            for (var x = outer.X; x < outer.Right; x++)
            {
                if (ring.At<byte>(y - outer.Y, x - outer.X) == 0 || forbidden.At<byte>(y, x) != 0)
                {
                    continue;
                }

                points.Add(new Point(x, y));
            }
        }

        if (points.Count <= maximum)
        {
            return points;
        }

        var sampled = new List<Point>(maximum);
        var stride = points.Count / (double)maximum;
        for (var index = 0; index < maximum; index++)
        {
            sampled.Add(points[(int)(index * stride)]);
        }

        return sampled;
    }

    private static byte[] ReadMaskPixels(Mat mask)
    {
        var rows = mask.Rows;
        var cols = mask.Cols;
        var pixels = new byte[checked(rows * cols)];
        for (var y = 0; y < rows; y++)
        {
            Marshal.Copy(mask.Ptr(y), pixels, y * cols, cols);
        }

        return pixels;
    }

    private sealed record TextureComponent(
        Rect Bounds,
        byte[] MaskPixels,
        TextBackgroundType BackgroundType);

    private readonly record struct SampleStatisticsResult(double Mean, double StandardDeviation);

    private readonly record struct TextureRestoreSummary(int DonorComponents, int FallbackComponents);

    private static byte Median(byte[] values)
    {
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (byte)((values[middle - 1] + values[middle]) / 2)
            : values[middle];
    }

    private static Mat ToBgr(Mat source)
    {
        var result = new Mat();
        switch (source.Channels())
        {
            case 1:
                Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGR);
                break;
            case 3:
                source.CopyTo(result);
                break;
            case 4:
                Cv2.CvtColor(source, result, ColorConversionCodes.BGRA2BGR);
                break;
            default:
                result.Dispose();
                throw new TextCleaningException($"Unsupported source channel count: {source.Channels()}.");
        }

        return result;
    }

    private static Mat ExpandMask(Mat mask, int radius)
    {
        if (radius == 0)
        {
            return mask.Clone();
        }

        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse,
            new Size(radius * 2 + 1, radius * 2 + 1));
        var expanded = new Mat();
        Cv2.Dilate(mask, expanded, kernel);
        return expanded;
    }

    private static Mat RestoreChannels(Mat cleanedBgr, Mat source)
    {
        var result = new Mat();
        switch (source.Channels())
        {
            case 1:
                Cv2.CvtColor(cleanedBgr, result, ColorConversionCodes.BGR2GRAY);
                break;
            case 3:
                cleanedBgr.CopyTo(result);
                break;
            case 4:
                Cv2.CvtColor(cleanedBgr, result, ColorConversionCodes.BGR2BGRA);
                var sourceChannels = source.Split();
                var resultChannels = result.Split();
                try
                {
                    sourceChannels[3].CopyTo(resultChannels[3]);
                    Cv2.Merge(resultChannels, result);
                }
                finally
                {
                    foreach (var channel in sourceChannels)
                    {
                        channel.Dispose();
                    }

                    foreach (var channel in resultChannels)
                    {
                        channel.Dispose();
                    }
                }

                break;
            default:
                result.Dispose();
                throw new TextCleaningException($"Unsupported source channel count: {source.Channels()}.");
        }

        return result;
    }

    private static ImageClip IntersectWithImage(ImageClip clip, int width, int height)
    {
        var left = Math.Max(0L, clip.X);
        var top = Math.Max(0L, clip.Y);
        var right = Math.Min((long)width, (long)clip.X + clip.Width);
        var bottom = Math.Min((long)height, (long)clip.Y + clip.Height);
        if (right <= left || bottom <= top)
        {
            return default;
        }

        return new ImageClip(
            checked((int)left),
            checked((int)top),
            checked((int)(right - left)),
            checked((int)(bottom - top)));
    }

    private static Rect ToRect(ImageClip clip) => new(clip.X, clip.Y, clip.Width, clip.Height);

    private static Rect Expand(Rect bounds, int padding, int width, int height)
    {
        var x0 = Math.Max(0, bounds.X - padding);
        var y0 = Math.Max(0, bounds.Y - padding);
        var x1 = Math.Min(width, bounds.Right + padding);
        var y1 = Math.Min(height, bounds.Bottom + padding);
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        var x0 = Math.Max(first.X, second.X);
        var y0 = Math.Max(first.Y, second.Y);
        var x1 = Math.Min(first.Right, second.Right);
        var y1 = Math.Min(first.Bottom, second.Bottom);
        return x1 <= x0 || y1 <= y0
            ? default
            : new Rect(x0, y0, x1 - x0, y1 - y0);
    }

}
