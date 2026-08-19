using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd;

/// <summary>
/// C# equivalent of CTD's det_rearrange_forward helper.
/// It keeps very tall or very wide pages from being reduced to one square.
/// </summary>
internal static class RearrangedInference
{
    public static ModelInferenceMaps? TryInfer(
        Mat imageBgr,
        int inputSize,
        double scaleThreshold,
        double aspectThreshold,
        Func<Mat, ModelInferenceMaps> infer)
    {
        var originalHeight = imageBgr.Rows;
        var originalWidth = imageBgr.Cols;
        var transpose = originalHeight < originalWidth;
        var h = transpose ? originalWidth : originalHeight;
        var w = transpose ? originalHeight : originalWidth;

        var downScaleRatio = h / (double)inputSize;
        if (downScaleRatio <= scaleThreshold || h / (double)w <= aspectThreshold)
        {
            return null;
        }

        using var normalized = transpose ? Transpose(imageBgr) : imageBgr.Clone();

        var pwNum = Math.Max((int)Math.Floor(2d * inputSize / w), 2);
        var patchSize = checked(pwNum * w);
        var phNum = (int)Math.Ceiling(h / (double)patchSize);
        var phStep = phNum > 1 ? (h - patchSize) / (phNum - 1) : 0;

        var relativeSteps = new double[phNum];
        var patches = new List<Mat>(phNum);
        try
        {
            for (var index = 0; index < phNum; index++)
            {
                var top = index * phStep;
                relativeSteps[index] = top / (double)h;
                patches.Add(new Mat(normalized, new Rect(0, top, w, patchSize)).Clone());
            }

            var padCount = ((phNum + pwNum - 1) / pwNum) * pwNum - phNum;
            for (var index = 0; index < padCount; index++)
            {
                patches.Add(new Mat(patchSize, w, MatType.CV_8UC3, Scalar.Black));
            }

            var mapList = new List<ModelInferenceMaps>(patches.Count / pwNum);
            for (var offset = 0; offset < patches.Count; offset += pwNum)
            {
                using var mosaic = BuildMosaic(patches, offset, pwNum, w, transpose);
                using var resized = SquarePadResize(mosaic, inputSize, out var padSize);
                var maps = infer(resized);
                if (padSize > 0)
                {
                    var linePad = maps.LineWidth == 0
                        ? 0
                        : (int)(maps.LineWidth / (double)inputSize * padSize);
                    var maskPad = maps.MaskWidth == 0
                        ? 0
                        : (int)(maps.MaskWidth / (double)inputSize * padSize);
                    maps = CropMaps(
                        maps,
                        Math.Max(1, maps.LineWidth - linePad),
                        Math.Max(1, maps.LineHeight - linePad),
                        Math.Max(1, maps.MaskWidth - maskPad),
                        Math.Max(1, maps.MaskHeight - maskPad));
                }

                mapList.Add(maps);
            }

            return Unrearrange(
                mapList,
                transpose,
                pwNum,
                padCount,
                h,
                w,
                patchSize,
                phStep,
                relativeSteps);
        }
        finally
        {
            foreach (var patch in patches)
            {
                patch.Dispose();
            }
        }
    }

    private static Mat BuildMosaic(
        IReadOnlyList<Mat> patches,
        int offset,
        int patchCount,
        int patchWidth,
        bool transpose)
    {
        var patchSize = patches[offset].Rows;
        var mosaic = new Mat(patchSize, patchSize, MatType.CV_8UC3, Scalar.Black);
        for (var index = 0; index < patchCount; index++)
        {
            var target = transpose
                ? new Rect(0, index * patchWidth, patchSize, patchWidth)
                : new Rect(index * patchWidth, 0, patchWidth, patchSize);
            using var roi = new Mat(mosaic, target);
            if (transpose)
            {
                using var transposedPatch = Transpose(patches[offset + index]);
                transposedPatch.CopyTo(roi);
            }
            else
            {
                patches[offset + index].CopyTo(roi);
            }
        }

        return mosaic;
    }

    private static Mat SquarePadResize(Mat image, int targetSize, out int padSize)
    {
        var height = image.Rows;
        var width = image.Cols;
        var padHeight = 0;
        var padWidth = 0;
        if (width < height)
        {
            padWidth = height - width;
            width += padWidth;
        }
        else if (height < width)
        {
            padHeight = width - height;
            height += padHeight;
        }

        padSize = targetSize - height;
        if (padSize > 0)
        {
            padHeight += padSize;
            padWidth += padSize;
        }

        Mat padded = image;
        if (padHeight > 0 || padWidth > 0)
        {
            padded = new Mat();
            Cv2.CopyMakeBorder(image, padded, 0, padHeight, 0, padWidth, BorderTypes.Constant);
        }

        var ratio = targetSize / (double)padded.Rows;
        if (ratio < 1)
        {
            var resized = new Mat();
            Cv2.Resize(padded, resized, new Size(targetSize, targetSize), interpolation: InterpolationFlags.Linear);
            if (!ReferenceEquals(padded, image))
            {
                padded.Dispose();
            }

            return resized;
        }

        if (!ReferenceEquals(padded, image))
        {
            return padded;
        }

        return image.Clone();
    }

    private static ModelInferenceMaps Unrearrange(
        IReadOnlyList<ModelInferenceMaps> maps,
        bool transpose,
        int patchWidthCount,
        int padCount,
        int normalizedHeight,
        int normalizedWidth,
        int patchSize,
        int patchStep,
        IReadOnlyList<double> relativeSteps)
    {
        if (maps.Count == 0)
        {
            throw new InvalidOperationException("Rearranged inference produced no model maps.");
        }

        var lineMap = UnrearrangeMap(
            maps.Select(map => new ChannelMap(map.Lines, map.LineChannels, map.LineHeight, map.LineWidth)).ToArray(),
            transpose,
            patchWidthCount,
            padCount,
            normalizedHeight,
            normalizedWidth,
            patchSize,
            patchStep,
            relativeSteps);
        var maskMap = UnrearrangeMap(
            maps.Select(map => new ChannelMap(map.Mask, 1, map.MaskHeight, map.MaskWidth)).ToArray(),
            transpose,
            patchWidthCount,
            padCount,
            normalizedHeight,
            normalizedWidth,
            patchSize,
            patchStep,
            relativeSteps);

        return new ModelInferenceMaps(
            lineMap.Values,
            lineMap.Channels,
            lineMap.Height,
            lineMap.Width,
            maskMap.Values,
            maskMap.Height,
            maskMap.Width);
    }

    private static ChannelMap UnrearrangeMap(
        IReadOnlyList<ChannelMap> maps,
        bool transpose,
        int patchWidthCount,
        int padCount,
        int normalizedHeight,
        int normalizedWidth,
        int patchSize,
        int patchStep,
        IReadOnlyList<double> relativeSteps)
    {
        var first = maps[0];
        var mapSize = first.Width;
        var step = (int)(patchStep * mapSize / (double)patchSize);
        var stripWidth = Math.Max(1, mapSize / patchWidthCount);
        var outputHeight = Math.Max(1, (int)(stripWidth / (double)normalizedWidth * normalizedHeight));
        var target = new float[first.Channels * outputHeight * stripWidth];
        var patchCount = maps.Count * patchWidthCount - padCount;

        for (var mapIndex = 0; mapIndex < maps.Count; mapIndex++)
        {
            var map = maps[mapIndex];
            for (var strip = 0; strip < patchWidthCount; strip++)
            {
                var patchIndex = mapIndex * patchWidthCount + strip;
                if (patchIndex >= patchCount || patchIndex >= relativeSteps.Count)
                {
                    break;
                }

                var top = Math.Clamp((int)Math.Round(relativeSteps[patchIndex] * outputHeight), 0, outputHeight);
                var bottom = Math.Min(top + map.Height, outputHeight);
                var sourceWidth = Math.Min(stripWidth, map.Width - strip * stripWidth);
                if (bottom <= top || sourceWidth <= 0)
                {
                    continue;
                }

                for (var channel = 0; channel < map.Channels; channel++)
                {
                    for (var y = top; y < bottom; y++)
                    {
                        var sourceY = y - top;
                        var destinationOffset = channel * outputHeight * stripWidth + y * stripWidth;
                        for (var x = 0; x < sourceWidth; x++)
                        {
                            var sourceX = strip * stripWidth + x;
                            var sourceIndex = transpose
                                ? channel * map.Height * map.Width + sourceX * map.Width + sourceY
                                : channel * map.Height * map.Width + sourceY * map.Width + sourceX;
                            target[destinationOffset + x] += map.Values[sourceIndex];
                        }
                    }
                }

                if (patchIndex > 0)
                {
                    var interleave = Math.Clamp(map.Height - step, 0, outputHeight - top);
                    if (interleave > 0)
                    {
                        for (var channel = 0; channel < map.Channels; channel++)
                        {
                            var offset = channel * outputHeight * stripWidth + top * stripWidth;
                            for (var y = 0; y < interleave; y++)
                            {
                                for (var x = 0; x < sourceWidth; x++)
                                {
                                    target[offset + y * stripWidth + x] /= 2f;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (!transpose)
        {
            return new ChannelMap(target, first.Channels, outputHeight, stripWidth);
        }

        var transposed = new float[first.Channels * stripWidth * outputHeight];
        for (var channel = 0; channel < first.Channels; channel++)
        {
            for (var y = 0; y < outputHeight; y++)
            {
                for (var x = 0; x < stripWidth; x++)
                {
                    transposed[channel * stripWidth * outputHeight + x * outputHeight + y]
                        = target[channel * outputHeight * stripWidth + y * stripWidth + x];
                }
            }
        }

        return new ChannelMap(transposed, first.Channels, stripWidth, outputHeight);
    }

    private sealed record ChannelMap(float[] Values, int Channels, int Height, int Width);

    private static Mat Transpose(Mat image)
    {
        var result = new Mat();
        Cv2.Transpose(image, result);
        return result;
    }

    private static ModelInferenceMaps CropMaps(
        ModelInferenceMaps maps,
        int lineWidth,
        int lineHeight,
        int maskWidth,
        int maskHeight)
    {
        var lines = Crop(maps.Lines, maps.LineChannels, maps.LineHeight, maps.LineWidth, lineWidth, lineHeight);
        var mask = Crop(maps.Mask, 1, maps.MaskHeight, maps.MaskWidth, maskWidth, maskHeight);
        return new ModelInferenceMaps(lines, maps.LineChannels, lineHeight, lineWidth, mask, maskHeight, maskWidth);
    }

    private static float[] Crop(float[] source, int channels, int sourceHeight, int sourceWidth, int width, int height)
    {
        var result = new float[checked(channels * width * height)];
        for (var channel = 0; channel < channels; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                Array.Copy(
                    source,
                    channel * sourceHeight * sourceWidth + y * sourceWidth,
                    result,
                    channel * height * width + y * width,
                    width);
            }
        }

        return result;
    }
}
