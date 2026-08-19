using System.Globalization;
using MangaTranslator.Dialogue;
using MangaTranslator.FontManager;
using SkiaSharp;

namespace MangaTranslator.Rendering;

/// <summary>
/// Renders text exclusively with font files resolved from a FontManager library. No operating-
/// system font lookup or default-font fallback is used.
/// </summary>
public sealed class SkiaTextBlockRenderer : ITextBlockRenderer, IDisposable
{
    private const float SyntheticItalicSkew = -0.2f;
    private const float ClipSafetyInset = 1.5f;
    private const float CompactTuGlyphRatio = 0.68f;
    private const float VerticalTuRightTarget = 0.7f;
    private const float VerticalTuTopTarget = 0.3f;
    private readonly FontLibrary fontLibrary;
    private readonly string? fallbackFontId;
    private readonly Dictionary<FontKey, TypefaceSelection> typefaces = [];
    private bool disposed;

    public SkiaTextBlockRenderer(FontLibrary fontLibrary, string? fallbackFontId = null)
    {
        this.fontLibrary = fontLibrary ?? throw new ArgumentNullException(nameof(fontLibrary));
        this.fallbackFontId = string.IsNullOrWhiteSpace(fallbackFontId)
            ? null
            : fallbackFontId.Trim();
    }

    public RenderedTextBlock Render(TextBlockRenderData block)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateBlock(block);

        var box = block.Layout.TextBox;
        var width = box.X1 - box.X0;
        var height = box.Y1 - box.Y0;
        var typeface = ResolveTypeface(
            block.Layout.FontId,
            block.Layout.EffectiveFontWeight,
            block.Layout.Italic);
        var fallbackTypeface = ResolveFallbackTypeface(
            block.Layout.FontId,
            block.Layout.EffectiveFontWeight,
            block.Layout.Italic);
        var context = CreateContext(block);
        var rubyInsets = MeasureRubyInsets(block);

        using var surface = SKSurface.Create(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        if (surface is null)
        {
            throw new RenderingException(
                $"SkiaSharp could not create a surface for block '{block.BlockId}'.");
        }

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Save();
        surface.Canvas.ClipRect(new SKRect(0, 0, width, height));
        List<GlyphPlacement> placements = block.Layout.Orientation switch
        {
            DialogueTextOrientation.Horizontal => DrawHorizontal(
                surface.Canvas,
                block,
                context,
                typeface,
                fallbackTypeface,
                width,
                rubyInsets.Top),
            DialogueTextOrientation.Vertical => DrawVertical(
                surface.Canvas,
                block,
                context,
                typeface,
                fallbackTypeface,
                width,
                height,
                rubyInsets.Left,
                rubyInsets.Right),
            _ => throw new RenderingException(
                $"Unsupported text orientation '{block.Layout.Orientation}'.")
        };
        DrawRuby(surface.Canvas, block, context, typeface, fallbackTypeface, placements);
        surface.Canvas.Restore();

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new RenderingException(
                $"SkiaSharp could not encode text block '{block.BlockId}'.");
        return new RenderedTextBlock(block.BlockId, width, height, encoded.ToArray());
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var selection in typefaces.Values)
        {
            selection.Typeface.Dispose();
        }

        typefaces.Clear();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static RenderContext CreateContext(TextBlockRenderData block)
    {
        var normalizedText = block.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        var elements = new List<IReadOnlyList<TextElement>>(lines.Length);
        var globalIndex = 0;
        foreach (var line in lines)
        {
            var lineElements = EnumerateTextElements(line)
                .Select(value => new TextElement(globalIndex++, value))
                .ToArray();
            elements.Add(lineElements);
        }

        var advanced = block.Layout.Advanced;
        var lineAdjustments = ToUniqueDictionary(
            advanced?.LineAdjustments ?? [],
            item => item.LineIndex,
            block.BlockId,
            "line adjustment");
        var characterAdjustments = ToUniqueDictionary(
            advanced?.CharacterAdjustments ?? [],
            item => item.CharacterIndex,
            block.BlockId,
            "character adjustment");

        foreach (var index in lineAdjustments.Keys)
        {
            if (index < 0 || index >= lines.Length)
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' line adjustment index {index} is out of range.");
            }
        }

        foreach (var index in characterAdjustments.Keys)
        {
            if (index < 0 || index >= globalIndex)
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' character adjustment index {index} is out of range.");
            }
        }

        ValidateRuby(block, globalIndex);
        return new RenderContext(elements, lineAdjustments, characterAdjustments);
    }

    private static List<GlyphPlacement> DrawHorizontal(
        SKCanvas canvas,
        TextBlockRenderData block,
        RenderContext context,
        TypefaceSelection typeface,
        TypefaceSelection? fallbackTypeface,
        int width,
        float topInset)
    {
        var result = new List<GlyphPlacement>();
        var cursorY = topInset;
        var contentLineIndices = Enumerable.Range(0, context.Lines.Count)
            .Where(index => context.Lines[index].Count > 0)
            .ToArray();
        var firstContentLine = contentLineIndices.Length == 0 ? -1 : contentLineIndices[0];
        var lastContentLine = contentLineIndices.Length == 0 ? -1 : contentLineIndices[^1];
        for (var lineIndex = 0; lineIndex < context.Lines.Count; lineIndex++)
        {
            var elements = context.Lines[lineIndex];
            var runs = elements.Select(element => CreateRun(
                block,
                context,
                typeface,
                fallbackTypeface,
                lineIndex,
                element)).ToArray();
            var lineAdjustment = context.LineAdjustments.GetValueOrDefault(lineIndex);
            var defaultSize = lineAdjustment?.FontSizePx ?? block.Layout.FontSizePx;
            var lineHeight = Math.Max(1, defaultSize * 1.2f);
            if (runs.Length == 0)
            {
                cursorY += lineHeight;
                continue;
            }

            var ascent = runs.Max(run => -run.Metrics.Ascent);
            var descent = runs.Max(run => run.Metrics.Descent);
            var measuredTopInset = runs.Max(run => AddClipSafety(Math.Max(
                0,
                -ascent - run.TextBounds.Top + run.Paints.Expansion / 2)));
            var measuredBottomInset = runs.Max(run => AddClipSafety(Math.Max(
                0,
                run.TextBounds.Bottom - descent + run.Paints.Expansion / 2)));
            var lineTopInset = lineIndex == firstContentLine ? measuredTopInset : 0;
            var lineBottomInset = lineIndex == lastContentLine ? measuredBottomInset : 0;
            lineHeight = Math.Max(lineHeight, ascent + descent)
                + lineTopInset
                + lineBottomInset;
            var widths = runs.Select(run => run.Width).ToArray();
            var baseGap = block.Layout.LetterSpacingPx;
            var justifyLine = block.Layout.Alignment == DialogueTextAlignment.Justify
                && lineIndex < context.Lines.Count - 1;
            var visualLeftRun = block.Layout.ReadingDirection
                    == DialogueReadingDirection.LeftToRight
                ? runs[0]
                : runs[^1];
            var visualRightRun = block.Layout.ReadingDirection
                    == DialogueReadingDirection.LeftToRight
                ? runs[^1]
                : runs[0];
            var leftInset = AddClipSafety(Math.Max(
                0,
                -visualLeftRun.TextBounds.Left + visualLeftRun.Paints.Expansion / 2));
            var rightInset = AddClipSafety(Math.Max(
                0,
                visualRightRun.TextBounds.Right
                    - visualRightRun.Width
                    + visualRightRun.Paints.Expansion / 2));
            var availableWidth = Math.Max(0, width - leftInset - rightInset);
            var totalWidth = widths.Sum() + baseGap * Math.Max(0, runs.Length - 1);
            var gap = ResolveJustifiedGap(
                justifyLine,
                availableWidth,
                widths.Sum(),
                runs.Length,
                baseGap);
            if (justifyLine)
            {
                totalWidth = widths.Sum() + gap * Math.Max(0, runs.Length - 1);
            }

            var left = leftInset + ResolveHorizontalLeft(
                block.Layout.Alignment,
                block.Layout.ReadingDirection,
                availableWidth,
                totalWidth);
            var baseline = cursorY
                + lineTopInset
                + ascent
                + (lineAdjustment?.OffsetYPx ?? 0);
            if (block.Layout.ReadingDirection == DialogueReadingDirection.LeftToRight)
            {
                var cursorX = left;
                foreach (var run in runs)
                {
                    var x = cursorX
                        + (lineAdjustment?.OffsetXPx ?? 0)
                        + (run.CharacterAdjustment?.OffsetXPx ?? 0);
                    var y = baseline + (run.CharacterAdjustment?.OffsetYPx ?? 0);
                    DrawText(canvas, run.RenderedText, x, y, run.Paints);
                    result.Add(new GlyphPlacement(
                        run.Element.GlobalIndex,
                        lineIndex,
                        new SKRect(
                            x + run.TextBounds.Left - run.Paints.Expansion / 2,
                            y + run.TextBounds.Top - run.Paints.Expansion / 2,
                            x + run.TextBounds.Right + run.Paints.Expansion / 2,
                            y + run.TextBounds.Bottom + run.Paints.Expansion / 2),
                        run.Style.FontSize));
                    cursorX += run.Width + gap;
                }
            }
            else
            {
                var cursorX = left + totalWidth;
                foreach (var run in runs)
                {
                    cursorX -= run.Width;
                    var x = cursorX
                        + (lineAdjustment?.OffsetXPx ?? 0)
                        + (run.CharacterAdjustment?.OffsetXPx ?? 0);
                    var y = baseline + (run.CharacterAdjustment?.OffsetYPx ?? 0);
                    DrawText(canvas, run.RenderedText, x, y, run.Paints);
                    result.Add(new GlyphPlacement(
                        run.Element.GlobalIndex,
                        lineIndex,
                        new SKRect(
                            x + run.TextBounds.Left - run.Paints.Expansion / 2,
                            y + run.TextBounds.Top - run.Paints.Expansion / 2,
                            x + run.TextBounds.Right + run.Paints.Expansion / 2,
                            y + run.TextBounds.Bottom + run.Paints.Expansion / 2),
                        run.Style.FontSize));
                    cursorX -= gap;
                }
            }

            DisposeRuns(runs);
            cursorY += lineHeight;
        }

        return result;
    }

    private static List<GlyphPlacement> DrawVertical(
        SKCanvas canvas,
        TextBlockRenderData block,
        RenderContext context,
        TypefaceSelection typeface,
        TypefaceSelection? fallbackTypeface,
        int width,
        int height,
        float leftInset,
        float rightInset)
    {
        var result = new List<GlyphPlacement>();
        var columns = new List<VerticalColumn>(context.Lines.Count);
        for (var lineIndex = 0; lineIndex < context.Lines.Count; lineIndex++)
        {
            var runs = context.Lines[lineIndex].Select(element => CreateRun(
                block,
                context,
                typeface,
                fallbackTypeface,
                lineIndex,
                element)).ToArray();
            var advances = runs.Select(run =>
            {
                var contentAdvance = run.VerticalOrientation == VerticalGlyphOrientation.Rotated
                    ? run.Width
                    : run.Metrics.Descent - run.Metrics.Ascent;
                return Math.Max(run.Style.FontSize, contentAdvance);
            }).ToArray();
            var topInset = runs.Length == 0
                ? 0
                : ResolveVerticalTopInset(runs[0], advances[0]);
            var bottomInset = runs.Length == 0
                ? 0
                : ResolveVerticalBottomInset(runs[^1], advances[^1]);
            var baseGap = block.Layout.LetterSpacingPx;
            var justifyLine = block.Layout.Alignment == DialogueTextAlignment.Justify
                && lineIndex < context.Lines.Count - 1;
            var gap = ResolveJustifiedGap(
                justifyLine,
                Math.Max(0, height - topInset - bottomInset),
                advances.Sum(),
                runs.Length,
                baseGap);
            var totalHeight = topInset
                + advances.Sum()
                + gap * Math.Max(0, runs.Length - 1)
                + bottomInset;
            var columnWidth = runs.Length == 0
                ? block.Layout.FontSizePx
                : runs.Select((run, index) => ResolveVerticalColumnWidth(
                    run,
                    advances[index])).Max();
            columns.Add(new VerticalColumn(
                lineIndex,
                runs,
                advances,
                gap,
                totalHeight,
                topInset,
                Math.Max(1, columnWidth)));
        }

        var columnGap = Math.Max(1, block.Layout.FontSizePx * 0.2f);
        var columnGroupWidth = columns.Sum(column => column.Width)
            + columnGap * Math.Max(0, columns.Count - 1);
        var availableWidth = width - leftInset - rightInset;
        var groupLeft = leftInset + (availableWidth - columnGroupWidth) / 2;

        // ReadingDirection determines the order in which columns advance, not where the
        // complete column group is anchored inside its layout box. Center the aggregate group
        // on the cross axis, then walk it in the requested direction.
        var cursorX = block.Layout.ReadingDirection == DialogueReadingDirection.RightToLeft
            ? groupLeft + columnGroupWidth
            : groupLeft;
        foreach (var column in columns)
        {
            var lineAdjustment = context.LineAdjustments.GetValueOrDefault(column.LineIndex);
            var centerX = block.Layout.ReadingDirection == DialogueReadingDirection.RightToLeft
                ? cursorX - column.Width / 2
                : cursorX + column.Width / 2;
            centerX += lineAdjustment?.OffsetXPx ?? 0;
            var cursorY = ResolveVerticalTop(
                block.Layout.Alignment,
                height,
                column.TotalHeight)
                + column.TopInset
                + (lineAdjustment?.OffsetYPx ?? 0);
            for (var index = 0; index < column.Runs.Length; index++)
            {
                var run = column.Runs[index];
                var advance = column.Advances[index];
                var offsetX = run.CharacterAdjustment?.OffsetXPx ?? 0;
                var offsetY = run.CharacterAdjustment?.OffsetYPx ?? 0;
                var outlineInset = run.Paints.Expansion / 2;
                SKRect bounds;
                if (run.VerticalOrientation == VerticalGlyphOrientation.Rotated)
                {
                    var glyphCenterX = centerX + offsetX;
                    var glyphCenterY = cursorY + advance / 2 + offsetY;
                    var x = glyphCenterX
                        - (run.TextBounds.Left + run.TextBounds.Right) / 2;
                    var baseline = glyphCenterY
                        - (run.TextBounds.Top + run.TextBounds.Bottom) / 2;
                    DrawRotatedVerticalText(
                        canvas,
                        run.RenderedText,
                        x,
                        baseline,
                        glyphCenterX,
                        glyphCenterY,
                        run.Paints);
                    bounds = new SKRect(
                        glyphCenterX - run.TextBounds.Height / 2 - outlineInset,
                        glyphCenterY - run.TextBounds.Width / 2 - outlineInset,
                        glyphCenterX + run.TextBounds.Height / 2 + outlineInset,
                        glyphCenterY + run.TextBounds.Width / 2 + outlineInset);
                }
                else if (run.VerticalOrientation == VerticalGlyphOrientation.PresentationForm)
                {
                    var x = centerX - run.Width / 2 + offsetX;
                    var top = cursorY + offsetY;
                    var baseline = top - run.Metrics.Ascent;
                    DrawText(canvas, run.RenderedText, x, baseline, run.Paints);
                    bounds = new SKRect(
                        x + run.TextBounds.Left - outlineInset,
                        baseline + run.TextBounds.Top - outlineInset,
                        x + run.TextBounds.Right + outlineInset,
                        baseline + run.TextBounds.Bottom + outlineInset);
                }
                else if (run.VerticalOrientation == VerticalGlyphOrientation.TransformUpright)
                {
                    var placement = ResolveTransformUprightPlacement(run, advance);
                    var glyphCenterX = centerX + placement.CenterXOffset + offsetX;
                    var glyphCenterY = cursorY + placement.CenterY + offsetY;
                    var x = glyphCenterX
                        - (run.TextBounds.Left + run.TextBounds.Right) / 2;
                    var baseline = glyphCenterY
                        - (run.TextBounds.Top + run.TextBounds.Bottom) / 2;
                    DrawText(canvas, run.RenderedText, x, baseline, run.Paints);
                    bounds = new SKRect(
                        glyphCenterX - run.TextBounds.Width / 2 - outlineInset,
                        glyphCenterY - run.TextBounds.Height / 2 - outlineInset,
                        glyphCenterX + run.TextBounds.Width / 2 + outlineInset,
                        glyphCenterY + run.TextBounds.Height / 2 + outlineInset);
                }
                else
                {
                    var x = centerX
                        - (run.TextBounds.Left + run.TextBounds.Right) / 2
                        + offsetX;
                    var top = cursorY + offsetY;
                    var baseline = top - run.Metrics.Ascent;
                    DrawText(canvas, run.RenderedText, x, baseline, run.Paints);
                    bounds = new SKRect(
                        x + run.TextBounds.Left - outlineInset,
                        baseline + run.TextBounds.Top - outlineInset,
                        x + run.TextBounds.Right + outlineInset,
                        baseline + run.TextBounds.Bottom + outlineInset);
                }

                result.Add(new GlyphPlacement(
                    run.Element.GlobalIndex,
                    column.LineIndex,
                    bounds,
                    run.Style.FontSize));
                cursorY += advance + column.Gap;
            }

            DisposeRuns(column.Runs);
            if (block.Layout.ReadingDirection == DialogueReadingDirection.RightToLeft)
            {
                cursorX -= column.Width + columnGap;
            }
            else
            {
                cursorX += column.Width + columnGap;
            }
        }

        return result;
    }

    private static void DrawRuby(
        SKCanvas canvas,
        TextBlockRenderData block,
        RenderContext context,
        TypefaceSelection typeface,
        TypefaceSelection? fallbackTypeface,
        IReadOnlyList<GlyphPlacement> placements)
    {
        var annotations = block.Layout.Advanced?.RubyAnnotations ?? [];
        foreach (var ruby in annotations)
        {
            var baseGlyphs = placements
                .Where(item => item.CharacterIndex >= ruby.BaseCharacterIndex
                    && item.CharacterIndex < ruby.BaseCharacterIndex + ruby.BaseCharacterLength)
                .ToArray();
            if (baseGlyphs.Length != ruby.BaseCharacterLength)
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' ruby range could not be mapped to rendered glyphs.");
            }

            var bounds = baseGlyphs.Select(item => item.Bounds).Aggregate(Union);
            var baseSize = baseGlyphs.Average(item => item.FontSize);
            var style = new GlyphStyle(
                ruby.FontSizePx ?? Math.Max(1, baseSize * 0.5f),
                ParseColor(ruby.Color ?? block.Layout.Color));
            if (block.Layout.Orientation == DialogueTextOrientation.Horizontal)
            {
                DrawHorizontalRuby(canvas, block, ruby, SelectTypefaceForText(typeface, fallbackTypeface, ruby.Text, style.FontSize), bounds, style);
            }
            else
            {
                DrawVerticalRuby(canvas, block, ruby, typeface, fallbackTypeface, bounds, style);
            }
        }
    }

    private static void DrawHorizontalRuby(
        SKCanvas canvas,
        TextBlockRenderData block,
        DialogueRubyAnnotation ruby,
        TypefaceSelection typeface,
        SKRect bounds,
        GlyphStyle style)
    {
        using var paints = CreatePaints(typeface, style, block.Layout.Stroke);
        paints.Font.GetFontMetrics(out var metrics);
        var rubyWidth = paints.Font.MeasureText(ruby.Text, paints.Fill);
        var x = bounds.MidX - rubyWidth / 2 + ruby.OffsetXPx;
        var before = ruby.Placement is DialogueRubyPlacement.Auto or DialogueRubyPlacement.Before;
        var baseline = before
            ? bounds.Top - 1 - metrics.Descent
            : bounds.Bottom + 1 - metrics.Ascent;
        DrawText(canvas, ruby.Text, x, baseline + ruby.OffsetYPx, paints);
    }

    private static void DrawVerticalRuby(
        SKCanvas canvas,
        TextBlockRenderData block,
        DialogueRubyAnnotation ruby,
        TypefaceSelection typeface,
        TypefaceSelection? fallbackTypeface,
        SKRect bounds,
        GlyphStyle style)
    {
        var elements = EnumerateTextElements(ruby.Text).ToArray();
        var totalHeight = elements.Length * style.FontSize;
        var top = bounds.MidY - totalHeight / 2 + ruby.OffsetYPx;
        var before = ruby.Placement is DialogueRubyPlacement.Auto or DialogueRubyPlacement.Before;
        var beforeIsRight = block.Layout.ReadingDirection == DialogueReadingDirection.RightToLeft;
        var placeRight = before ? beforeIsRight : !beforeIsRight;
        var centerX = (placeRight
            ? bounds.Right + style.FontSize / 2 + 1
            : bounds.Left - style.FontSize / 2 - 1)
            + ruby.OffsetXPx;
        using var paints = CreatePaints(typeface, style, block.Layout.Stroke);
        paints.Font.GetFontMetrics(out var metrics);
        foreach (var element in elements)
        {
            var activeTypeface = SelectTypefaceForText(typeface, fallbackTypeface, element, style.FontSize);
            using var activePaints = ReferenceEquals(activeTypeface, typeface)
                ? null
                : CreatePaints(activeTypeface, style, block.Layout.Stroke);
            var glyphPaints = activePaints ?? paints;
            var glyphWidth = glyphPaints.Font.MeasureText(element, glyphPaints.Fill);
            DrawText(
                canvas,
                element,
                centerX - glyphWidth / 2,
                top - metrics.Ascent,
                glyphPaints);
            top += style.FontSize;
        }
    }

    private static GlyphRun CreateRun(
        TextBlockRenderData block,
        RenderContext context,
        TypefaceSelection typeface,
        TypefaceSelection? fallbackTypeface,
        int lineIndex,
        TextElement element)
    {
        var line = context.LineAdjustments.GetValueOrDefault(lineIndex);
        var character = context.CharacterAdjustments.GetValueOrDefault(element.GlobalIndex);
        var fontSize = character?.FontSizePx ?? line?.FontSizePx ?? block.Layout.FontSizePx;
        var color = character?.Color ?? line?.Color ?? block.Layout.Color;
        if (!float.IsFinite(fontSize) || fontSize <= 0)
        {
            throw new RenderingException(
                $"Block '{block.BlockId}' has an invalid font size at character "
                + $"{element.GlobalIndex}.");
        }

        var style = new GlyphStyle(fontSize, ParseColor(color));
        var verticalGlyph = block.Layout.Orientation == DialogueTextOrientation.Vertical
            ? UnicodeVerticalOrientation.ResolveGlyph(
                element.Value,
                candidate => TypefaceContainsText(typeface, candidate, fontSize)
                    || (fallbackTypeface is not null && TypefaceContainsText(fallbackTypeface, candidate, fontSize)))
            : new VerticalGlyphResolution(
                element.Value,
                UnicodeVerticalOrientation.Classify(element.Value));
        var activeTypeface = SelectTypefaceForText(typeface, fallbackTypeface, verticalGlyph.Text, fontSize);
        var paints = CreatePaints(activeTypeface, style, block.Layout.Stroke);
        paints.Font.GetFontMetrics(out var metrics);
        var width = paints.Font.MeasureText(verticalGlyph.Text, out var textBounds, paints.Fill);
        return new GlyphRun(
            element,
            character,
            style,
            paints,
            verticalGlyph.Text,
            width,
            textBounds,
            metrics,
            verticalGlyph.Orientation);
    }

    private static GlyphPaints CreatePaints(
        TypefaceSelection typeface,
        GlyphStyle style,
        DialogueTextStroke? stroke)
    {
        var syntheticWeight = GetSyntheticWeightPx(typeface.WeightDelta, style.FontSize);
        var syntheticErode = GetSyntheticErodePx(typeface.WeightDelta, style.FontSize);
        var useAutomaticContrastStroke = IsNearWhite(style.Color)
            && !HasEffectiveContrastStroke(stroke, style.Color);
        var outlineWidth = useAutomaticContrastStroke
            ? Math.Clamp(style.FontSize * 0.04f, 1, 2)
            : stroke?.WidthPx ?? 0;
        var outlineColor = useAutomaticContrastStroke
            ? SKColors.Black
            : stroke is null
                ? style.Color
                : ParseColor(stroke.Color);
        var roundOutline = useAutomaticContrastStroke || stroke?.RoundCorners == true;
        var expansion = outlineWidth * 2 + syntheticWeight;
        SKImageFilter? erodeFilter = null;
        if (syntheticErode > 0)
        {
            erodeFilter = SKImageFilter.CreateErode(
                syntheticErode,
                syntheticErode);
        }

        var font = new SKFont(typeface.Typeface, style.FontSize)
        {
            SkewX = typeface.SyntheticItalic ? SyntheticItalicSkew : 0,
            Subpixel = true
        };
        var fill = new SKPaint
        {
            Color = style.Color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            ImageFilter = erodeFilter
        };
        SKPaint? outline = null;
        if (outlineWidth > 0)
        {
            outline = new SKPaint
            {
                Color = outlineColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = outlineWidth * 2 + syntheticWeight,
                StrokeJoin = roundOutline
                    ? SKStrokeJoin.Round
                    : SKStrokeJoin.Miter,
                StrokeCap = roundOutline
                    ? SKStrokeCap.Round
                    : SKStrokeCap.Butt,
                StrokeMiter = 4
            };
        }

        SKPaint? weight = null;
        if (syntheticWeight > 0)
        {
            weight = new SKPaint
            {
                Color = style.Color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = syntheticWeight,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round
            };
        }

        return new GlyphPaints(font, fill, outline, weight, erodeFilter, expansion);
    }

    private static bool IsNearWhite(SKColor color) =>
        color.Alpha > 0
        && color.Red >= 224
        && color.Green >= 224
        && color.Blue >= 224;

    private static bool HasEffectiveContrastStroke(
        DialogueTextStroke? stroke,
        SKColor fillColor)
    {
        if (stroke is null || stroke.WidthPx < 0.5f)
        {
            return false;
        }

        var strokeColor = ParseColor(stroke.Color);
        return strokeColor.Alpha > 0
            && ContrastRatio(
                CompositeOverWhite(fillColor),
                CompositeOverWhite(strokeColor)) >= 2;
    }

    private static SKColor CompositeOverWhite(SKColor color)
    {
        var alpha = color.Alpha / 255d;
        return new SKColor(
            CompositeColorChannel(color.Red, alpha),
            CompositeColorChannel(color.Green, alpha),
            CompositeColorChannel(color.Blue, alpha));
    }

    private static byte CompositeColorChannel(byte value, double alpha) =>
        (byte)Math.Round(value * alpha + byte.MaxValue * (1 - alpha));

    private static double ContrastRatio(SKColor left, SKColor right)
    {
        var leftLuminance = RelativeLuminance(left);
        var rightLuminance = RelativeLuminance(right);
        return (Math.Max(leftLuminance, rightLuminance) + 0.05)
            / (Math.Min(leftLuminance, rightLuminance) + 0.05);
    }

    private static double RelativeLuminance(SKColor color) =>
        0.2126 * LinearizeColorChannel(color.Red)
        + 0.7152 * LinearizeColorChannel(color.Green)
        + 0.0722 * LinearizeColorChannel(color.Blue);

    private static double LinearizeColorChannel(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private TypefaceSelection ResolveTypeface(string fontId, int fontWeight, bool italic)
    {
        var key = new FontKey(fontId.Trim().ToUpperInvariant(), fontWeight, italic);
        if (typefaces.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var entry = fontLibrary.ReadCatalog().Fonts.SingleOrDefault(candidate =>
            string.Equals(candidate.FontId, key.FontId, StringComparison.OrdinalIgnoreCase))
            ?? throw new RenderingException(
                $"Font '{fontId}' does not exist in the managed font library.");
        var selection = SelectAsset(entry, fontWeight, italic);
        var absolutePath = fontLibrary.ResolveManagedPath(selection.Asset.RelativePath);
        if (!File.Exists(absolutePath))
        {
            throw new RenderingException(
                $"Managed font file for '{entry.FontId}' does not exist: "
                + selection.Asset.RelativePath);
        }

        var loaded = SKTypeface.FromFile(absolutePath, selection.Asset.FaceIndex)
            ?? throw new RenderingException(
                $"SkiaSharp could not load managed font '{entry.FontId}' from "
                + $"'{selection.Asset.RelativePath}'.");
        var resolved = new TypefaceSelection(
            loaded,
            selection.NativeWeight,
            selection.WeightDelta,
            selection.SyntheticItalic);
        typefaces.Add(key, resolved);
        return resolved;
    }

    private TypefaceSelection? ResolveFallbackTypeface(string primaryFontId, int fontWeight, bool italic)
    {
        if (fallbackFontId is null
            || string.Equals(primaryFontId, fallbackFontId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return ResolveTypeface(fallbackFontId, fontWeight, italic);
        }
        catch (RenderingException)
        {
            return null;
        }
    }

    private static TypefaceSelection SelectTypefaceForText(
        TypefaceSelection primary,
        TypefaceSelection? fallback,
        string text,
        float fontSize)
    {
        return !TypefaceContainsText(primary, text, fontSize)
            && fallback is not null
            && TypefaceContainsText(fallback, text, fontSize)
                ? fallback
                : primary;
    }

    private static bool TypefaceContainsText(TypefaceSelection typeface, string text, float fontSize)
    {
        using var font = new SKFont(typeface.Typeface, Math.Max(1, fontSize));
        return font.ContainsGlyphs(text);
    }

    private static AssetSelection SelectAsset(FontEntry entry, int fontWeight, bool italic)
    {
        if (fontWeight >= 600 && entry.Bold is not null)
        {
            return new AssetSelection(
                entry.Bold,
                entry.Bold.DetectedWeight ?? 700,
                fontWeight - (entry.Bold.DetectedWeight ?? 700),
                italic && entry.Italic is null);
        }

        if (italic && entry.Italic is not null)
        {
            return new AssetSelection(
                entry.Italic,
                entry.Italic.DetectedWeight ?? 400,
                fontWeight - (entry.Italic.DetectedWeight ?? 400),
                false);
        }

        var regular = entry.Regular ?? throw new RenderingException(
            $"Managed font '{entry.FontId}' has no regular face.");
        return new AssetSelection(
            regular,
            regular.DetectedWeight ?? 400,
            fontWeight - (regular.DetectedWeight ?? 400),
            italic);
    }

    private static float ResolveVerticalColumnWidth(GlyphRun run, float advance)
    {
        var outlineInset = run.Paints.Expansion / 2;
        if (run.VerticalOrientation == VerticalGlyphOrientation.PresentationForm)
        {
            var left = -run.Width / 2 + run.TextBounds.Left - outlineInset;
            var right = -run.Width / 2 + run.TextBounds.Right + outlineInset;
            return Math.Max(
                run.Style.FontSize,
                Math.Max(run.Width, 2 * Math.Max(Math.Abs(left), Math.Abs(right))));
        }

        if (run.VerticalOrientation == VerticalGlyphOrientation.TransformUpright)
        {
            var placement = ResolveTransformUprightPlacement(run, advance);
            var halfInk = run.TextBounds.Width / 2
                + outlineInset
                + ClipSafetyInset;
            return Math.Max(
                Math.Max(run.Style.FontSize, run.Width),
                2 * (Math.Abs(placement.CenterXOffset) + halfInk));
        }

        var inkWidth = run.VerticalOrientation == VerticalGlyphOrientation.Rotated
            ? run.TextBounds.Height
            : run.TextBounds.Width;
        return Math.Max(run.Style.FontSize, inkWidth + run.Paints.Expansion);
    }

    private static float ResolveVerticalTopInset(GlyphRun run, float advance)
    {
        if (run.VerticalOrientation == VerticalGlyphOrientation.TransformUpright)
        {
            var placement = ResolveTransformUprightPlacement(run, advance);
            var top = placement.CenterY
                - run.TextBounds.Height / 2
                - run.Paints.Expansion / 2;
            return AddClipSafety(Math.Max(0, -top));
        }

        var inkHeight = run.VerticalOrientation switch
        {
            VerticalGlyphOrientation.Rotated => run.TextBounds.Width,
            _ => 0
        };
        if (inkHeight > 0)
        {
            return AddClipSafety(Math.Max(
                0,
                (inkHeight + run.Paints.Expansion - advance) / 2));
        }

        return AddClipSafety(Math.Max(
            0,
            run.Metrics.Ascent - run.TextBounds.Top + run.Paints.Expansion / 2));
    }

    private static float ResolveVerticalBottomInset(GlyphRun run, float advance)
    {
        if (run.VerticalOrientation == VerticalGlyphOrientation.TransformUpright)
        {
            var placement = ResolveTransformUprightPlacement(run, advance);
            var bottom = placement.CenterY
                + run.TextBounds.Height / 2
                + run.Paints.Expansion / 2;
            return AddClipSafety(Math.Max(0, bottom - advance));
        }

        var inkHeight = run.VerticalOrientation switch
        {
            VerticalGlyphOrientation.Rotated => run.TextBounds.Width,
            _ => 0
        };
        if (inkHeight > 0)
        {
            return AddClipSafety(Math.Max(
                0,
                (inkHeight + run.Paints.Expansion - advance) / 2));
        }

        return AddClipSafety(Math.Max(
            0,
            -run.Metrics.Ascent
                + run.TextBounds.Bottom
                + run.Paints.Expansion / 2
                - advance));
    }

    private static float AddClipSafety(float inset) =>
        inset > 0 ? inset + ClipSafetyInset : 0;

    private static VerticalCellPlacement ResolveTransformUprightPlacement(
        GlyphRun run,
        float advance)
    {
        var cellWidth = Math.Max(run.Style.FontSize, run.Width);
        var metricsHeight = run.Metrics.Descent - run.Metrics.Ascent;
        var sourceCenterX = (cellWidth - run.Width) / 2
            + (run.TextBounds.Left + run.TextBounds.Right) / 2;
        var sourceCenterY = (advance - metricsHeight) / 2
            - run.Metrics.Ascent
            + (run.TextBounds.Top + run.TextBounds.Bottom) / 2;
        var compact = run.TextBounds.Width <= cellWidth * CompactTuGlyphRatio
            && run.TextBounds.Height <= advance * CompactTuGlyphRatio;
        var targetX = compact
            ? Math.Max(cellWidth - sourceCenterX, cellWidth * VerticalTuRightTarget)
            : cellWidth / 2;
        var targetY = compact
            ? Math.Min(advance - sourceCenterY, advance * VerticalTuTopTarget)
            : advance / 2;
        var outlineInset = run.Paints.Expansion / 2;
        targetX = ClampInkCenter(
            targetX,
            run.TextBounds.Width,
            cellWidth,
            outlineInset);
        targetY = ClampInkCenter(
            targetY,
            run.TextBounds.Height,
            advance,
            outlineInset);
        return new VerticalCellPlacement(targetX - cellWidth / 2, targetY);
    }

    private static float ClampInkCenter(
        float desired,
        float inkSize,
        float cellSize,
        float outlineInset)
    {
        var margin = inkSize / 2 + outlineInset + ClipSafetyInset;
        return margin * 2 >= cellSize
            ? cellSize / 2
            : Math.Clamp(desired, margin, cellSize - margin);
    }

    private static float ResolveHorizontalLeft(
        DialogueTextAlignment alignment,
        DialogueReadingDirection direction,
        float available,
        float occupied)
    {
        return alignment switch
        {
            DialogueTextAlignment.Center => (available - occupied) / 2,
            DialogueTextAlignment.End
                when direction == DialogueReadingDirection.LeftToRight => available - occupied,
            DialogueTextAlignment.End => 0,
            DialogueTextAlignment.Start
                when direction == DialogueReadingDirection.RightToLeft => available - occupied,
            _ => 0
        };
    }

    private static float ResolveVerticalTop(
        DialogueTextAlignment alignment,
        float available,
        float occupied)
    {
        return alignment switch
        {
            DialogueTextAlignment.Center => (available - occupied) / 2,
            DialogueTextAlignment.End => available - occupied,
            _ => 0
        };
    }

    private static float ResolveJustifiedGap(
        bool justify,
        float available,
        float contentSize,
        int count,
        float baseGap)
    {
        if (!justify || count <= 1)
        {
            return baseGap;
        }

        return Math.Max(baseGap, (available - contentSize) / (count - 1));
    }

    private static SKColor ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] != '#'
            || value.Length is not (7 or 9)
            || !uint.TryParse(
                value.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var packed))
        {
            throw new RenderingException(
                $"Color '{value}' must use #RRGGBB or #RRGGBBAA format.");
        }

        if (value.Length == 7)
        {
            return new SKColor(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed,
                byte.MaxValue);
        }

        return new SKColor(
            (byte)(packed >> 24),
            (byte)(packed >> 16),
            (byte)(packed >> 8),
            (byte)packed);
    }

    private static void ValidateBlock(TextBlockRenderData block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(block.BlockId);
        ArgumentNullException.ThrowIfNull(block.Text);
        ArgumentNullException.ThrowIfNull(block.Layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(block.Layout.FontId);
        var box = block.Layout.TextBox;
        if (box.X1 <= box.X0 || box.Y1 <= box.Y0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' must have a positive layout-box size.");
        }

        if (!float.IsFinite(block.Layout.FontSizePx) || block.Layout.FontSizePx <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' must have a positive finite font size.");
        }

        if (!float.IsFinite(block.Layout.LetterSpacingPx))
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' must have finite letter spacing.");
        }

        if (block.Layout.EffectiveFontWeight is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' must have a font weight between 1 and 1000.");
        }

        if (block.Layout.Rotation is not null
            && (!float.IsFinite(block.Layout.Rotation.CenterXPx)
                || !float.IsFinite(block.Layout.Rotation.CenterYPx)
                || !float.IsFinite(block.Layout.Rotation.AngleDeg)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' rotation values must be finite.");
        }

        if (block.Layout.Stroke is not null
            && (block.Layout.Stroke.WidthPx < 0
                || !float.IsFinite(block.Layout.Stroke.WidthPx)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(block),
                $"Block '{block.BlockId}' stroke width must be non-negative.");
        }

        if (block.Layout.Stroke is not null)
        {
            _ = ParseColor(block.Layout.Stroke.Color);
        }
    }

    private static void ValidateRuby(TextBlockRenderData block, int characterCount)
    {
        var ranges = new HashSet<(int Start, int Length)>();
        foreach (var ruby in block.Layout.Advanced?.RubyAnnotations ?? [])
        {
            if (ruby.BaseCharacterIndex < 0
                || ruby.BaseCharacterLength <= 0
                || ruby.BaseCharacterIndex + ruby.BaseCharacterLength > characterCount)
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' has an out-of-range ruby annotation.");
            }

            if (string.IsNullOrEmpty(ruby.Text) || ruby.Text.Contains('\n'))
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' ruby text must be non-empty and single-line.");
            }

            if (!ranges.Add((ruby.BaseCharacterIndex, ruby.BaseCharacterLength)))
            {
                throw new RenderingException(
                    $"Block '{block.BlockId}' contains a duplicate ruby base range.");
            }
        }
    }

    private static RubyInsets MeasureRubyInsets(TextBlockRenderData block)
    {
        var annotations = block.Layout.Advanced?.RubyAnnotations ?? [];
        if (annotations.Count == 0)
        {
            return new RubyInsets(0, 0, 0, 0);
        }

        var largestBaseSize = new[] { block.Layout.FontSizePx }
            .Concat(block.Layout.Advanced?.LineAdjustments
                .Where(item => item.FontSizePx is not null)
                .Select(item => item.FontSizePx!.Value) ?? [])
            .Concat(block.Layout.Advanced?.CharacterAdjustments
                .Where(item => item.FontSizePx is not null)
                .Select(item => item.FontSizePx!.Value) ?? [])
            .Max();
        var top = 0f;
        var bottom = 0f;
        var left = 0f;
        var right = 0f;
        foreach (var ruby in annotations)
        {
            var size = ruby.FontSizePx ?? Math.Max(1, largestBaseSize * 0.5f);
            var before = ruby.Placement is DialogueRubyPlacement.Auto
                or DialogueRubyPlacement.Before;
            if (block.Layout.Orientation == DialogueTextOrientation.Horizontal)
            {
                var verticalInset = size * 1.35f + 2;
                if (before)
                {
                    top = Math.Max(top, verticalInset);
                }
                else
                {
                    bottom = Math.Max(bottom, verticalInset);
                }

                continue;
            }

            var horizontalInset = size + 2;
            var beforeIsRight = block.Layout.ReadingDirection
                == DialogueReadingDirection.RightToLeft;
            var placeRight = before ? beforeIsRight : !beforeIsRight;
            if (placeRight)
            {
                right = Math.Max(right, horizontalInset);
            }
            else
            {
                left = Math.Max(left, horizontalInset);
            }
        }

        return new RubyInsets(top, right, bottom, left);
    }

    private static Dictionary<int, TValue> ToUniqueDictionary<TValue>(
        IEnumerable<TValue> values,
        Func<TValue, int> keySelector,
        string blockId,
        string description)
    {
        var result = new Dictionary<int, TValue>();
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                throw new RenderingException(
                    $"Block '{blockId}' contains duplicate {description} index {key}.");
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateTextElements(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static SKRect Union(SKRect left, SKRect right)
    {
        return new SKRect(
            Math.Min(left.Left, right.Left),
            Math.Min(left.Top, right.Top),
            Math.Max(left.Right, right.Right),
            Math.Max(left.Bottom, right.Bottom));
    }

    private static void DisposeRuns(IEnumerable<GlyphRun> runs)
    {
        foreach (var run in runs)
        {
            run.Paints.Dispose();
        }
    }

    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        GlyphPaints paints)
    {
        if (paints.Outline is not null)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, paints.Font, paints.Outline);
        }

        if (paints.Weight is not null)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, paints.Font, paints.Weight);
        }

        canvas.DrawText(text, x, baseline, SKTextAlign.Left, paints.Font, paints.Fill);
    }

    private static void DrawRotatedVerticalText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        float centerX,
        float centerY,
        GlyphPaints paints)
    {
        canvas.Save();
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees(90);
        canvas.Translate(-centerX, -centerY);
        DrawText(canvas, text, x, baseline, paints);
        canvas.Restore();
    }

    private sealed record RenderContext(
        IReadOnlyList<IReadOnlyList<TextElement>> Lines,
        IReadOnlyDictionary<int, DialogueLineAdjustment> LineAdjustments,
        IReadOnlyDictionary<int, DialogueCharacterAdjustment> CharacterAdjustments);

    private sealed record TextElement(int GlobalIndex, string Value);

    private sealed record GlyphStyle(float FontSize, SKColor Color);

    private sealed record GlyphRun(
        TextElement Element,
        DialogueCharacterAdjustment? CharacterAdjustment,
        GlyphStyle Style,
        GlyphPaints Paints,
        string RenderedText,
        float Width,
        SKRect TextBounds,
        SKFontMetrics Metrics,
        VerticalGlyphOrientation VerticalOrientation);

    private sealed class GlyphPaints(
        SKFont font,
        SKPaint fill,
        SKPaint? outline,
        SKPaint? weight,
        SKImageFilter? erodeFilter,
        float expansion) : IDisposable
    {
        public SKFont Font { get; } = font;

        public SKPaint Fill { get; } = fill;

        public SKPaint? Outline { get; } = outline;

        public SKPaint? Weight { get; } = weight;

        public SKImageFilter? ErodeFilter { get; } = erodeFilter;

        public float Expansion { get; } = expansion;

        public void Dispose()
        {
            Outline?.Dispose();
            Weight?.Dispose();
            Fill.Dispose();
            Font.Dispose();
            ErodeFilter?.Dispose();
        }
    }

    private sealed record GlyphPlacement(
        int CharacterIndex,
        int LineIndex,
        SKRect Bounds,
        float FontSize);

    private sealed record VerticalColumn(
        int LineIndex,
        GlyphRun[] Runs,
        float[] Advances,
        float Gap,
        float TotalHeight,
        float TopInset,
        float Width);

    private readonly record struct VerticalCellPlacement(
        float CenterXOffset,
        float CenterY);

    private sealed record FontKey(string FontId, int FontWeight, bool Italic);

    private sealed record AssetSelection(
        FontFaceAsset Asset,
        int NativeWeight,
        int WeightDelta,
        bool SyntheticItalic);

    private sealed record TypefaceSelection(
        SKTypeface Typeface,
        int NativeWeight,
        int WeightDelta,
        bool SyntheticItalic);

    private static float GetSyntheticWeightPx(int weightDelta, float fontSize)
    {
        return Math.Max(0, weightDelta) / 500f * fontSize * 0.08f;
    }

    private static float GetSyntheticErodePx(int weightDelta, float fontSize)
    {
        return Math.Max(0, -weightDelta) / 500f * fontSize * 0.08f;
    }

    private sealed record RubyInsets(float Top, float Right, float Bottom, float Left);
}
