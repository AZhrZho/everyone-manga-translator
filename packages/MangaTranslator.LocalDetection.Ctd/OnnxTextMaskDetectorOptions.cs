namespace MangaTranslator.LocalDetection.Ctd;

/// <summary>
/// Options for the current Comic-Text-Detector (CTD) pipeline. Properties
/// shared with the legacy detector retain their names to ease migration.
/// </summary>
public sealed record OnnxTextMaskDetectorOptions
{
    public int InputSize { get; init; } = 1024;

    /// <summary>
    /// Retained for source compatibility. The current upstream CTD path does
    /// not consume the YOLO block output.
    /// </summary>
    public float BlockConfidenceThreshold { get; init; } = 0.4f;

    /// <summary>
    /// Retained for source compatibility. The current upstream CTD path does
    /// not run block NMS.
    /// </summary>
    public float BlockIouThreshold { get; init; } = 0.35f;

    /// <summary>DBNet shrink-map threshold used to create line contours.</summary>
    public float LineBitmapThreshold { get; init; } = 0.3f;

    /// <summary>Minimum mean DBNet confidence retained as a text line.</summary>
    public float LineScoreThreshold { get; init; } = 0.6f;

    /// <summary>Maximum number of DBNet contours examined per image.</summary>
    public int MaxDetections { get; init; } = 1000;

    /// <summary>
    /// The latest CTD detector deliberately leaves language unknown because
    /// language classification belongs after OCR. The default therefore keeps
    /// unknown-language regions in the result.
    /// </summary>
    public bool IncludeUnknownLanguageBlocks { get; init; } = true;

    /// <summary>
    /// Opts into the upstream-compatible undetected-mask recovery helper. The
    /// latest default CTD path leaves this disabled.
    /// </summary>
    public bool KeepUndetectedMask { get; init; }

    /// <summary>
    /// Images with a shorter side below this value receive a black right/bottom
    /// border before detection, matching the current upstream detector host.
    /// Set to zero to disable this behavior.
    /// </summary>
    public int MinimumImageSize { get; init; } = 400;

    /// <summary>Enables upstream long-image rearranged inference.</summary>
    public bool EnableRearrangedInference { get; init; } = true;

    /// <summary>Long-side/input-size ratio required before rearrangement.</summary>
    public double RearrangedScaleThreshold { get; init; } = 2.5;

    /// <summary>Long-side/short-side ratio required before rearrangement.</summary>
    public double RearrangedAspectRatioThreshold { get; init; } = 3.0;
}
