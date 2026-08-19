using System.Globalization;
using System.Text;
using MangaTranslator.Api.Settings;
using MangaTranslator.Dialogue;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Rendering;
using SkiaSharp;

namespace MangaTranslator.Api.Projects;

public static class ProjectResultRenderer
{
    public static RenderedTextBlock RenderClip(
        ProjectResultService.ProjectLayoutSegment segment,
        FontCatalogService fonts,
        string? defaultFontId)
    {
        var box = segment.Layout.TextBox;
        var width = Math.Max(1, box.X1 - box.X0);
        var height = Math.Max(1, box.Y1 - box.Y0);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-box.X0, -box.Y0);
        var clipSegment = segment with
        {
            Layout = segment.Layout with { Rotation = null }
        };
        foreach (var block in ExpandBlocks(clipSegment))
        {
            using var typeface = ResolveTypeface(
                fonts,
                defaultFontId,
                block.Layout.FontId,
                block.Layout.FontWeight,
                block.Layout.Italic);
            using var font = new SKFont(typeface, Math.Max(1, block.Layout.FontSizePx));
            font.Embolden = block.Layout.FontWeight >= 600;
            font.SkewX = block.Layout.Italic && typeface.FontStyle.Slant != SKFontStyleSlant.Italic ? -0.2f : 0;
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Color = ParseColor(block.Layout.Color),
                Style = SKPaintStyle.Fill
            };
            using var stroke = CreateStroke(block.Layout.Stroke);
            DrawBlock(canvas, block, font, fill, stroke);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode the rendered text clip.");
        return new RenderedTextBlock(segment.SegmentId, width, height, encoded.ToArray());
    }

    internal static byte[] EncodeBackground(PageBackgroundData background)
    {
        using var bitmap = SKBitmap.Decode(background.Data.ToArray())
            ?? throw new InvalidOperationException("Unable to decode the page background.");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode the page background.");
        return encoded.ToArray();
    }

    internal static byte[] CreateTransparentPage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        return EncodeBitmap(bitmap, "transparent page");
    }

    internal static byte[] RenderLettering(
        ProjectPageSnapshot page,
        IReadOnlyList<ProjectResultService.ProjectLayoutSegment> segments,
        FontCatalogService fonts,
        string? defaultFontId)
    {
        using var output = new SKBitmap(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        output.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(output);
        DrawSegments(canvas, segments, fonts, defaultFontId);
        return EncodeBitmap(output, "lettering layer");
    }

    internal static byte[] Render(
        ProjectPageSnapshot page,
        PageBackgroundData background,
        IReadOnlyList<ProjectResultService.ProjectLayoutSegment> segments,
        FontCatalogService fonts,
        string? defaultFontId)
    {
        using var backgroundBitmap = SKBitmap.Decode(background.Data.ToArray())
            ?? throw new InvalidOperationException("Unable to decode the page background.");
        if (backgroundBitmap.Width != page.Width || backgroundBitmap.Height != page.Height)
        {
            throw new InvalidOperationException($"Page background dimensions do not match page {page.PageId}.");
        }

        using var output = backgroundBitmap.Copy()
            ?? throw new InvalidOperationException("Unable to copy the page background.");
        using var canvas = new SKCanvas(output);
        DrawSegments(canvas, segments, fonts, defaultFontId);

        return EncodeBitmap(output, "rendered result");
    }

    private static void DrawSegments(
        SKCanvas canvas,
        IReadOnlyList<ProjectResultService.ProjectLayoutSegment> segments,
        FontCatalogService fonts,
        string? defaultFontId)
    {
        foreach (var segment in segments.Where(item => item.IsRenderable && !string.IsNullOrWhiteSpace(item.TranslatedText)))
        {
            foreach (var block in ExpandBlocks(segment))
            {
                using var typeface = ResolveTypeface(fonts, defaultFontId, block.Layout.FontId, block.Layout.FontWeight, block.Layout.Italic);
                using var font = new SKFont(typeface, Math.Max(1, block.Layout.FontSizePx));
                font.Embolden = block.Layout.FontWeight >= 600;
                font.SkewX = block.Layout.Italic && typeface.FontStyle.Slant != SKFontStyleSlant.Italic ? -0.2f : 0;
                using var fill = new SKPaint { IsAntialias = true, Color = ParseColor(block.Layout.Color), Style = SKPaintStyle.Fill };
                using var stroke = CreateStroke(block.Layout.Stroke);
                DrawBlock(canvas, block, font, fill, stroke);
            }
        }
    }

    private static byte[] EncodeBitmap(SKBitmap output, string description)
    {
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException($"Unable to encode the {description}.");
        return encoded.ToArray();
    }

    private static SKTypeface ResolveTypeface(
        FontCatalogService fonts,
        string? defaultFontId,
        string fontId,
        int fontWeight,
        bool italic)
    {
        var resolved = fonts.Resolve(fontId, defaultFontId, fontWeight, italic);
        return SKTypeface.FromFile(resolved.FilePath, resolved.FaceIndex)
            ?? throw new InvalidOperationException($"Unable to load font '{fontId}'.");
    }

    private static IReadOnlyList<RenderBlock> ExpandBlocks(ProjectResultService.ProjectLayoutSegment segment)
    {
        var groups = segment.Layout.Advanced?.PlacementGroups;
        var normalizedRotation = DialogueBoxRotationCoordinates
            .NormalizeLegacyPageRelativeCenter(
                segment.Layout.Rotation,
                segment.Layout.TextBox);
        if (groups is null || groups.Count == 0)
        {
            return
            [
                new RenderBlock(
                    segment.TranslatedText,
                    segment.Layout with { Rotation = normalizedRotation })
            ];
        }

        var lines = NormalizeLines(segment.TranslatedText);
        var result = new List<RenderBlock>(groups.Count);
        foreach (var group in groups)
        {
            var text = string.Join('\n', group.LineIndices
                .Where(index => index >= 0 && index < lines.Length)
                .Select(index => lines[index]));
            if (text.Length == 0) continue;
            var rotation = normalizedRotation is null
                ? null
                : normalizedRotation with
                {
                    CenterXPx = normalizedRotation.CenterXPx - (group.TextBox.X0 - segment.Layout.TextBox.X0),
                    CenterYPx = normalizedRotation.CenterYPx - (group.TextBox.Y0 - segment.Layout.TextBox.Y0)
                };
            result.Add(new RenderBlock(text, segment.Layout with { TextBox = group.TextBox, Rotation = rotation, Advanced = null }));
        }
        return result;
    }

    private static void DrawBlock(SKCanvas canvas, RenderBlock block, SKFont font, SKPaint fill, SKPaint? stroke)
    {
        var box = block.Layout.TextBox;
        var rotation = block.Layout.Rotation;
        canvas.Save();
        if (rotation is not null && rotation.AngleDeg != 0)
        {
            var center = new SKPoint(box.X0 + rotation.CenterXPx, box.Y0 + rotation.CenterYPx);
            canvas.Translate(center.X, center.Y);
            canvas.RotateDegrees(rotation.AngleDeg);
            canvas.Translate(-center.X, -center.Y);
        }

        if (block.Layout.Orientation == DialogueTextOrientation.Vertical)
        {
            DrawVertical(canvas, block, font, fill, stroke);
        }
        else
        {
            DrawHorizontal(canvas, block, font, fill, stroke);
        }
        canvas.Restore();
    }

    private static void DrawHorizontal(SKCanvas canvas, RenderBlock block, SKFont font, SKPaint fill, SKPaint? stroke)
    {
        var box = block.Layout.TextBox;
        var lines = NormalizeLines(block.Text);
        font.GetFontMetrics(out var metrics);
        var lineHeight = Math.Max(font.Size * 1.16f, metrics.Descent - metrics.Ascent);
        var totalHeight = lineHeight * lines.Length;
        var y = box.Y0 + (box.Y1 - box.Y0 - totalHeight) / 2f - metrics.Ascent;
        var globalCharacterIndex = 0;
        foreach (var (line, lineIndex) in lines.Select((value, index) => (value, index)))
        {
            var lineAdjustment = block.Layout.Advanced?.LineAdjustments.FirstOrDefault(item => item.LineIndex == lineIndex);
            var lineFont = lineAdjustment?.FontSizePx is > 0 && lineAdjustment.FontSizePx != font.Size
                ? new SKFont(font.Typeface, lineAdjustment.FontSizePx.Value) { Embolden = font.Embolden, SkewX = font.SkewX }
                : null;
            var activeFont = lineFont ?? font;
            var activePaint = lineAdjustment?.Color is null ? fill : ClonePaint(fill, ParseColor(lineAdjustment.Color));
            using (activePaint == fill ? null : activePaint)
            {
                var baseline = y + lineIndex * lineHeight + (lineAdjustment?.OffsetYPx ?? 0);
                DrawLine(canvas, line, box, globalCharacterIndex, baseline, lineAdjustment?.OffsetXPx ?? 0, block.Layout.Alignment, activeFont, activePaint, stroke, block.Layout.LetterSpacingPx, block.Layout.Advanced?.CharacterAdjustments);
            }
            globalCharacterIndex += StringInfo.ParseCombiningCharacters(line).Length;
            lineFont?.Dispose();
        }
    }

    private static void DrawLine(SKCanvas canvas, string line, TextLayoutBox box, int globalStart, float baseline, float offsetX, DialogueTextAlignment alignment, SKFont font, SKPaint fill, SKPaint? stroke, float letterSpacing, IReadOnlyList<DialogueCharacterAdjustment>? adjustments)
    {
        var starts = StringInfo.ParseCombiningCharacters(line);
        var graphemes = starts
            .Select((start, index) => (Text: line[start..(index + 1 < starts.Length ? starts[index + 1] : line.Length)], Index: index))
            .ToArray();
        var widths = graphemes.Select(item => font.MeasureText(item.Text, fill)).ToArray();
        var totalWidth = widths.Sum() + Math.Max(0, graphemes.Length - 1) * letterSpacing;
        var x = alignment switch
        {
            DialogueTextAlignment.Start => box.X0,
            DialogueTextAlignment.End => box.X1 - totalWidth,
            _ => box.X0 + (box.X1 - box.X0 - totalWidth) / 2f
        };
        x += offsetX;
        for (var index = 0; index < graphemes.Length; index++)
        {
            var item = graphemes[index];
            var adjustment = adjustments?.FirstOrDefault(candidate => candidate.CharacterIndex == globalStart + item.Index);
            var drawX = x + (adjustment?.OffsetXPx ?? 0);
            var drawY = baseline + (adjustment?.OffsetYPx ?? 0);
            var drawFont = font;
            SKFont? adjustedFont = null;
            SKPaint? adjustedPaint = null;
            if (adjustment?.FontSizePx is > 0)
            {
                adjustedFont = new SKFont(font.Typeface, adjustment.FontSizePx.Value) { Embolden = font.Embolden, SkewX = font.SkewX };
                drawFont = adjustedFont;
            }
            if (adjustment?.Color is not null) adjustedPaint = ClonePaint(fill, ParseColor(adjustment.Color));
            var drawPaint = adjustedPaint ?? fill;
            DrawText(canvas, item.Text, drawX, drawY, drawFont, drawPaint, stroke);
            adjustedPaint?.Dispose();
            adjustedFont?.Dispose();
            x += widths[index] + letterSpacing;
        }
    }

    private static void DrawVertical(SKCanvas canvas, RenderBlock block, SKFont font, SKPaint fill, SKPaint? stroke)
    {
        var box = block.Layout.TextBox;
        var text = block.Text.Replace("\n", string.Empty, StringComparison.Ordinal);
        var starts = StringInfo.ParseCombiningCharacters(text);
        var characters = starts
            .Select((start, index) => text[start..(index + 1 < starts.Length ? starts[index + 1] : text.Length)])
            .ToArray();
        font.GetFontMetrics(out var metrics);
        var advance = Math.Max(font.Size, metrics.Descent - metrics.Ascent) + block.Layout.LetterSpacingPx;
        var total = characters.Length * advance;
        var y = box.Y0 + (box.Y1 - box.Y0 - total) / 2f - metrics.Ascent;
        var x = box.X0 + (box.X1 - box.X0) / 2f;
        for (var index = 0; index < characters.Length; index++)
        {
            var glyph = characters[index];
            var width = font.MeasureText(glyph, fill);
            DrawText(canvas, glyph, x - width / 2, y + index * advance, font, fill, stroke);
        }
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint fill, SKPaint? stroke)
    {
        if (stroke is not null) canvas.DrawText(text, x, y, SKTextAlign.Left, font, stroke);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, fill);
    }

    private static SKPaint? CreateStroke(DialogueTextStroke? stroke)
    {
        if (stroke is null || stroke.WidthPx <= 0) return null;
        return new SKPaint { IsAntialias = true, Color = ParseColor(stroke.Color), Style = SKPaintStyle.Stroke, StrokeWidth = stroke.WidthPx, StrokeJoin = stroke.RoundCorners ? SKStrokeJoin.Round : SKStrokeJoin.Miter, StrokeCap = stroke.RoundCorners ? SKStrokeCap.Round : SKStrokeCap.Butt };
    }

    private static string[] NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static SKColor ParseColor(string? value) => !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out var color) ? color : SKColors.White;

    private static SKPaint ClonePaint(SKPaint source, SKColor color) => new()
    {
        IsAntialias = source.IsAntialias,
        Color = color,
        Style = source.Style,
        StrokeWidth = source.StrokeWidth,
        StrokeJoin = source.StrokeJoin,
        StrokeCap = source.StrokeCap
    };

    private sealed record RenderBlock(string Text, DialogueTextLayout Layout);
}

internal sealed record PageBackgroundData(string MediaType, ReadOnlyMemory<byte> Data);
