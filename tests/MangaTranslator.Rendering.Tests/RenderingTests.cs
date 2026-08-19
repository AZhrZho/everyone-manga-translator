using MangaTranslator.Dialogue;
using MangaTranslator.FontManager;
using MangaTranslator.ModelProvider.Abstractions;
using SkiaSharp;

namespace MangaTranslator.Rendering.Tests;

public sealed class RenderingTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "manga-translator-rendering-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RendersConstructedPageDataToOneWhiteBackgroundImagePerPage()
    {
        var library = CreateManagedFontLibrary();
        using var blockRenderer = new SkiaTextBlockRenderer(library);
        var renderer = new DialoguePageRenderer(blockRenderer, new SkiaPageComposer());
        PageRenderData[] pages =
        [
            new(
                "page-horizontal",
                480,
                280,
                [
                    new TextBlockRenderData(
                        "horizontal-dialogue",
                        "Hello manga!\nEditable block",
                        Layout(
                            new TextLayoutBox(30, 30, 450, 180),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            30,
                            fontWeight: 520,
                            rotation: new DialogueBoxRotation(210, 75, -3),
                            stroke: new DialogueTextStroke(2, "#FFFFFF", true),
                            advanced: new DialogueAdvancedLayout(
                                [new DialogueLineAdjustment(1, 8, 4, 24, "#5B21B6")],
                                [new DialogueCharacterAdjustment(6, 0, -4, 36, "#DC2626")],
                                [new DialogueRubyAnnotation(0, 5, "greeting", DialogueRubyPlacement.Before)])))
                ]),
            new(
                "page-vertical",
                320,
                440,
                [
                    new TextBlockRenderData(
                        "vertical-dialogue",
                        "MANGA\nTEXT",
                        Layout(
                            new TextLayoutBox(100, 30, 290, 410),
                            DialogueTextOrientation.Vertical,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.Center,
                            34,
                            bold: true,
                            fontWeight: 780,
                            stroke: new DialogueTextStroke(2, "#FFFFFF", true),
                            letterSpacing: 3,
                            advanced: new DialogueAdvancedLayout(
                                [new DialogueLineAdjustment(1, -6, 0, 30, "#0F766E")],
                                [new DialogueCharacterAdjustment(2, 4, 0, 42, "#EA580C")],
                                [new DialogueRubyAnnotation(0, 2, "MA", DialogueRubyPlacement.Before)])))
                ]),
            new(
                "page-weight-rotation-stroke",
                640,
                260,
                [
                    new TextBlockRenderData(
                        "light-rotated",
                        "轻字重",
                        Layout(
                            new TextLayoutBox(30, 40, 210, 180),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            30,
                            fontId: "F0001",
                            fontWeight: 300,
                            rotation: new DialogueBoxRotation(90, 70, -10))),
                    new TextBlockRenderData(
                        "medium-outlined",
                        "中等字重",
                        Layout(
                            new TextLayoutBox(230, 40, 410, 180),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            30,
                            fontId: "F0002",
                            fontWeight: 550,
                            stroke: new DialogueTextStroke(2, "#F59E0B", true))),
                    new TextBlockRenderData(
                        "heavy-rotated-outline",
                        "重描边",
                        Layout(
                            new TextLayoutBox(430, 40, 610, 180),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            30,
                            fontId: "F0003",
                            fontWeight: 900,
                            rotation: new DialogueBoxRotation(90, 70, 10),
                            stroke: new DialogueTextStroke(4, "#2563EB", true)))
                ]),
            new(
                "page-comic-multi-block",
                760,
                900,
                [
                    new TextBlockRenderData(
                        "speech-top-left",
                        "等一下！\n这边！",
                        Layout(
                            new TextLayoutBox(30, 30, 300, 125),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Start,
                            28,
                            fontId: "F0001")),
                    new TextBlockRenderData(
                        "speech-top-right",
                        "别这么快！",
                        Layout(
                            new TextLayoutBox(440, 30, 730, 125),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.End,
                            26,
                            italic: true,
                            fontId: "F0002")),
                    new TextBlockRenderData(
                        "chapter-caption",
                        "第 01 章",
                        Layout(
                            new TextLayoutBox(280, 155, 480, 225),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            24,
                            fontId: "F0003")),
                    new TextBlockRenderData(
                        "vertical-go",
                        "走！",
                        Layout(
                            new TextLayoutBox(30, 250, 180, 530),
                            DialogueTextOrientation.Vertical,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Start,
                            30,
                            bold: true,
                            fontId: "F0002")),
                    new TextBlockRenderData(
                        "vertical-stop",
                        "停下！",
                        Layout(
                            new TextLayoutBox(580, 230, 730, 550),
                            DialogueTextOrientation.Vertical,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.Start,
                            30,
                            fontId: "F0003")),
                    new TextBlockRenderData(
                        "narration-justify",
                        "我看见你了\n现在。",
                        Layout(
                            new TextLayoutBox(210, 260, 520, 390),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Justify,
                            22,
                            letterSpacing: 2,
                            fontId: "F0001")),
                    new TextBlockRenderData(
                        "reply-center",
                        "真的？",
                        Layout(
                            new TextLayoutBox(210, 410, 500, 525),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.Center,
                            25,
                            fontId: "F0002")),
                    new TextBlockRenderData(
                        "sound-effect",
                        "砰！",
                        Layout(
                            new TextLayoutBox(225, 560, 535, 670),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            36,
                            bold: true,
                            italic: true,
                            fontWeight: 850,
                            rotation: new DialogueBoxRotation(155, 55, -8),
                            stroke: new DialogueTextStroke(3, "#FFFFFF", true),
                            fontId: "F0003")),
                    new TextBlockRenderData(
                        "small-left",
                        "……",
                        Layout(
                            new TextLayoutBox(20, 700, 220, 805),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Start,
                            24,
                            fontId: "F0001")),
                    new TextBlockRenderData(
                        "small-right",
                        "嗯？",
                        Layout(
                            new TextLayoutBox(540, 650, 740, 760),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.End,
                            24,
                            fontId: "F0002")),
                    new TextBlockRenderData(
                        "vertical-aside",
                        "侧面",
                        Layout(
                            new TextLayoutBox(450, 680, 540, 840),
                            DialogueTextOrientation.Vertical,
                            DialogueReadingDirection.RightToLeft,
                            DialogueTextAlignment.End,
                            22,
                            fontId: "F0003")),
                    new TextBlockRenderData(
                        "footer",
                        "未完待续……",
                        Layout(
                            new TextLayoutBox(270, 850, 530, 895),
                            DialogueTextOrientation.Horizontal,
                            DialogueReadingDirection.LeftToRight,
                            DialogueTextAlignment.Center,
                            18,
                            fontId: "F0001"))
                ])
        ];

        foreach (var pageData in pages)
        {
            var background = new PageBackground(
                "image/png",
                CreateRulerPng(pageData.Width, pageData.Height, library));
            var page = renderer.Render(pageData, background, debug: true);

            Assert.Equal("image/png", page.MediaType);
            Assert.Equal(pageData.Width, page.Width);
            Assert.Equal(pageData.Height, page.Height);
            using var bitmap = SKBitmap.Decode(page.Data.ToArray());
            Assert.NotNull(bitmap);
            Assert.Equal(new SKColor(37, 99, 235), bitmap.GetPixel(0, 0));
            Assert.True(CountPixels(bitmap, new SKColor(220, 38, 38)) > 100);
            Assert.True(File.Exists(TestArtifactWriter.WritePage(
                nameof(RendersConstructedPageDataToOneWhiteBackgroundImagePerPage),
                page)));
        }
    }

    [Fact]
    public void RepositionsRenderedBlockWithoutRerenderingText()
    {
        var library = CreateManagedFontLibrary();
        using var blockRenderer = new SkiaTextBlockRenderer(library);
        var block = blockRenderer.Render(new TextBlockRenderData(
            "move-me",
            "Move",
            Layout(
                new TextLayoutBox(0, 0, 120, 60),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Start,
                32)));
        var composer = new SkiaPageComposer();
        var first = composer.Compose(new PageCompositionData(
            "placement-first",
            320,
            180,
            [new TextBlockPlacement(block, 10, 15)]));
        var second = composer.Compose(new PageCompositionData(
            "placement-second",
            320,
            180,
            [new TextBlockPlacement(block, 170, 90)]));

        using var firstBitmap = SKBitmap.Decode(first.Data.ToArray());
        using var secondBitmap = SKBitmap.Decode(second.Data.ToArray());
        var firstBounds = FindNonWhiteBounds(firstBitmap);
        var secondBounds = FindNonWhiteBounds(secondBitmap);
        Assert.True(secondBounds.Left - firstBounds.Left > 140);
        Assert.True(secondBounds.Top - firstBounds.Top > 60);
    }

    [Fact]
    public void CentersRightToLeftVerticalColumnGroupAcrossTextBox()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var rendered = renderer.Render(new TextBlockRenderData(
            "centered-vertical-columns",
            "MM\nMM",
            Layout(
                new TextLayoutBox(0, 0, 240, 200),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                32,
                letterSpacing: 0)));

        using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
        var bounds = FindVisibleBounds(bitmap);
        var leftMargin = bounds.Left;
        var rightMargin = bitmap.Width - bounds.Right;

        Assert.InRange(Math.Abs(leftMargin - rightMargin), 0, 4);
        Assert.InRange(
            (bounds.Left + bounds.Right) / 2f,
            bitmap.Width / 2f - 2,
            bitmap.Width / 2f + 2);
    }

    [Theory]
    [InlineData("\u2026")]
    [InlineData("\u22EF")]
    [InlineData("\u2013")]
    [InlineData("\u2014")]
    [InlineData("\u2015")]
    public void RotatesHorizontalPunctuationForVerticalText(string punctuation)
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var horizontal = renderer.Render(new TextBlockRenderData(
            $"horizontal-{(int)punctuation[0]:X4}",
            punctuation,
            Layout(
                new TextLayoutBox(0, 0, 160, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));
        var vertical = renderer.Render(new TextBlockRenderData(
            $"vertical-{(int)punctuation[0]:X4}",
            punctuation,
            Layout(
                new TextLayoutBox(0, 0, 100, 160),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));

        using var horizontalBitmap = SKBitmap.Decode(horizontal.PngData.ToArray());
        using var verticalBitmap = SKBitmap.Decode(vertical.PngData.ToArray());
        var horizontalBounds = FindVisibleBounds(horizontalBitmap);
        var verticalBounds = FindVisibleBounds(verticalBitmap);

        Assert.InRange(
            Math.Abs(horizontalBounds.Width - verticalBounds.Height),
            0,
            3);
        Assert.InRange(
            Math.Abs(horizontalBounds.Height - verticalBounds.Width),
            0,
            3);
        Assert.NotEqual(horizontalBounds.Width, horizontalBounds.Height);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("（")]
    [InlineData("“")]
    [InlineData("ー")]
    public void UsesUnicodeVerticalOrientationForOtherRotatedGlyphs(string text)
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var horizontal = renderer.Render(new TextBlockRenderData(
            "unicode-orientation-horizontal",
            text,
            Layout(
                new TextLayoutBox(0, 0, 160, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));
        var vertical = renderer.Render(new TextBlockRenderData(
            "unicode-orientation-vertical",
            text,
            Layout(
                new TextLayoutBox(0, 0, 100, 160),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));

        using var horizontalBitmap = SKBitmap.Decode(horizontal.PngData.ToArray());
        using var verticalBitmap = SKBitmap.Decode(vertical.PngData.ToArray());
        var horizontalBounds = FindVisibleBounds(horizontalBitmap);
        var verticalBounds = FindVisibleBounds(verticalBitmap);
        Assert.InRange(Math.Abs(horizontalBounds.Width - verticalBounds.Height), 0, 3);
        Assert.InRange(Math.Abs(horizontalBounds.Height - verticalBounds.Width), 0, 3);
    }

    [Fact]
    public void KeepsUprightCjkGlyphsAndUsesVerticalPresentationPunctuationBearings()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var upright = renderer.Render(new TextBlockRenderData(
            "upright-cjk",
            "一",
            Layout(
                new TextLayoutBox(0, 0, 100, 160),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));
        using var uprightBitmap = SKBitmap.Decode(upright.PngData.ToArray());
        var uprightBounds = FindVisibleBounds(uprightBitmap);
        Assert.True(uprightBounds.Width > uprightBounds.Height);

        foreach (var punctuation in new[] { "、", "。" })
        {
            var transformed = renderer.Render(new TextBlockRenderData(
                $"transform-upright-{(int)punctuation[0]:X4}",
                punctuation,
                Layout(
                    new TextLayoutBox(0, 0, 100, 160),
                    DialogueTextOrientation.Vertical,
                    DialogueReadingDirection.RightToLeft,
                    DialogueTextAlignment.Center,
                    56,
                    letterSpacing: 0)));
            using var transformedBitmap = SKBitmap.Decode(transformed.PngData.ToArray());
            var transformedBounds = FindVisibleBounds(transformedBitmap);
            var centerX = (transformedBounds.Left + transformedBounds.Right) / 2f;
            var centerY = (transformedBounds.Top + transformedBounds.Bottom) / 2f;
            Assert.True(
                centerX > transformedBitmap.Width / 2f + 5,
                $"U+{(int)punctuation[0]:X4} should retain the presentation glyph's right bearing; "
                + $"actual center=({centerX}, {centerY}).");
            Assert.True(
                centerY < transformedBitmap.Height / 2f - 5,
                $"U+{(int)punctuation[0]:X4} should retain the presentation glyph's upper bearing; "
                + $"actual center=({centerX}, {centerY}).");
        }
    }

    [Theory]
    [InlineData("，", "︐")]
    [InlineData("！", "︕")]
    [InlineData("？", "︖")]
    public void UsesAvailableStandardVerticalPresentationForms(
        string source,
        string presentationForm)
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var vertical = renderer.Render(new TextBlockRenderData(
            "vertical-presentation-source",
            source,
            Layout(
                new TextLayoutBox(0, 0, 100, 160),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));
        var expectedGlyph = renderer.Render(new TextBlockRenderData(
            "horizontal-presentation-reference",
            presentationForm,
            Layout(
                new TextLayoutBox(0, 0, 160, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                56,
                letterSpacing: 0)));

        using var verticalBitmap = SKBitmap.Decode(vertical.PngData.ToArray());
        using var expectedBitmap = SKBitmap.Decode(expectedGlyph.PngData.ToArray());
        var verticalBounds = FindVisibleBounds(verticalBitmap);
        var expectedBounds = FindVisibleBounds(expectedBitmap);
        Assert.InRange(Math.Abs(verticalBounds.Width - expectedBounds.Width), 0, 1);
        Assert.InRange(Math.Abs(verticalBounds.Height - expectedBounds.Height), 0, 1);
    }

    [Fact]
    public void KeepsFullwidthFullStopFallbackDistinctFromIdeographicFullStop()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        RenderedTextBlock RenderPunctuation(string id, string text) => renderer.Render(
            new TextBlockRenderData(
                id,
                text,
                Layout(
                    new TextLayoutBox(0, 0, 100, 160),
                    DialogueTextOrientation.Vertical,
                    DialogueReadingDirection.RightToLeft,
                    DialogueTextAlignment.Center,
                    56,
                    letterSpacing: 0)));

        var fullwidth = RenderPunctuation("fullwidth-full-stop", "．");
        var ideographic = RenderPunctuation("ideographic-full-stop", "。");

        using var fullwidthBitmap = SKBitmap.Decode(fullwidth.PngData.ToArray());
        using var ideographicBitmap = SKBitmap.Decode(ideographic.PngData.ToArray());
        Assert.NotEqual(
            CountVisiblePixels(fullwidthBitmap),
            CountVisiblePixels(ideographicBitmap));
    }

    [Fact]
    public void FallsBackSafelyWhenVerticalPresentationGlyphIsUnavailable()
    {
        var available = UnicodeVerticalOrientation.ResolveGlyph("、", _ => true);
        var unavailable = UnicodeVerticalOrientation.ResolveGlyph("、", _ => false);
        var noExactPresentation = UnicodeVerticalOrientation.ResolveGlyph("．", _ => true);

        Assert.Equal("︑", available.Text);
        Assert.Equal(VerticalGlyphOrientation.PresentationForm, available.Orientation);
        Assert.Equal("、", unavailable.Text);
        Assert.Equal(VerticalGlyphOrientation.TransformUpright, unavailable.Orientation);
        Assert.Equal("．", noExactPresentation.Text);
        Assert.Equal(
            VerticalGlyphOrientation.TransformUpright,
            noExactPresentation.Orientation);
    }

    [Fact]
    public void RealF0001FallbackPlacesCompactTuPunctuationUpperRight()
    {
        var library = new FontLibrary(Path.Combine(
            FindRepositoryRoot(),
            "resources",
            "font-library"));
        var entry = library.ReadCatalog().Fonts.Single(font => font.FontId == "F0001");
        var regular = Assert.IsType<FontFaceAsset>(entry.Regular);
        using var typeface = SKTypeface.FromFile(
            library.ResolveManagedPath(regular.RelativePath),
            regular.FaceIndex);
        Assert.NotNull(typeface);
        using var glyphFont = new SKFont(typeface);
        Assert.False(glyphFont.ContainsGlyphs("︑"));
        Assert.False(glyphFont.ContainsGlyphs("︒"));

        using var renderer = new SkiaTextBlockRenderer(library);
        RenderedTextBlock RenderVertical(string id, string text) => renderer.Render(
            new TextBlockRenderData(
                id,
                text,
                Layout(
                    new TextLayoutBox(0, 0, 100, 160),
                    DialogueTextOrientation.Vertical,
                    DialogueReadingDirection.RightToLeft,
                    DialogueTextAlignment.Center,
                    56,
                    letterSpacing: 0,
                    fontId: "F0001")));

        foreach (var punctuation in new[] { "、", "。" })
        {
            var rendered = RenderVertical(
                $"f0001-tu-{(int)punctuation[0]:X4}",
                punctuation);
            using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
            var bounds = FindVisibleBounds(bitmap);
            var centerX = (bounds.Left + bounds.Right) / 2f;
            var centerY = (bounds.Top + bounds.Bottom) / 2f;
            Assert.True(centerX > bitmap.Width / 2f + 5);
            Assert.True(centerY < bitmap.Height / 2f - 5);
            Assert.True(bounds.Left > 0 && bounds.Top > 0);
            Assert.True(bounds.Right < bitmap.Width && bounds.Bottom < bitmap.Height);
        }

        foreach (var punctuation in new[] { "！", "？" })
        {
            var rendered = RenderVertical(
                $"f0001-large-tu-{(int)punctuation[0]:X4}",
                punctuation);
            using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
            var bounds = FindVisibleBounds(bitmap);
            Assert.InRange(
                (bounds.Left + bounds.Right) / 2f,
                bitmap.Width / 2f - 4,
                bitmap.Width / 2f + 4);
            Assert.InRange(
                (bounds.Top + bounds.Bottom) / 2f,
                bitmap.Height / 2f - 4,
                bitmap.Height / 2f + 4);
        }

        foreach (var rotated in new[] { "…", "—" })
        {
            var rendered = RenderVertical(
                $"f0001-rotated-{(int)rotated[0]:X4}",
                rotated);
            using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
            var bounds = FindVisibleBounds(bitmap);
            Assert.True(bounds.Height > bounds.Width);
        }
    }

    [Fact]
    public void AutomaticallyOutlinesNearWhiteTextForWhiteBackgroundRendering()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var automatic = renderer.Render(new TextBlockRenderData(
            "automatic-white-contrast",
            "WHITE",
            Layout(
                new TextLayoutBox(0, 0, 240, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                48,
                letterSpacing: 0) with
            { Color = "#F8F8F8" }));
        var explicitStroke = renderer.Render(new TextBlockRenderData(
            "explicit-white-contrast",
            "WHITE",
            Layout(
                new TextLayoutBox(0, 0, 240, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                48,
                letterSpacing: 0,
                stroke: new DialogueTextStroke(2, "#2563EB", true)) with
            { Color = "#FFFFFF" }));
        var ineffectiveStroke = renderer.Render(new TextBlockRenderData(
            "ineffective-white-contrast",
            "WHITE",
            Layout(
                new TextLayoutBox(0, 0, 240, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                48,
                letterSpacing: 0,
                stroke: new DialogueTextStroke(3, "#FFFFFF", false)) with
            { Color = "#FFFFFF" }));

        using var automaticBitmap = SKBitmap.Decode(automatic.PngData.ToArray());
        using var explicitBitmap = SKBitmap.Decode(explicitStroke.PngData.ToArray());
        using var ineffectiveBitmap = SKBitmap.Decode(ineffectiveStroke.PngData.ToArray());
        Assert.True(CountPixels(automaticBitmap, SKColors.Black) > 10);
        Assert.True(CountPixels(automaticBitmap, new SKColor(248, 248, 248)) > 10);
        Assert.Equal(0, CountPixels(explicitBitmap, SKColors.Black));
        Assert.True(CountPixels(explicitBitmap, new SKColor(37, 99, 235)) > 10);
        Assert.True(CountPixels(ineffectiveBitmap, SKColors.Black) > 10);
    }

    [Fact]
    public void OutlinesTranslucentNearWhiteTextWithoutReplacingEffectiveExplicitStroke()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        DialogueTextLayout TranslucentLayout(DialogueTextStroke? stroke = null) => Layout(
            new TextLayoutBox(0, 0, 240, 100),
            DialogueTextOrientation.Horizontal,
            DialogueReadingDirection.LeftToRight,
            DialogueTextAlignment.Center,
            48,
            letterSpacing: 0,
            stroke: stroke) with
        { Color = "#FFFFFF80" };

        var automatic = renderer.Render(new TextBlockRenderData(
            "automatic-translucent-white-contrast",
            "WHITE",
            TranslucentLayout()));
        var opaqueExplicit = renderer.Render(new TextBlockRenderData(
            "explicit-translucent-white-contrast",
            "WHITE",
            TranslucentLayout(new DialogueTextStroke(2, "#2563EB", true))));
        var translucentExplicit = renderer.Render(new TextBlockRenderData(
            "explicit-translucent-stroke-contrast",
            "WHITE",
            TranslucentLayout(new DialogueTextStroke(2, "#00000080", true))));

        using var automaticBitmap = SKBitmap.Decode(automatic.PngData.ToArray());
        using var opaqueExplicitBitmap = SKBitmap.Decode(opaqueExplicit.PngData.ToArray());
        using var translucentExplicitBitmap = SKBitmap.Decode(
            translucentExplicit.PngData.ToArray());
        Assert.True(CountPixels(automaticBitmap, SKColors.Black) > 10);
        Assert.True(CountPixels(automaticBitmap, new SKColor(255, 255, 255, 128)) > 10);
        Assert.Equal(0, CountPixels(opaqueExplicitBitmap, SKColors.Black));
        Assert.True(
            CountPixels(opaqueExplicitBitmap, new SKColor(37, 99, 235)) > 10);
        Assert.Equal(0, CountPixels(translucentExplicitBitmap, SKColors.Black));
        Assert.True(
            CountPixels(translucentExplicitBitmap, new SKColor(0, 0, 0, 128)) > 10);
    }

    [Fact]
    public void ReservesAutomaticAndExplicitOutlinesInsideAlignedTextBoxEdges()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var horizontalStart = renderer.Render(new TextBlockRenderData(
            "outlined-horizontal-start",
            "MMMM",
            Layout(
                new TextLayoutBox(0, 0, 260, 72),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Start,
                48,
                letterSpacing: 0) with
            { Color = "#FFFFFF" }));
        var horizontalEnd = renderer.Render(new TextBlockRenderData(
            "outlined-horizontal-end",
            "MMMM",
            Layout(
                new TextLayoutBox(0, 0, 260, 72),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.End,
                48,
                letterSpacing: 0) with
            { Color = "#FFFFFF" }));
        var verticalStart = renderer.Render(new TextBlockRenderData(
            "outlined-vertical-start",
            "一二",
            Layout(
                new TextLayoutBox(0, 0, 84, 180),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Start,
                48,
                letterSpacing: 0) with
            { Color = "#FFFFFF" }));
        var verticalEnd = renderer.Render(new TextBlockRenderData(
            "outlined-vertical-end",
            "一二",
            Layout(
                new TextLayoutBox(0, 0, 84, 180),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.End,
                48,
                letterSpacing: 0) with
            { Color = "#FFFFFF" }));
        var explicitHorizontalEnd = renderer.Render(new TextBlockRenderData(
            "explicit-horizontal-end",
            "MMMM",
            Layout(
                new TextLayoutBox(0, 0, 260, 72),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.End,
                48,
                letterSpacing: 0,
                stroke: new DialogueTextStroke(4, "#2563EB", true))));
        var explicitVerticalStart = renderer.Render(new TextBlockRenderData(
            "explicit-vertical-start",
            "一二",
            Layout(
                new TextLayoutBox(0, 0, 84, 180),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Start,
                48,
                letterSpacing: 0,
                stroke: new DialogueTextStroke(4, "#2563EB", true))));

        using var horizontalStartBitmap = SKBitmap.Decode(horizontalStart.PngData.ToArray());
        using var horizontalEndBitmap = SKBitmap.Decode(horizontalEnd.PngData.ToArray());
        using var verticalStartBitmap = SKBitmap.Decode(verticalStart.PngData.ToArray());
        using var verticalEndBitmap = SKBitmap.Decode(verticalEnd.PngData.ToArray());
        using var explicitHorizontalBitmap = SKBitmap.Decode(
            explicitHorizontalEnd.PngData.ToArray());
        using var explicitVerticalBitmap = SKBitmap.Decode(explicitVerticalStart.PngData.ToArray());
        var horizontalStartBounds = FindVisibleBounds(horizontalStartBitmap);
        var horizontalEndBounds = FindVisibleBounds(horizontalEndBitmap);
        var verticalStartBounds = FindVisibleBounds(verticalStartBitmap);
        var verticalEndBounds = FindVisibleBounds(verticalEndBitmap);
        var explicitHorizontalBounds = FindVisibleBounds(explicitHorizontalBitmap);
        var explicitVerticalBounds = FindVisibleBounds(explicitVerticalBitmap);

        Assert.True(horizontalStartBounds.Left > 0);
        Assert.True(horizontalStartBounds.Top > 0);
        Assert.True(horizontalEndBounds.Right < horizontalEndBitmap.Width);
        Assert.True(horizontalEndBounds.Top > 0);
        Assert.True(verticalStartBounds.Left > 0);
        Assert.True(verticalStartBounds.Top > 0);
        Assert.True(verticalStartBounds.Right < verticalStartBitmap.Width);
        Assert.True(verticalEndBounds.Left > 0);
        Assert.True(verticalEndBounds.Right < verticalEndBitmap.Width);
        Assert.True(verticalEndBounds.Bottom < verticalEndBitmap.Height);
        Assert.True(explicitHorizontalBounds.Top > 0);
        Assert.True(explicitHorizontalBounds.Right < explicitHorizontalBitmap.Width);
        Assert.True(explicitVerticalBounds.Left > 0);
        Assert.True(explicitVerticalBounds.Top > 0);
        Assert.True(explicitVerticalBounds.Right < explicitVerticalBitmap.Width);
    }

    [Fact]
    public void ReservesItalicSideBearingsAndOutlineAtHorizontalEdges()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        RenderedTextBlock RenderAligned(string id, DialogueTextAlignment alignment) =>
            renderer.Render(new TextBlockRenderData(
                id,
                "f",
                Layout(
                    new TextLayoutBox(0, 0, 64, 76),
                    DialogueTextOrientation.Horizontal,
                    DialogueReadingDirection.LeftToRight,
                    alignment,
                    56,
                    italic: true,
                    letterSpacing: 0,
                    stroke: new DialogueTextStroke(3, "#2563EB", true))));

        var start = RenderAligned("italic-start", DialogueTextAlignment.Start);
        var end = RenderAligned("italic-end", DialogueTextAlignment.End);

        using var startBitmap = SKBitmap.Decode(start.PngData.ToArray());
        using var endBitmap = SKBitmap.Decode(end.PngData.ToArray());
        var startBounds = FindVisibleBounds(startBitmap);
        var endBounds = FindVisibleBounds(endBitmap);
        Assert.True(startBounds.Left > 0);
        Assert.True(startBounds.Top > 0);
        Assert.True(endBounds.Right < endBitmap.Width);
        Assert.True(endBounds.Top > 0);
    }

    [Fact]
    public void VerticalOutlineAddsOnlyOuterInsetsInsteadOfPerCharacterAdvance()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        DialogueTextLayout VerticalLayout(string color) => Layout(
            new TextLayoutBox(0, 0, 100, 420),
            DialogueTextOrientation.Vertical,
            DialogueReadingDirection.RightToLeft,
            DialogueTextAlignment.Center,
            32,
            letterSpacing: 1) with
        { Color = color };
        const string Text = "一二三四五六七八九十";
        var dark = renderer.Render(new TextBlockRenderData(
            "vertical-dark-no-outline",
            Text,
            VerticalLayout("#202020")));
        var white = renderer.Render(new TextBlockRenderData(
            "vertical-white-auto-outline",
            Text,
            VerticalLayout("#FFFFFF")));

        using var darkBitmap = SKBitmap.Decode(dark.PngData.ToArray());
        using var whiteBitmap = SKBitmap.Decode(white.PngData.ToArray());
        var darkBounds = FindVisibleBounds(darkBitmap);
        var whiteBounds = FindVisibleBounds(whiteBitmap);
        Assert.InRange(whiteBounds.Height - darkBounds.Height, 0, 6);
        Assert.InRange(
            Math.Abs(
                (whiteBounds.Top + whiteBounds.Bottom)
                - (darkBounds.Top + darkBounds.Bottom)),
            0,
            2);
    }

    [Fact]
    public void HorizontalOutlineAddsOnlyOuterInsetsInsteadOfPerLineHeight()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        DialogueTextLayout HorizontalLayout(string color) => Layout(
            new TextLayoutBox(0, 0, 260, 420),
            DialogueTextOrientation.Horizontal,
            DialogueReadingDirection.LeftToRight,
            DialogueTextAlignment.Start,
            28,
            letterSpacing: 0) with
        { Color = color };
        const string Text = "MMMM\nMMMM\nMMMM\nMMMM\nMMMM\nMMMM\nMMMM\nMMMM";
        var dark = renderer.Render(new TextBlockRenderData(
            "horizontal-dark-no-outline",
            Text,
            HorizontalLayout("#202020")));
        var white = renderer.Render(new TextBlockRenderData(
            "horizontal-white-auto-outline",
            Text,
            HorizontalLayout("#FFFFFF")));

        using var darkBitmap = SKBitmap.Decode(dark.PngData.ToArray());
        using var whiteBitmap = SKBitmap.Decode(white.PngData.ToArray());
        var darkBounds = FindVisibleBounds(darkBitmap);
        var whiteBounds = FindVisibleBounds(whiteBitmap);
        Assert.InRange(whiteBounds.Height - darkBounds.Height, 0, 8);
    }

    [Fact]
    public void FromDialogueExpandsPlacementGroupsAndProjectsAdvancedIndexes()
    {
        var result = CreatePlacementGroupResult(includeRotation: true);

        var page = PageRenderData.FromDialogue(result, 600, 450);

        Assert.Collection(
            page.TextBlocks,
            first =>
            {
                Assert.Equal("segment-a:placement:0001", first.BlockId);
                Assert.Equal("A\U0001F600\ne\u0301B", first.Text);
                Assert.Equal(new TextLayoutBox(100, 50, 250, 300), first.Layout.TextBox);
                Assert.Equal("F0001", first.Layout.FontId);
                Assert.Equal(DialogueTextOrientation.Vertical, first.Layout.Orientation);
                var advanced = Assert.IsType<DialogueAdvancedLayout>(first.Layout.Advanced);
                Assert.Empty(advanced.PlacementGroups);
                Assert.Equal(1, Assert.Single(advanced.LineAdjustments).LineIndex);
                Assert.Equal(2, Assert.Single(advanced.CharacterAdjustments).CharacterIndex);
                Assert.Equal(2, Assert.Single(advanced.RubyAnnotations).BaseCharacterIndex);
                var rotation = Assert.IsType<DialogueBoxRotation>(first.Layout.Rotation);
                Assert.Equal(200, rotation.CenterXPx);
                Assert.Equal(175, rotation.CenterYPx);
            },
            second =>
            {
                Assert.Equal("segment-a:placement:0002", second.BlockId);
                Assert.Equal("CD", second.Text);
                Assert.Equal(new TextLayoutBox(320, 80, 500, 400), second.Layout.TextBox);
                var advanced = Assert.IsType<DialogueAdvancedLayout>(second.Layout.Advanced);
                Assert.Empty(advanced.PlacementGroups);
                Assert.Equal(0, Assert.Single(advanced.LineAdjustments).LineIndex);
                Assert.Equal(1, Assert.Single(advanced.CharacterAdjustments).CharacterIndex);
                Assert.Equal(0, Assert.Single(advanced.RubyAnnotations).BaseCharacterIndex);
                var rotation = Assert.IsType<DialogueBoxRotation>(second.Layout.Rotation);
                Assert.Equal(-20, rotation.CenterXPx);
                Assert.Equal(145, rotation.CenterYPx);
            });

        var firstAbsoluteCenter = (
            X: page.TextBlocks[0].Layout.TextBox.X0
                + page.TextBlocks[0].Layout.Rotation!.CenterXPx,
            Y: page.TextBlocks[0].Layout.TextBox.Y0
                + page.TextBlocks[0].Layout.Rotation!.CenterYPx);
        var secondAbsoluteCenter = (
            X: page.TextBlocks[1].Layout.TextBox.X0
                + page.TextBlocks[1].Layout.Rotation!.CenterXPx,
            Y: page.TextBlocks[1].Layout.TextBox.Y0
                + page.TextBlocks[1].Layout.Rotation!.CenterYPx);
        Assert.Equal(firstAbsoluteCenter, secondAbsoluteCenter);
    }

    [Fact]
    public void FromDialogueNormalizesLegacyPageRelativeRotationCenter()
    {
        var result = CreatePlacementGroupResult(
            includeRotation: false,
            includeGroups: false);
        var original = Assert.Single(result.Segments);
        var box = new TextLayoutBox(275, 1238, 470, 1345);
        var legacyPageCenter = new DialogueBoxRotation(372.5f, 1291.5f, -15);
        var localCenter = new DialogueBoxRotation(97.5f, 53.5f, -15);
        var legacyResult = result with
        {
            Segments =
            [
                original with
                {
                    Layout = original.Layout with
                    {
                        TextBox = box,
                        Rotation = legacyPageCenter,
                        Advanced = null
                    }
                }
            ]
        };
        var localResult = legacyResult with
        {
            Segments =
            [
                Assert.Single(legacyResult.Segments) with
                {
                    Layout = Assert.Single(legacyResult.Segments).Layout with
                    {
                        Rotation = localCenter
                    }
                }
            ]
        };

        var legacyPage = PageRenderData.FromDialogue(legacyResult, 1440, 2048);
        var localPage = PageRenderData.FromDialogue(localResult, 1440, 2048);

        Assert.Equal(localCenter, Assert.Single(legacyPage.TextBlocks).Layout.Rotation);
        var library = CreateManagedFontLibrary();
        using var blockRenderer = new SkiaTextBlockRenderer(library);
        var renderer = new DialoguePageRenderer(blockRenderer, new SkiaPageComposer());
        var legacyRendered = renderer.Render(legacyPage);
        var localRendered = renderer.Render(localPage);
        Assert.Equal(localRendered.Data.ToArray(), legacyRendered.Data.ToArray());
    }

    [Fact]
    public void KeepsEmptyPlacementGroupsAsOneLegacyBlock()
    {
        var result = CreatePlacementGroupResult(
            includeRotation: false,
            includeGroups: false);
        var original = Assert.Single(result.Segments);

        var page = PageRenderData.FromDialogue(result, 600, 450);

        var block = Assert.Single(page.TextBlocks);
        Assert.Equal(original.SegmentId, block.BlockId);
        Assert.Equal(original.TranslatedText, block.Text);
        Assert.Same(original.Layout, block.Layout);
    }

    [Fact]
    public void RejectsRubyThatSpansPlacementGroups()
    {
        var result = CreatePlacementGroupResult(includeRotation: false);
        var segment = Assert.Single(result.Segments);
        var invalidAdvanced = segment.Layout.Advanced! with
        {
            RubyAnnotations =
            [
                new DialogueRubyAnnotation(3, 2, "crosses", DialogueRubyPlacement.Before)
            ]
        };
        var invalidSegment = segment with
        {
            Layout = segment.Layout with { Advanced = invalidAdvanced }
        };
        var invalidResult = result with { Segments = [invalidSegment] };

        var exception = Assert.Throws<ArgumentException>(() =>
            PageRenderData.FromDialogue(invalidResult, 600, 450));

        Assert.Contains("crosses placement groups", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugDrawsEveryPlacementGroupBoxWithoutDrawingTheEnvelope()
    {
        var result = CreatePlacementGroupResult(includeRotation: false);
        var pageData = PageRenderData.FromDialogue(result, 600, 450);
        var library = CreateManagedFontLibrary();
        using var blockRenderer = new SkiaTextBlockRenderer(library);
        var renderer = new DialoguePageRenderer(blockRenderer, new SkiaPageComposer());

        var page = renderer.Render(pageData, debug: true);

        using var bitmap = SKBitmap.Decode(page.Data.ToArray());
        var debugRed = new SKColor(220, 38, 38);
        Assert.Equal(debugRed, bitmap.GetPixel(100, 50));
        Assert.Equal(debugRed, bitmap.GetPixel(320, 80));
        Assert.NotEqual(debugRed, bitmap.GetPixel(300, 50));
    }

    [Fact]
    public void DefaultRubyPlacementKeepsGlyphsInsideTextBlockEdges()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var horizontal = renderer.Render(new TextBlockRenderData(
            "horizontal-ruby",
            "Hello",
            Layout(
                new TextLayoutBox(0, 0, 220, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                32,
                advanced: new DialogueAdvancedLayout(
                    [],
                    [],
                    [new DialogueRubyAnnotation(0, 5, "greeting", DialogueRubyPlacement.Before)]))));
        var vertical = renderer.Render(new TextBlockRenderData(
            "vertical-ruby",
            "MANGA",
            Layout(
                new TextLayoutBox(0, 0, 180, 260),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                34,
                advanced: new DialogueAdvancedLayout(
                    [],
                    [],
                    [new DialogueRubyAnnotation(0, 2, "MA", DialogueRubyPlacement.Before)]))));

        using var horizontalBitmap = SKBitmap.Decode(horizontal.PngData.ToArray());
        using var verticalBitmap = SKBitmap.Decode(vertical.PngData.ToArray());
        var horizontalBounds = FindVisibleBounds(horizontalBitmap);
        var verticalBounds = FindVisibleBounds(verticalBitmap);
        Assert.True(horizontalBounds.Top > 0);
        Assert.True(horizontalBounds.Right < horizontalBitmap.Width);
        Assert.True(verticalBounds.Top > 0);
        Assert.True(verticalBounds.Right < verticalBitmap.Width);
    }

    [Fact]
    public void SupportsArbitraryWeightRoundedStrokeAndBoxRotation()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var light = renderer.Render(new TextBlockRenderData(
            "weight-200",
            "字重测试",
            Layout(
                new TextLayoutBox(0, 0, 220, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                32,
                fontWeight: 200)));
        var heavy = renderer.Render(new TextBlockRenderData(
            "weight-900",
            "字重测试",
            Layout(
                new TextLayoutBox(0, 0, 220, 100),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                32,
                fontWeight: 900)));
        var outlined = renderer.Render(new TextBlockRenderData(
            "outlined",
            "描边",
            Layout(
                new TextLayoutBox(0, 0, 160, 90),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Center,
                32,
                fontWeight: 650,
                stroke: new DialogueTextStroke(3, "#FF00FF", true))));

        using var lightBitmap = SKBitmap.Decode(light.PngData.ToArray());
        using var heavyBitmap = SKBitmap.Decode(heavy.PngData.ToArray());
        using var outlinedBitmap = SKBitmap.Decode(outlined.PngData.ToArray());
        Assert.True(CountVisiblePixels(heavyBitmap) > CountVisiblePixels(lightBitmap));
        Assert.True(CountPixels(outlinedBitmap, new SKColor(255, 0, 255)) > 20);

        var rotatedPage = new SkiaPageComposer().Compose(
            new PageCompositionData(
                "rotated-box",
                360,
                240,
                [
                    new TextBlockPlacement(
                        outlined,
                        100,
                        80,
                        new DialogueBoxRotation(80, 45, 25))
                ]),
            debug: true);
        using var rotatedBitmap = SKBitmap.Decode(rotatedPage.Data.ToArray());
        var border = FindColorBounds(rotatedBitmap, new SKColor(220, 38, 38));
        Assert.True(border.Left < 100 || border.Top < 80 || border.Right > 260 || border.Bottom > 170);
    }

    [Fact]
    public void UsesOptionalBackgroundWithoutImplicitlyScalingIt()
    {
        var composer = new SkiaPageComposer();
        var page = new PageCompositionData("background-page", 64, 48, []);
        var background = new PageBackground("image/png", CreateSolidPng(64, 48, SKColors.LightBlue));

        var result = composer.Compose(page, background);

        using var bitmap = SKBitmap.Decode(result.Data.ToArray());
        Assert.Equal(SKColors.LightBlue, bitmap.GetPixel(0, 0));
        var invalidBackground = new PageBackground(
            "image/png",
            CreateSolidPng(32, 24, SKColors.LightBlue));
        Assert.Throws<RenderingException>(() => composer.Compose(page, invalidBackground));
    }

    [Fact]
    public void RejectsFontsThatAreNotInTheManagedLibrary()
    {
        var library = CreateManagedFontLibrary();
        using var renderer = new SkiaTextBlockRenderer(library);
        var data = new TextBlockRenderData(
            "unknown-font",
            "Text",
            Layout(
                new TextLayoutBox(0, 0, 120, 60),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Start,
                24) with
            { FontId = "Arial" });

        var exception = Assert.Throws<RenderingException>(() => renderer.Render(data));
        Assert.Contains("managed font library", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DialogueTranslationResult CreatePlacementGroupResult(
        bool includeRotation,
        bool includeGroups = true)
    {
        IReadOnlyList<DialoguePlacementGroup> groups = includeGroups
            ?
            [
                new DialoguePlacementGroup(
                    [0, 1],
                    ["page-a:region:0001"],
                    new TextLayoutBox(100, 50, 250, 300)),
                new DialoguePlacementGroup(
                    [2],
                    ["page-a:region:0002"],
                    new TextLayoutBox(320, 80, 500, 400))
            ]
            : [];
        var advanced = new DialogueAdvancedLayout(
            [
                new DialogueLineAdjustment(1, 3, -2),
                new DialogueLineAdjustment(2, -1, 4)
            ],
            [
                new DialogueCharacterAdjustment(2, 1, 2),
                new DialogueCharacterAdjustment(5, -2, 1)
            ],
            [
                new DialogueRubyAnnotation(2, 2, "first", DialogueRubyPlacement.Before),
                new DialogueRubyAnnotation(4, 2, "second", DialogueRubyPlacement.After)
            ])
        {
            PlacementGroups = groups
        };
        var segment = new DialogueSegment(
            "segment-a",
            DialogueSegmentKind.Dialogue,
            DialogueSegmentStatus.Translated,
            1,
            ["page-a:region:0001", "page-a:region:0002"],
            "source",
            "A\U0001F600\ne\u0301B\nCD",
            Layout(
                new TextLayoutBox(100, 50, 500, 400),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                32,
                advanced: advanced,
                rotation: includeRotation
                    ? new DialogueBoxRotation(200, 175, 12)
                    : null),
            "speaker-a",
            0.98,
            []);

        return new DialogueTranslationResult(
            "1.2.0",
            "run-a",
            "book-a",
            "zh-Hans",
            "page-a",
            "provider-a",
            "model-a",
            [segment],
            [],
            [],
            null,
            new ModelResponseMetadata(null, TimeSpan.Zero));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }

    private FontLibrary CreateManagedFontLibrary()
    {
        var sources = FindTestFonts();
        Assert.NotEmpty(sources);
        var library = new FontLibrary(rootDirectory);
        for (var index = 0; index < 3; index++)
        {
            var source = sources[index % sources.Length];
            library.Add(new AddFontRequest
            {
                FontId = $"F{index + 1:0000}",
                DisplayName = $"Rendering Test Font {index + 1}",
                SourceKind = "test-fixture",
                Regular = new FontStyleInput { SourcePath = source }
            });
        }

        return library;
    }

    private static DialogueTextLayout Layout(
        TextLayoutBox box,
        DialogueTextOrientation orientation,
        DialogueReadingDirection direction,
        DialogueTextAlignment alignment,
        float fontSize,
        bool bold = false,
        bool italic = false,
        float letterSpacing = 1,
        DialogueAdvancedLayout? advanced = null,
        string fontId = "F0001",
        int fontWeight = 400,
        DialogueBoxRotation? rotation = null,
        DialogueTextStroke? stroke = null)
    {
        return new DialogueTextLayout(
            box,
            orientation,
            direction,
            alignment,
            fontId,
            fontSize,
            bold,
            italic,
            "#1F2937",
            letterSpacing,
            advanced,
            fontWeight,
            rotation,
            stroke);
    }

    private static string[] FindTestFonts()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        [
            Path.Combine(fonts, "msyh.ttc"),
            Path.Combine(fonts, "simhei.ttf"),
            Path.Combine(fonts, "simsun.ttc"),
            Path.Combine(fonts, "Deng.ttf"),
            Path.Combine(fonts, "arial.ttf"),
            Path.Combine(fonts, "times.ttf"),
            Path.Combine(fonts, "consola.ttf"),
            Path.Combine(fonts, "segoeui.ttf"),
            Path.Combine(fonts, "tahoma.ttf"),
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
            "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/Hiragino Sans GB.ttc",
            "/Library/Fonts/Arial.ttf",
            "/Library/Fonts/Times New Roman.ttf",
            "/Library/Fonts/Courier New.ttf"
        ];
        return candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountPixels(SKBitmap bitmap, SKColor color)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountVisiblePixels(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKRectI FindColorBounds(SKBitmap bitmap, SKColor color)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != color)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left && bottom >= top, $"Color {color} was not rendered.");
        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static ReadOnlyMemory<byte> CreateRulerPng(
        int width,
        int height,
        FontLibrary library)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        using var minorPaint = new SKPaint
        {
            Color = new SKColor(226, 232, 240),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false
        };
        using var majorPaint = new SKPaint
        {
            Color = new SKColor(148, 163, 184),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false
        };
        for (var x = 10; x < width; x += 10)
        {
            surface.Canvas.DrawLine(
                x + 0.5f,
                0,
                x + 0.5f,
                height,
                x % 50 == 0 ? majorPaint : minorPaint);
        }

        for (var y = 10; y < height; y += 10)
        {
            surface.Canvas.DrawLine(
                0,
                y + 0.5f,
                width,
                y + 0.5f,
                y % 50 == 0 ? majorPaint : minorPaint);
        }

        var entry = library.ReadCatalog().Fonts.Single(font => font.FontId == "F0001");
        var asset = entry.Regular ?? throw new InvalidOperationException(
            "The ruler test font has no regular face.");
        using var typeface = SKTypeface.FromFile(
            library.ResolveManagedPath(asset.RelativePath),
            asset.FaceIndex) ?? throw new InvalidOperationException(
                "The managed ruler font could not be loaded.");
        using var labelFont = new SKFont(typeface, 9);
        using var labelPaint = new SKPaint
        {
            Color = new SKColor(71, 85, 105),
            IsAntialias = true
        };
        for (var x = 50; x < width; x += 50)
        {
            surface.Canvas.DrawText(
                x.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x + 2,
                10,
                SKTextAlign.Left,
                labelFont,
                labelPaint);
        }

        for (var y = 50; y < height; y += 50)
        {
            surface.Canvas.DrawText(
                y.ToString(System.Globalization.CultureInfo.InvariantCulture),
                2,
                y - 2,
                SKTextAlign.Left,
                labelFont,
                labelPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKRectI FindNonWhiteBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.White)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left && bottom >= top, "Rendered page contains no text pixels.");
        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static SKRectI FindVisibleBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left && bottom >= top, "Rendered block contains no visible pixels.");
        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static ReadOnlyMemory<byte> CreateSolidPng(
        int width,
        int height,
        SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
