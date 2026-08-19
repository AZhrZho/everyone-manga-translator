namespace MangaTranslator.LocalDetection.Ctd;

internal readonly record struct IntBox(int X0, int Y0, int X1, int Y1)
{
    public int Width => Math.Max(0, X1 - X0);

    public int Height => Math.Max(0, Y1 - Y0);

    public int Area => Width * Height;
}

internal sealed record CtdTextLine(
    ImagePoint[] Points,
    float Confidence,
    DetectedTextOrientation Orientation,
    int FontSize,
    IntBox Box);
