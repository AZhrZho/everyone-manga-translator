using SkiaSharp;

namespace MangaTranslator.Rendering;

public sealed class SkiaPageComposer : IPageComposer
{
    private static readonly SKColor DebugPageBorderColor = new(37, 99, 235);
    private static readonly SKColor DebugBlockBorderColor = new(220, 38, 38);

    public RenderedPage Compose(
        PageCompositionData page,
        PageBackground? background = null,
        bool debug = false)
    {
        ValidatePage(page);
        using var surface = SKSurface.Create(new SKImageInfo(
            page.Width,
            page.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        if (surface is null)
        {
            throw new RenderingException("SkiaSharp could not create the page surface.");
        }

        if (background is null)
        {
            surface.Canvas.Clear(SKColors.White);
        }
        else
        {
            DrawBackground(surface.Canvas, page, background);
        }

        foreach (var placement in page.Placements)
        {
            DrawBlock(surface.Canvas, placement);
        }

        if (debug)
        {
            DrawDebugBorders(surface.Canvas, page);
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new RenderingException("SkiaSharp could not encode the page as PNG.");
        return new RenderedPage(
            page.PageId,
            "image/png",
            page.Width,
            page.Height,
            encoded.ToArray());
    }

    private static void DrawBackground(
        SKCanvas canvas,
        PageCompositionData page,
        PageBackground background)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(background.MediaType);
        if (background.Data.IsEmpty)
        {
            throw new ArgumentException("Background data cannot be empty.", nameof(background));
        }

        using var bitmap = SKBitmap.Decode(background.Data.ToArray())
            ?? throw new RenderingException(
                $"Unable to decode page background '{background.MediaType}'.");
        if (bitmap.Width != page.Width || bitmap.Height != page.Height)
        {
            throw new RenderingException(
                $"Background is {bitmap.Width}x{bitmap.Height}, but page '{page.PageId}' "
                + $"is {page.Width}x{page.Height}. Backgrounds are never implicitly scaled.");
        }

        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default, null);
    }

    private static void DrawBlock(SKCanvas canvas, TextBlockPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(placement.Block);
        if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement),
                "Text-block coordinates must be finite.");
        }

        using var bitmap = SKBitmap.Decode(placement.Block.PngData.ToArray())
            ?? throw new RenderingException(
                $"Unable to decode rendered block '{placement.Block.BlockId}'.");
        if (bitmap.Width != placement.Block.Width || bitmap.Height != placement.Block.Height)
        {
            throw new RenderingException(
                $"Rendered block '{placement.Block.BlockId}' metadata does not match its PNG.");
        }

        canvas.Save();
        ApplyPlacementRotation(canvas, placement);
        canvas.DrawBitmap(bitmap, placement.X, placement.Y, SKSamplingOptions.Default, null);
        canvas.Restore();
    }

    private static void DrawDebugBorders(SKCanvas canvas, PageCompositionData page)
    {
        using var pagePaint = new SKPaint
        {
            Color = DebugPageBorderColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false
        };
        canvas.DrawRect(
            new SKRect(0.5f, 0.5f, page.Width - 0.5f, page.Height - 0.5f),
            pagePaint);

        using var blockPaint = new SKPaint
        {
            Color = DebugBlockBorderColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = false
        };
        foreach (var placement in page.Placements)
        {
            canvas.Save();
            ApplyPlacementRotation(canvas, placement);
            canvas.DrawRect(
                new SKRect(
                    placement.X + 0.5f,
                    placement.Y + 0.5f,
                    placement.X + placement.Block.Width - 0.5f,
                    placement.Y + placement.Block.Height - 0.5f),
                blockPaint);
            canvas.Restore();
        }
    }

    private static void ApplyPlacementRotation(
        SKCanvas canvas,
        TextBlockPlacement placement)
    {
        var rotation = placement.Rotation;
        if (rotation is null)
        {
            return;
        }

        if (!float.IsFinite(rotation.CenterXPx)
            || !float.IsFinite(rotation.CenterYPx)
            || !float.IsFinite(rotation.AngleDeg))
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement),
                "Text-block rotation values must be finite.");
        }

        if (rotation.AngleDeg == 0)
        {
            return;
        }

        var centerX = placement.X + rotation.CenterXPx;
        var centerY = placement.Y + rotation.CenterYPx;
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees(rotation.AngleDeg);
        canvas.Translate(-centerX, -centerY);
    }

    private static void ValidatePage(PageCompositionData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.PageId);
        ArgumentNullException.ThrowIfNull(page.Placements);
        if (page.Width <= 0 || page.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page),
                "Page width and height must be positive.");
        }
    }
}

public sealed class RenderingException : Exception
{
    public RenderingException(string message)
        : base(message)
    {
    }

    public RenderingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
