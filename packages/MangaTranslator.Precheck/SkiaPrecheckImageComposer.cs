using SkiaSharp;

namespace MangaTranslator.Precheck;

public sealed class SkiaPrecheckImageComposer : IPrecheckImageComposer
{
    private const int LabelHeight = 34;
    private const int OuterPadding = 18;
    private const int CardGap = 14;

    public async Task<IReadOnlyList<GeneratedImage>> CreateContactSheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);

        var pageCount = Math.Max(1, request.Options.ContactSheetPageCount);
        var result = new List<GeneratedImage>();
        for (var offset = 0; offset < pages.Count; offset += pageCount)
        {
            var chunk = pages.Skip(offset).Take(pageCount).ToArray();
            var assets = await LoadAssetsAsync(request, chunk, cancellationToken).ConfigureAwait(false);
            result.Add(ComposeGrid(
                assets,
                request.Options.ContactSheetColumns,
                request.Options.ContactSheetThumbnailWidth,
                request.Options.ContactSheetThumbnailHeight,
                $"contact_{result.Count:000}"));
        }

        return result;
    }

    public async Task<IReadOnlyList<GeneratedImage>> CreateAdjacencySheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);

        var pairCountPerSheet = Math.Max(1, request.Options.ContactSheetPageCount / 2);
        var pairs = pages
            .Zip(pages.Skip(1), (left, right) => new[] { left, right })
            .ToArray();
        var result = new List<GeneratedImage>();

        for (var offset = 0; offset < pairs.Length; offset += pairCountPerSheet)
        {
            var chunk = pairs.Skip(offset).Take(pairCountPerSheet).ToArray();
            var flattened = chunk
                .SelectMany(pair => pair)
                .DistinctBy(page => page.PageId, StringComparer.Ordinal)
                .ToArray();
            var assets = await LoadAssetsAsync(request, flattened, cancellationToken).ConfigureAwait(false);
            var lookup = assets.ToDictionary(asset => asset.Page.PageId, StringComparer.Ordinal);
            result.Add(ComposeAdjacencyGrid(
                chunk,
                lookup,
                2,
                request.Options.ContactSheetThumbnailWidth,
                request.Options.ContactSheetThumbnailHeight,
                $"adjacency_{result.Count:000}"));
        }

        return result;
    }

    private static async Task<PageAsset[]> LoadAssetsAsync(
        PrecheckRequest request,
        PageDescriptor[] pages,
        CancellationToken cancellationToken)
    {
        var assets = new List<PageAsset>(pages.Length);
        foreach (var page in pages)
        {
            var bytes = await request.PageAssets
                .GetPageAsync(page.PageId, ImageQuality.Thumbnail, cancellationToken)
                .ConfigureAwait(false);
            assets.Add(new PageAsset(page, bytes));
        }

        return [.. assets];
    }

    private static GeneratedImage ComposeGrid(
        PageAsset[] assets,
        int columns,
        int thumbnailWidth,
        int thumbnailHeight,
        string assetId)
    {
        columns = Math.Max(1, columns);
        var rows = Math.Max(1, (int)Math.Ceiling(assets.Length / (double)columns));
        var cardWidth = thumbnailWidth + OuterPadding * 2;
        var cardHeight = thumbnailHeight + LabelHeight + OuterPadding * 2;
        var width = columns * cardWidth + (columns - 1) * CardGap;
        var height = rows * cardHeight + (rows - 1) * CardGap;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(232, 232, 232));
        using var labelFont = new SKFont(SKTypeface.Default, 16);
        using var labelPaint = CreateLabelPaint();

        for (var index = 0; index < assets.Length; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var x = column * (cardWidth + CardGap);
            var y = row * (cardHeight + CardGap);
            DrawPageCard(surface.Canvas, assets[index], x, y, thumbnailWidth, thumbnailHeight, labelFont, labelPaint);
        }

        return EncodeSurface(surface, assetId);
    }

    private static GeneratedImage ComposeAdjacencyGrid(
        PageDescriptor[][] pairs,
        Dictionary<string, PageAsset> assets,
        int columns,
        int thumbnailWidth,
        int thumbnailHeight,
        string assetId)
    {
        var cardWidth = thumbnailWidth * 2 + OuterPadding * 2;
        var cardHeight = thumbnailHeight + LabelHeight + OuterPadding * 2;
        var rows = Math.Max(1, (int)Math.Ceiling(pairs.Length / (double)columns));
        var width = columns * cardWidth + (columns - 1) * CardGap;
        var height = rows * cardHeight + (rows - 1) * CardGap;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(232, 232, 232));
        using var labelFont = new SKFont(SKTypeface.Default, 16);
        using var labelPaint = CreateLabelPaint();

        for (var index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            var column = index % columns;
            var row = index / columns;
            var x = column * (cardWidth + CardGap);
            var y = row * (cardHeight + CardGap);

            DrawPageCard(surface.Canvas, assets[pair[0].PageId], x, y, thumbnailWidth, thumbnailHeight, labelFont, labelPaint);
            DrawPageCard(
                surface.Canvas,
                assets[pair[1].PageId],
                x + thumbnailWidth,
                y,
                thumbnailWidth,
                thumbnailHeight,
                labelFont,
                labelPaint);
        }

        return EncodeSurface(surface, assetId);
    }

    private static void DrawPageCard(
        SKCanvas canvas,
        PageAsset asset,
        int x,
        int y,
        int targetWidth,
        int targetHeight,
        SKFont labelFont,
        SKPaint labelPaint)
    {
        using var bitmap = SKBitmap.Decode(asset.Bytes.ToArray())
            ?? throw new InvalidDataException($"Unable to decode page {asset.Page.PageId}.");
        using var backgroundPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(170, 170, 170),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        var card = new SKRect(x, y, x + targetWidth + OuterPadding * 2, y + targetHeight + LabelHeight + OuterPadding * 2);
        canvas.DrawRect(card, backgroundPaint);
        canvas.DrawRect(card, borderPaint);

        var imageRect = FitRect(
            bitmap.Width,
            bitmap.Height,
            new SKRect(x + OuterPadding, y + OuterPadding, x + OuterPadding + targetWidth, y + OuterPadding + targetHeight));
        canvas.DrawBitmap(bitmap, imageRect, SKSamplingOptions.Default);
        canvas.DrawText(
            $"{asset.Page.PageId}  [{asset.Page.SourceIndex}]",
            x + OuterPadding,
            y + OuterPadding + targetHeight + 23,
            SKTextAlign.Left,
            labelFont,
            labelPaint);
    }

    private static SKRect FitRect(int sourceWidth, int sourceHeight, SKRect bounds)
    {
        var scale = Math.Min(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var left = bounds.Left + (bounds.Width - width) / 2;
        var top = bounds.Top + (bounds.Height - height) / 2;
        return new SKRect(left, top, left + width, top + height);
    }

    private static SKPaint CreateLabelPaint()
    {
        return new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true
        };
    }

    private static GeneratedImage EncodeSurface(SKSurface surface, string assetId)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return new GeneratedImage(
            assetId,
            "image/jpeg",
            data.ToArray(),
            image.Width,
            image.Height);
    }

    private sealed record PageAsset(PageDescriptor Page, ReadOnlyMemory<byte> Bytes);
}
