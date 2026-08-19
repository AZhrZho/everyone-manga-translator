using MangaTranslator.LocalDetection.Craft;

namespace MangaTranslator.LocalDetection.Craft.Tests;

public sealed class CraftCoordinateMapperTests
{
    [Theory]
    [InlineData(0f, 450, 1439, 0)]
    [InlineData(225f, 450, 1439, 720)]
    [InlineData(449f, 450, 1439, 1436)]
    public void MapsCroppedScoreCoordinatesToTheOriginalPage(
        float coordinate,
        int mapLength,
        int imageLength,
        int expected)
    {
        Assert.Equal(expected, CraftCoordinateMapper.MapX(coordinate, mapLength, imageLength));
    }

    [Fact]
    public void UsesTheActualCroppedMapSizeForOddContentDimensions()
    {
        // A 899-pixel-wide model input produces a 450-column score map. The
        // old hard-coded stride transform mapped the last score column using
        // 2 / scale and could drift from the mask resize path.
        const int mapWidth = 450;
        const int originalWidth = 1439;

        Assert.Equal(1436, CraftCoordinateMapper.MapX(449, mapWidth, originalWidth));
        Assert.Equal(1438, CraftCoordinateMapper.MapX(450, mapWidth, originalWidth));
    }

    [Fact]
    public void PaddingOutsideTheCroppedMapCannotChangeTheMapping()
    {
        const int croppedMapWidth = 39;
        const int paddedCanvasMapWidth = 48;
        const int originalWidth = 101;

        var correct = CraftCoordinateMapper.MapX(38, croppedMapWidth, originalWidth);
        var incorrectlyIncludingPadding = CraftCoordinateMapper.MapX(38, paddedCanvasMapWidth, originalWidth);

        Assert.Equal(98, correct);
        Assert.Equal(80, incorrectlyIncludingPadding);
        Assert.NotEqual(incorrectlyIncludingPadding, correct);
    }

    [Fact]
    public void RejectsInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CraftCoordinateMapper.MapX(0, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => CraftCoordinateMapper.MapY(0, 10, 0));
    }
}
