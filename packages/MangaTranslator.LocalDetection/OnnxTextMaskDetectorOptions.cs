namespace MangaTranslator.LocalDetection;

/// <summary>Options for the original block-grouping CTD implementation.</summary>
public sealed record OnnxTextMaskDetectorOptions
{
    public int InputSize { get; init; } = 1024;

    public float BlockConfidenceThreshold { get; init; } = 0.4f;

    public float BlockIouThreshold { get; init; } = 0.35f;

    public float LineBitmapThreshold { get; init; } = 0.3f;

    public float LineScoreThreshold { get; init; } = 0.6f;

    public int MaxDetections { get; init; } = 300;

    public bool IncludeUnknownLanguageBlocks { get; init; }

    public bool KeepUndetectedMask { get; init; }
}
