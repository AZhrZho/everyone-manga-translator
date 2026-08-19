using System.Collections.ObjectModel;

namespace MangaTranslator.LocalDetection;

/// <summary>Input supplied by a caller. Detectors never persist the input or their result.</summary>
public sealed record DetectionInput(
    ReadOnlyMemory<byte> ImageBytes,
    string? MediaType = null);

public enum DetectedTextLanguage
{
    English = 0,
    Japanese = 1,
    Unknown = 2
}

public enum DetectedTextOrientation
{
    Horizontal,
    Vertical
}

public readonly record struct ImagePoint(int X, int Y);

public sealed record DetectedTextLine(
    IReadOnlyList<ImagePoint> Polygon,
    float Confidence);

public sealed record DetectedTextRegion(
    int X0,
    int Y0,
    int X1,
    int Y1,
    DetectedTextLanguage Language,
    DetectedTextOrientation Orientation,
    int FontSize,
    float Confidence,
    IReadOnlyList<DetectedTextLine> Lines);

/// <summary>
/// A row-major, one-byte-per-pixel mask. Values are 0 or 255. Encoding it as
/// PNG or another persistence format is the caller's responsibility.
/// </summary>
public sealed record MaskImage
{
    public MaskImage(int width, int height, ReadOnlyMemory<byte> pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Mask dimensions must be positive.");
        }

        if (pixels.Length != checked(width * height))
        {
            throw new ArgumentException("Mask pixel count does not match its dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Pixels { get; }
}

public sealed record TextDetectionResult
{
    public TextDetectionResult(
        int width,
        int height,
        IReadOnlyList<DetectedTextRegion> regions,
        MaskImage mask)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        Width = width;
        Height = height;
        Regions = new ReadOnlyCollection<DetectedTextRegion>(
            (regions ?? throw new ArgumentNullException(nameof(regions))).ToArray());
        Mask = mask ?? throw new ArgumentNullException(nameof(mask));
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<DetectedTextRegion> Regions { get; }

    public MaskImage Mask { get; }
}

/// <summary>Detects text geometry and creates the corresponding in-memory mask.</summary>
public interface ITextMaskDetector : IDisposable
{
    ValueTask<TextDetectionResult> DetectAsync(
        DetectionInput input,
        CancellationToken cancellationToken = default);
}
