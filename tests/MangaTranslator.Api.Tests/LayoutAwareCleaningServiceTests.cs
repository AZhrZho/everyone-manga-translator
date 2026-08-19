using MangaTranslator.Api.Projects;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.Tests;

public sealed class LayoutAwareCleaningServiceTests
{
    [Fact]
    public void CandidateMaskIsLimitedToFinalTextBoxes()
    {
        var candidate = new MaskImage(12, 8, Enumerable.Repeat((byte)255, 96).ToArray());
        var effective = LayoutAwareCleaningService.ClipMaskToTextBoxes(
            candidate,
            [new TextLayoutBox(2, 1, 6, 5)]);

        Assert.Equal(16, effective.Pixels.ToArray().Count(value => value == 255));
        Assert.Equal(255, effective.Pixels.Span[2 + 1 * 12]);
        Assert.Equal(0, effective.Pixels.Span[9 + 6 * 12]);
    }

    [Fact]
    public void EmptyFinalLayoutClearsNoCandidateMaskPixels()
    {
        var candidate = new MaskImage(5, 5, Enumerable.Repeat((byte)255, 25).ToArray());
        var effective = LayoutAwareCleaningService.ClipMaskToTextBoxes(candidate, []);

        Assert.All(effective.Pixels.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ExpandedLayoutRecoversPixelsOnlyWhenRebuiltFromTheCandidateMask()
    {
        var candidate = new MaskImage(8, 4, Enumerable.Repeat((byte)255, 32).ToArray());
        var narrow = LayoutAwareCleaningService.ClipMaskToTextBoxes(
            candidate,
            [new TextLayoutBox(1, 1, 4, 3)]);
        var expandedFromCandidate = LayoutAwareCleaningService.ClipMaskToTextBoxes(
            candidate,
            [new TextLayoutBox(1, 1, 7, 3)]);
        var expandedFromPreviousEffective = LayoutAwareCleaningService.ClipMaskToTextBoxes(
            narrow,
            [new TextLayoutBox(1, 1, 7, 3)]);

        const int restoredPixel = 6 + 1 * 8;
        Assert.Equal(0, narrow.Pixels.Span[restoredPixel]);
        Assert.Equal(255, expandedFromCandidate.Pixels.Span[restoredPixel]);
        Assert.Equal(0, expandedFromPreviousEffective.Pixels.Span[restoredPixel]);
    }

    [Fact]
    public void RotatedTextBoxUsesTheRotatedFootprint()
    {
        var candidate = new MaskImage(12, 12, Enumerable.Repeat((byte)255, 144).ToArray());
        var effective = LayoutAwareCleaningService.ClipMaskToRegions(
            candidate,
            [new LayoutMaskRegion(
                new TextLayoutBox(3, 4, 9, 8),
                new DialogueBoxRotation(3, 2, 90))]);

        Assert.Equal(24, effective.Pixels.ToArray().Count(value => value == 255));
        Assert.Equal(255, effective.Pixels.Span[5 + 3 * 12]);
        Assert.Equal(0, effective.Pixels.Span[3 + 4 * 12]);
    }

    [Fact]
    public void UnreadableAndEmptySegmentsDoNotProduceCleaningRegions()
    {
        const string layout = """
            {
              "segments": [
                {
                  "status": "unreadable",
                  "translated_text": "ignored",
                  "layout": {
                    "text_box": { "x0": 0, "y0": 0, "x1": 10, "y1": 10 },
                    "orientation": "horizontal", "reading_direction": "left_to_right",
                    "alignment": "center", "font_id": "font", "font_size_px": 20,
                    "italic": false, "color": "#000000", "letter_spacing_px": 0
                  }
                },
                {
                  "status": "translated",
                  "translated_text": "",
                  "layout": {
                    "text_box": { "x0": 0, "y0": 0, "x1": 10, "y1": 10 },
                    "orientation": "horizontal", "reading_direction": "left_to_right",
                    "alignment": "center", "font_id": "font", "font_size_px": 20,
                    "italic": false, "color": "#000000", "letter_spacing_px": 0
                  }
                }
              ]
            }
            """;

        Assert.Empty(LayoutAwareCleaningService.ReadRenderableTextBoxes(layout, 20, 20));
    }
}
