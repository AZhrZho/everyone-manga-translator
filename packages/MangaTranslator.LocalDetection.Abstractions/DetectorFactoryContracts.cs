namespace MangaTranslator.LocalDetection;

public static class TextDetectorIds
{
    public const string Ctd = "ctd";

    private const string LegacyCtdId = "manga-image-translator";

    public const string Craft = "craft";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Ctd;
        }

        if (string.Equals(value, Craft, StringComparison.OrdinalIgnoreCase))
        {
            return Craft;
        }

        if (string.Equals(value, Ctd, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, LegacyCtdId, StringComparison.OrdinalIgnoreCase))
        {
            return Ctd;
        }

        throw new ArgumentException($"Unknown text detection engine: {value}", nameof(value));
    }
}

public sealed record TextMaskDetectorDescriptor(
    string DetectorId,
    string AlgorithmVersion,
    string OptionsFingerprint,
    string DefaultModelFileName);

public interface ITextMaskDetectorFactory
{
    TextMaskDetectorDescriptor Descriptor { get; }

    ITextMaskDetector Create(ReadOnlyMemory<byte> model);
}
