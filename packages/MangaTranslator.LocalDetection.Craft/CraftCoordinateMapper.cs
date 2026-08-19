namespace MangaTranslator.LocalDetection.Craft;

/// <summary>
/// Maps coordinates from the cropped CRAFT score map to page space. The
/// cropped map dimensions are authoritative: they already exclude the canvas
/// padding and also account for odd content dimensions at the model stride.
/// </summary>
internal static class CraftCoordinateMapper
{
    public static int MapX(float x, int mapWidth, int imageWidth) =>
        Map(x, mapWidth, imageWidth);

    public static int MapY(float y, int mapHeight, int imageHeight) =>
        Map(y, mapHeight, imageHeight);

    private static int Map(float coordinate, int mapLength, int imageLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageLength);

        var mapped = coordinate / mapLength * imageLength;
        return Math.Clamp((int)Math.Round(mapped), 0, imageLength - 1);
    }
}
