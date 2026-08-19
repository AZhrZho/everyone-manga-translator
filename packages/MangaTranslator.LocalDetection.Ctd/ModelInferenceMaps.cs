namespace MangaTranslator.LocalDetection.Ctd;

internal sealed record ModelInferenceMaps(
    float[] Lines,
    int LineChannels,
    int LineHeight,
    int LineWidth,
    float[] Mask,
    int MaskHeight,
    int MaskWidth);
