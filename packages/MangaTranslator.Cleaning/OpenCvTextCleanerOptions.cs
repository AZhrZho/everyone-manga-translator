namespace MangaTranslator.Cleaning;

public sealed record OpenCvTextCleanerOptions
{
    /// <summary>
    /// Expands the detector mask before cleaning so anti-aliased glyph edges
    /// and common manga text outlines are removed with the detected core.
    /// </summary>
    public int MaskExpansionRadius { get; init; } = 9;

    /// <summary>
    /// Additional distance kept between the prepared cleaning mask and the
    /// pixels used to estimate its background. This keeps antialiased glyph
    /// edges and light text outlines out of the background sample.
    /// </summary>
    public int BackgroundSampleGap { get; init; } = 5;

    /// <summary>Width of the background sampling ring outside the exclusion gap.</summary>
    public int BackgroundSampleRadius { get; init; } = 10;

    /// <summary>
    /// Maximum average per-channel absolute deviation for treating the local
    /// background as flat and filling it with its robust median color.
    /// </summary>
    public double FlatBackgroundMaximumDeviation { get; init; } = 8;

    /// <summary>
    /// Minimum mean high-frequency amplitude for restoring texture after
    /// inpainting. Dense screentones exceed this value; flat speech balloons
    /// and sparse line art normally do not.
    /// </summary>
    public double TextureMinimumHighFrequency { get; init; } = 5;

    /// <summary>Gaussian sigma used to separate local tone from screentone detail.</summary>
    public double TextureLowPassSigma { get; init; } = 2;

    /// <summary>Maximum distance searched for a clean donor texture.</summary>
    public int TextureDonorSearchRadius { get; init; } = 160;

    /// <summary>Extra mask exclusion around possible donor pixels.</summary>
    public int TextureDonorExclusionRadius { get; init; } = 4;

    /// <summary>
    /// Maximum allowed difference between the donor patch's mean low-frequency
    /// tone and the target background ring.
    /// </summary>
    public double TextureDonorMaximumMeanDifference { get; init; } = 16;

    /// <summary>
    /// Maximum fraction of donor samples whose low-frequency tone is outside
    /// the target background distribution.
    /// </summary>
    public double TextureDonorMaximumOutlierFraction { get; init; } = 0.08;

    /// <summary>
    /// Maximum multiplier for donor structural-edge density relative to the
    /// target ring. This prevents faces, borders and line art from becoming
    /// texture donors.
    /// </summary>
    public double TextureDonorMaximumStructureRatio { get; init; } = 1.5;

    /// <summary>
    /// Padding around a textured text component before restoring it as a
    /// coherent patch instead of preserving the original glyph silhouette.
    /// </summary>
    public int TextureRegionPadding { get; init; } = 4;

    /// <summary>
    /// Closing radius used to turn a scalloped glyph silhouette into a smooth
    /// repair area before textured inpainting.
    /// </summary>
    public int TextureMaskSmoothingRadius { get; init; } = 12;

    /// <summary>Radius passed to OpenCV Telea inpainting for textured backgrounds.</summary>
    public double InpaintRadius { get; init; } = 2;

    public int MinimumBackgroundSamples { get; init; } = 12;

    public int PngCompression { get; init; } = 3;

    internal void Validate()
    {
        if (MaskExpansionRadius is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(MaskExpansionRadius));
        }

        if (BackgroundSampleRadius < 1 || BackgroundSampleRadius > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(BackgroundSampleRadius));
        }

        if (BackgroundSampleGap < 0 || BackgroundSampleGap > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(BackgroundSampleGap));
        }

        if (!double.IsFinite(FlatBackgroundMaximumDeviation)
            || FlatBackgroundMaximumDeviation < 0
            || FlatBackgroundMaximumDeviation > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(FlatBackgroundMaximumDeviation));
        }

        if (!double.IsFinite(InpaintRadius) || InpaintRadius <= 0 || InpaintRadius > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(InpaintRadius));
        }

        if (!double.IsFinite(TextureMinimumHighFrequency)
            || TextureMinimumHighFrequency < 0
            || TextureMinimumHighFrequency > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureMinimumHighFrequency));
        }

        if (!double.IsFinite(TextureLowPassSigma)
            || TextureLowPassSigma <= 0
            || TextureLowPassSigma > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureLowPassSigma));
        }

        if (TextureDonorSearchRadius is < 8 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureDonorSearchRadius));
        }

        if (TextureDonorExclusionRadius is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureDonorExclusionRadius));
        }

        if (!double.IsFinite(TextureDonorMaximumMeanDifference)
            || TextureDonorMaximumMeanDifference < 0
            || TextureDonorMaximumMeanDifference > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureDonorMaximumMeanDifference));
        }

        if (!double.IsFinite(TextureDonorMaximumOutlierFraction)
            || TextureDonorMaximumOutlierFraction < 0
            || TextureDonorMaximumOutlierFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureDonorMaximumOutlierFraction));
        }

        if (!double.IsFinite(TextureDonorMaximumStructureRatio)
            || TextureDonorMaximumStructureRatio < 1
            || TextureDonorMaximumStructureRatio > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureDonorMaximumStructureRatio));
        }

        if (TextureRegionPadding is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureRegionPadding));
        }

        if (TextureMaskSmoothingRadius is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureMaskSmoothingRadius));
        }

        if (MinimumBackgroundSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumBackgroundSamples));
        }

        if (PngCompression is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(PngCompression));
        }
    }
}
