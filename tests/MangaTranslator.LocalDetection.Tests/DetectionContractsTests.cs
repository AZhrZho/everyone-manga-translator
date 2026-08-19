using MangaTranslator.LocalDetection;

namespace MangaTranslator.LocalDetection.Tests;

public sealed class DetectionContractsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ctd")]
    [InlineData("CTD")]
    [InlineData("manga-image-translator")]
    public void Text_detector_ids_normalize_to_ctd(string? detectorId)
    {
        Assert.Equal(TextDetectorIds.Ctd, TextDetectorIds.Normalize(detectorId));
    }

    [Fact]
    public void MaskImage_rejects_a_pixel_buffer_with_the_wrong_size()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new MaskImage(10, 10, new byte[99]));

        Assert.Equal("pixels", exception.ParamName);
    }

    [Fact]
    public void TextDetectionResult_copies_the_region_collection()
    {
        var source = new List<DetectedTextRegion>();
        var result = new TextDetectionResult(
            2,
            2,
            source,
            new MaskImage(2, 2, new byte[4]));

        source.Add(new DetectedTextRegion(
            0,
            0,
            1,
            1,
            DetectedTextLanguage.Japanese,
            DetectedTextOrientation.Vertical,
            12,
            0.9f,
            []));

        Assert.Empty(result.Regions);
    }
}
