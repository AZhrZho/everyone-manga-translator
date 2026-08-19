using System.Text;
using SkiaSharp;

namespace MangaTranslator.FontManager;

internal sealed class FontSampleRenderer
{
    private const int Margin = 24;
    private const int TitleHeight = 96;
    private const int HeaderHeight = 82;
    private const int FooterHeight = 50;
    private const int IdColumnWidth = 132;
    private const int NameColumnWidth = 360;
    private const int StyleColumnWidth = 650;
    private const int RowHeight = 214;

    private readonly FontLibrary library;

    public FontSampleRenderer(FontLibrary library)
    {
        this.library = library;
    }

    public FontSampleInfo Render(
        FontCatalog catalog,
        IReadOnlyList<string> sampleLines,
        int fontSizePx,
        string outputPath)
    {
        var width = Margin * 2 + IdColumnWidth + NameColumnWidth + StyleColumnWidth * 3;
        var height = Margin * 2
            + TitleHeight
            + HeaderHeight
            + RowHeight * catalog.Fonts.Count
            + FooterHeight;
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(246, 247, 250));

        var cells = new List<FontSampleCell>(catalog.Fonts.Count * 3);
        var typefaces = new Dictionary<(string Path, int FaceIndex), SKTypeface>();
        try
        {
            var uiTypeface = SelectUiTypeface(catalog, typefaces);
            DrawTitle(canvas, catalog, fontSizePx, width);
            DrawHeader(canvas, uiTypeface);
            for (var row = 0; row < catalog.Fonts.Count; row++)
            {
                var entry = catalog.Fonts[row];
                var top = Margin + TitleHeight + HeaderHeight + row * RowHeight;
                DrawIdentityCells(canvas, entry, top, uiTypeface);
                foreach (var slot in Enum.GetValues<FontStyleSlot>())
                {
                    var column = (int)slot;
                    var left = Margin + IdColumnWidth + NameColumnWidth + column * StyleColumnWidth;
                    var cell = DrawStyleCell(
                        canvas,
                        entry,
                        slot,
                        left,
                        top,
                        sampleLines,
                        fontSizePx,
                        typefaces);
                    cells.Add(cell);
                }
            }

            DrawFooter(canvas, catalog, width, height);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
            {
                throw new FontLibraryException("SkiaSharp failed to encode the sample sheet as PNG.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            data.SaveTo(stream);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (var typeface in typefaces.Values)
            {
                typeface.Dispose();
            }
        }

        return new FontSampleInfo
        {
            RelativePath = string.Empty,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceLibraryRevision = catalog.LibraryRevision,
            FontSizePx = fontSizePx,
            SampleLines = sampleLines.ToList(),
            WidthPx = width,
            HeightPx = height,
            Cells = cells
        };
    }

    private static void DrawTitle(SKCanvas canvas, FontCatalog catalog, int fontSizePx, int width)
    {
        using var paint = CreateTextStyle(SKTypeface.Default, 28, new SKColor(25, 32, 44));
        paint.FakeBoldText = true;
        canvas.DrawText("Font Catalog / Typography Samples", Margin, Margin + 31, paint);
        using var detailPaint = CreateTextStyle(SKTypeface.Default, 18, new SKColor(95, 103, 117));
        canvas.DrawText(
            $"revision {catalog.LibraryRevision}  ·  sample size {fontSizePx}px  ·  fixed-size comparison",
            Margin,
            Margin + 63,
            detailPaint);
        using var linePaint = new SKPaint
        {
            Color = new SKColor(203, 208, 218),
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawLine(
            Margin,
            Margin + TitleHeight - 10,
            width - Margin,
            Margin + TitleHeight - 10,
            linePaint);
    }

    private static void DrawHeader(SKCanvas canvas, SKTypeface uiTypeface)
    {
        var top = Margin + TitleHeight;
        var left = Margin;
        DrawHeaderCell(canvas, left, top, IdColumnWidth, "FONT ID", uiTypeface);
        left += IdColumnWidth;
        DrawHeaderCell(canvas, left, top, NameColumnWidth, "FONT NAME", uiTypeface);
        left += NameColumnWidth;
        DrawHeaderCell(canvas, left, top, StyleColumnWidth, "REGULAR  ·  常规", uiTypeface);
        left += StyleColumnWidth;
        DrawHeaderCell(canvas, left, top, StyleColumnWidth, "BOLD  ·  粗体", uiTypeface);
        left += StyleColumnWidth;
        DrawHeaderCell(canvas, left, top, StyleColumnWidth, "ITALIC  ·  斜体", uiTypeface);
    }

    private static void DrawHeaderCell(
        SKCanvas canvas,
        int left,
        int top,
        int width,
        string label,
        SKTypeface uiTypeface)
    {
        var rect = new SKRect(left, top, left + width, top + HeaderHeight);
        using var fill = new SKPaint { Color = new SKColor(229, 233, 241), IsAntialias = true };
        using var border = new SKPaint
        {
            Color = new SKColor(188, 195, 208),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, border);
        using var text = CreateTextStyle(uiTypeface, 22, new SKColor(43, 51, 65));
        text.FakeBoldText = true;
        canvas.DrawText(label, left + 16, top + 50, text);
    }

    private static void DrawIdentityCells(
        SKCanvas canvas,
        FontEntry entry,
        int top,
        SKTypeface uiTypeface)
    {
        var left = Margin;
        DrawPlainCell(canvas, left, top, IdColumnWidth, new SKColor(250, 251, 253));
        left += IdColumnWidth;
        DrawPlainCell(canvas, left, top, NameColumnWidth, new SKColor(250, 251, 253));

        using var idPaint = CreateTextStyle(uiTypeface, 30, new SKColor(22, 77, 141));
        idPaint.FakeBoldText = true;
        var identityBaseline = GetVerticallyCenteredBaseline(idPaint, top, RowHeight);
        var idWidth = idPaint.MeasureText(entry.FontId);
        canvas.DrawText(
            entry.FontId,
            Margin + (IdColumnWidth - idWidth) / 2,
            identityBaseline,
            idPaint);

        using var namePaint = CreateTextStyle(uiTypeface, 30, new SKColor(37, 43, 54));
        canvas.Save();
        canvas.ClipRect(new SKRect(
            Margin + IdColumnWidth + 8,
            top + 8,
            Margin + IdColumnWidth + NameColumnWidth - 8,
            top + RowHeight - 8));
        canvas.DrawText(
            FitText(
                namePaint,
                CleanDisplayName(entry.DisplayName),
                NameColumnWidth - 32),
            Margin + IdColumnWidth + 16,
            identityBaseline,
            namePaint);
        canvas.Restore();
    }

    private FontSampleCell DrawStyleCell(
        SKCanvas canvas,
        FontEntry entry,
        FontStyleSlot slot,
        int left,
        int top,
        IReadOnlyList<string> sampleLines,
        int fontSizePx,
        Dictionary<(string Path, int FaceIndex), SKTypeface> typefaces)
    {
        var background = slot switch
        {
            FontStyleSlot.Regular => new SKColor(255, 255, 255),
            FontStyleSlot.Bold => new SKColor(255, 252, 247),
            FontStyleSlot.Italic => new SKColor(248, 253, 255),
            _ => SKColors.White
        };
        DrawPlainCell(canvas, left, top, StyleColumnWidth, background);
        var asset = entry.GetStyle(slot);
        var renderMode = asset is not null ? SampleRenderMode.Native : SampleRenderMode.Synthetic;
        var sourceStyle = asset is not null ? slot : FontStyleSlot.Regular;
        var sourceAsset = asset ?? entry.Regular;
        if (sourceAsset is null)
        {
            throw new FontLibraryException($"{entry.FontId} has no regular face for sample rendering.");
        }

        var typeface = GetTypeface(sourceAsset, typefaces);
        var fullText = string.Join("\n", sampleLines);
        var missingGlyphs = FindMissingGlyphs(typeface, fullText);
        var label = asset is not null
            ? "native"
            : $"synthetic from {sourceStyle.ToString().ToLowerInvariant()}";
        if (missingGlyphs.Count > 0)
        {
            label += $"  ·  missing {missingGlyphs.Count}";
        }

        using var badgePaint = CreateTextStyle(SKTypeface.Default, 17, new SKColor(96, 104, 119));
        canvas.DrawText(label, left + 18, top + 30, badgePaint);
        using var samplePaint = CreateTextStyle(typeface, fontSizePx, new SKColor(26, 29, 36));
        samplePaint.FakeBoldText = asset is null && slot == FontStyleSlot.Bold;
        samplePaint.TextSkewX = asset is null && slot == FontStyleSlot.Italic ? -0.24f : 0;
        samplePaint.GetFontMetrics(out var metrics);
        var baseline = top + 52 - metrics.Ascent;
        EnsureSampleFits(
            entry.FontId,
            slot,
            sampleLines,
            samplePaint,
            metrics,
            baseline,
            top);
        canvas.Save();
        canvas.ClipRect(new SKRect(
            left + 8,
            top + 6,
            left + StyleColumnWidth - 8,
            top + RowHeight - 6));
        foreach (var line in sampleLines)
        {
            canvas.DrawText(line, left + 18, baseline, samplePaint);
            baseline += fontSizePx * 1.25f;
        }

        canvas.Restore();
        return new FontSampleCell
        {
            FontId = entry.FontId,
            Style = slot,
            RenderMode = renderMode,
            SourceStyle = sourceStyle,
            MissingGlyphCount = missingGlyphs.Count,
            MissingGlyphs = missingGlyphs,
            Label = label
        };
    }

    private static void EnsureSampleFits(
        string fontId,
        FontStyleSlot slot,
        IReadOnlyList<string> sampleLines,
        FontTextStyle samplePaint,
        SKFontMetrics metrics,
        float firstBaseline,
        int top)
    {
        var maximumWidth = StyleColumnWidth - 26;
        foreach (var line in sampleLines)
        {
            var measuredWidth = samplePaint.MeasureText(line);
            if (measuredWidth > maximumWidth)
            {
                throw new FontLibraryException(
                    $"{fontId}/{slot.ToString().ToLowerInvariant()} sample text is too wide "
                    + $"for the fixed comparison cell ({measuredWidth:0}px > {maximumWidth}px). "
                    + "Use shorter text or a smaller --font-size.");
            }
        }

        var finalBaseline = firstBaseline + (sampleLines.Count - 1) * samplePaint.TextSize * 1.25f;
        var lowestPixel = finalBaseline + metrics.Descent;
        var maximumBottom = top + RowHeight - 12;
        if (lowestPixel > maximumBottom)
        {
            throw new FontLibraryException(
                $"{fontId}/{slot.ToString().ToLowerInvariant()} sample text is too tall "
                + "for the fixed comparison row. Use fewer lines or a smaller --font-size.");
        }
    }

    private SKTypeface GetTypeface(
        FontFaceAsset asset,
        Dictionary<(string Path, int FaceIndex), SKTypeface> typefaces)
    {
        var absolutePath = library.ResolveManagedPath(asset.RelativePath);
        var key = (absolutePath, asset.FaceIndex);
        if (!typefaces.TryGetValue(key, out var typeface))
        {
            typeface = SKTypeface.FromFile(absolutePath, asset.FaceIndex)
                ?? throw new FontLibraryException(
                    $"Could not load managed font file: {asset.RelativePath}");
            typefaces.Add(key, typeface);
        }

        return typeface;
    }

    private SKTypeface SelectUiTypeface(
        FontCatalog catalog,
        Dictionary<(string Path, int FaceIndex), SKTypeface> typefaces)
    {
        const string uiText = "FONT ID / CATEGORY 常规粗体斜体";
        string[] preferredSystemFamilies =
        [
            "Microsoft YaHei",
            "Noto Sans CJK SC",
            "Noto Sans SC",
            "PingFang SC",
            "WenQuanYi Zen Hei"
        ];
        foreach (var family in preferredSystemFamilies)
        {
            var systemCandidate = SKTypeface.FromFamilyName(family);
            if (systemCandidate is not null && ContainsGlyphs(systemCandidate, uiText))
            {
                typefaces.Add(($"<ui:{family}>", 0), systemCandidate);
                return systemCandidate;
            }

            systemCandidate?.Dispose();
        }

        foreach (var entry in catalog.Fonts)
        {
            if (entry.Regular is null)
            {
                continue;
            }

            var candidate = GetTypeface(entry.Regular, typefaces);
            if (ContainsGlyphs(candidate, uiText))
            {
                return candidate;
            }
        }

        return SKTypeface.Default;
    }

    private static void DrawPlainCell(SKCanvas canvas, int left, int top, int width, SKColor color)
    {
        var rect = new SKRect(left, top, left + width, top + RowHeight);
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        using var border = new SKPaint
        {
            Color = new SKColor(205, 210, 220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, border);
    }

    private static void DrawFooter(SKCanvas canvas, FontCatalog catalog, int width, int height)
    {
        using var paint = CreateTextStyle(SKTypeface.Default, 16, new SKColor(105, 113, 126));
        var y = height - Margin - 18;
        canvas.DrawText(
            "Native = imported face. Synthetic = sample preview only; see font_catalog.json.",
            Margin,
            y,
            paint);
        var right = $"{catalog.Fonts.Count} font families";
        var rightWidth = paint.MeasureText(right);
        canvas.DrawText(right, width - Margin - rightWidth, y, paint);
    }

    private static FontTextStyle CreateTextStyle(SKTypeface typeface, float textSize, SKColor color)
    {
        return new FontTextStyle(typeface, textSize, color);
    }

    private static string FitText(FontTextStyle paint, string text, float maximumWidth)
    {
        if (paint.MeasureText(text) <= maximumWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        var length = text.Length;
        while (length > 0
               && paint.MeasureText(text[..length] + ellipsis) > maximumWidth)
        {
            length--;
        }

        return length == 0 ? ellipsis : text[..length] + ellipsis;
    }

    private static float GetVerticallyCenteredBaseline(
        FontTextStyle paint,
        float top,
        float height)
    {
        paint.GetFontMetrics(out var metrics);
        var textHeight = metrics.Descent - metrics.Ascent;
        return top + (height - textHeight) / 2 - metrics.Ascent;
    }

    private static string CleanDisplayName(string displayName)
    {
        var separatorIndex = displayName.IndexOf('｜');
        return separatorIndex >= 0 && separatorIndex + 1 < displayName.Length
            ? displayName[(separatorIndex + 1)..].Trim()
            : displayName;
    }

    private static List<string> FindMissingGlyphs(SKTypeface typeface, string text)
    {
        using var font = new SKFont(typeface);
        return text
            .EnumerateRunes()
            .Where(rune => !Rune.IsWhiteSpace(rune))
            .Where(rune => !font.ContainsGlyph(rune.Value))
            .Select(rune => rune.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool ContainsGlyphs(SKTypeface typeface, string text)
    {
        using var font = new SKFont(typeface);
        return font.ContainsGlyphs(text);
    }
}

internal sealed class FontTextStyle : IDisposable
{
    public FontTextStyle(SKTypeface typeface, float textSize, SKColor color)
    {
        Font = new SKFont(typeface, textSize) { Subpixel = true };
        Paint = new SKPaint { Color = color, IsAntialias = true };
    }

    public SKFont Font { get; }

    public SKPaint Paint { get; }

    public bool FakeBoldText
    {
        get => Font.Embolden;
        set => Font.Embolden = value;
    }

    public float TextSkewX
    {
        get => Font.SkewX;
        set => Font.SkewX = value;
    }

    public float TextSize => Font.Size;

    public float MeasureText(string text) => Font.MeasureText(text, Paint);

    public void GetFontMetrics(out SKFontMetrics metrics) => Font.GetFontMetrics(out metrics);

    public void Dispose()
    {
        Paint.Dispose();
        Font.Dispose();
    }
}

internal static class FontTextCanvasExtensions
{
    public static void DrawText(
        this SKCanvas canvas,
        string text,
        float x,
        float y,
        FontTextStyle style) =>
        canvas.DrawText(text, x, y, SKTextAlign.Left, style.Font, style.Paint);
}
