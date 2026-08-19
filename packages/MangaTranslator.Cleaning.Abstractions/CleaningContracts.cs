using MangaTranslator.LocalDetection;

namespace MangaTranslator.Cleaning;

/// <summary>
/// A page-space, left/top inclusive and right/bottom exclusive rectangle.
/// X and Y may be outside the page; the cleaner intersects the rectangle with
/// the decoded image before applying the mask.
/// </summary>
public readonly record struct ImageClip(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width == 0 || Height == 0;
}

/// <summary>
/// The background immediately around the text selected by a cleaning request.
/// This is supplied by a caller, for example from multimodal recognition or a
/// reviewer, so removal does not have to infer the intended repair strategy.
/// </summary>
public enum TextBackgroundType
{
    SolidColor,
    RegularTexture,
    RandomScreentone,
    ComplexPattern
}

public sealed record TextCleaningRequest(
    ReadOnlyMemory<byte> ImageBytes,
    MaskImage Mask,
    ImageClip Clip,
    string? MediaType = null,
    TextBackgroundType BackgroundType = TextBackgroundType.SolidColor);

public sealed record TextCleaningResult(
    ReadOnlyMemory<byte> ImageBytes,
    string MediaType,
    int Width,
    int Height,
    ImageClip AppliedClip,
    int AppliedMaskPixels,
    int SolidFillComponents,
    int InpaintedComponents,
    int TextureDonorComponents = 0,
    int TextureFallbackComponents = 0);

public interface ITextCleaner
{
    ValueTask<TextCleaningResult> CleanAsync(
        TextCleaningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TextCleaningException : Exception
{
    public TextCleaningException(string message)
        : base(message)
    {
    }

    public TextCleaningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
