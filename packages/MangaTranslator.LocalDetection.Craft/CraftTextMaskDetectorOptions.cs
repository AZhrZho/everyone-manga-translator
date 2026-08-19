namespace MangaTranslator.LocalDetection.Craft;

public sealed record CraftTextMaskDetectorOptions
{
    public int CanvasSize { get; init; } = 1280;

    public double MagnificationRatio { get; init; } = 1.5;

    public float TextThreshold { get; init; } = 0.7f;

    public float LowTextThreshold { get; init; } = 0.4f;

    public float LinkThreshold { get; init; } = 0.4f;

    public float GlyphSeedThreshold { get; init; } = 0.55f;

    public float GlyphCandidateThreshold { get; init; } = 0.25f;

    public int MinimumComponentArea { get; init; } = 10;

    public int MinimumGlyphArea { get; init; } = 2;

    public int MaximumRegions { get; init; } = 1000;

    public int GlyphMaskDilationRadius { get; init; }

    internal void Validate()
    {
        if (CanvasSize < 32 || CanvasSize % 32 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CanvasSize), "CRAFT canvas size must be a multiple of 32.");
        }

        if (!double.IsFinite(MagnificationRatio) || MagnificationRatio <= 0 || MagnificationRatio > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MagnificationRatio));
        }

        ValidateProbability(TextThreshold, nameof(TextThreshold));
        ValidateProbability(LowTextThreshold, nameof(LowTextThreshold));
        ValidateProbability(LinkThreshold, nameof(LinkThreshold));
        ValidateProbability(GlyphSeedThreshold, nameof(GlyphSeedThreshold));
        ValidateProbability(GlyphCandidateThreshold, nameof(GlyphCandidateThreshold));
        if (LowTextThreshold > TextThreshold)
        {
            throw new ArgumentException("LowTextThreshold cannot exceed TextThreshold.");
        }

        if (GlyphCandidateThreshold > GlyphSeedThreshold)
        {
            throw new ArgumentException("GlyphCandidateThreshold cannot exceed GlyphSeedThreshold.");
        }

        if (MinimumComponentArea < 1 || MinimumGlyphArea < 1 || MaximumRegions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumComponentArea));
        }

        if (GlyphMaskDilationRadius is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(GlyphMaskDilationRadius));
        }
    }

    private static void ValidateProbability(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
