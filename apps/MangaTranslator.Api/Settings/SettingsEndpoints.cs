using MangaTranslator.Contracts;
using MangaTranslator.Api.Projects;
using MangaTranslator.Dialogue;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Rendering;
using SkiaSharp;

namespace MangaTranslator.Api.Settings;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/settings", GetSettings);
        endpoints.MapPatch("/api/settings", UpdateSettings);
        endpoints.MapPost("/api/settings/fonts/import", ImportFonts);
        endpoints.MapGet("/api/settings/fonts/{fontId}/preview", GetFontPreview);
        endpoints.MapDelete("/api/settings/fonts/{fontId}", RemoveFont);
        return endpoints;
    }

    private static async Task<IResult> GetSettings(IApplicationSettingsStore settings, HttpContext context) =>
        Results.Ok(await settings.GetAsync(context.RequestAborted));

    private static async Task<IResult> UpdateSettings(
        UpdateAppSettingsRequest request,
        IApplicationSettingsStore settings,
        HttpContext context)
    {
        try
        {
            return Results.Ok(await settings.UpdateAsync(request, context.RequestAborted));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> ImportFonts(
        ImportFontsRequest request,
        IApplicationSettingsStore settings,
        HttpContext context)
    {
        try
        {
            return Results.Ok(await settings.ImportFontsAsync(request, context.RequestAborted));
        }
        catch (Exception exception) when (exception is ArgumentException or ProjectLocationException)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> RemoveFont(
        string fontId,
        IApplicationSettingsStore settings,
        HttpContext context)
    {
        try
        {
            return Results.Ok(await settings.RemoveFontAsync(fontId, context.RequestAborted));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> GetFontPreview(
        string fontId,
        string? locale,
        FontCatalogService fonts,
        ManagedLetteringRenderer renderer,
        IApplicationSettingsStore settings,
        HttpContext context)
    {
        if (fonts.Find(fontId) is null)
        {
            return Results.NotFound();
        }
        var text = string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase)
            ? "Everyone translates manga · Aa 123"
            : "人人都是漫画翻译家 · 中文 Aa 123";
        var appSettings = await settings.GetAsync(context.RequestAborted);
        var block = new TextBlockRenderData(
            $"font-preview-{fontId}",
            text,
            new DialogueTextLayout(
                new TextLayoutBox(0, 0, 720, 86),
                DialogueTextOrientation.Horizontal,
                DialogueReadingDirection.LeftToRight,
                DialogueTextAlignment.Start,
                fontId,
                34,
                Bold: false,
                Italic: false,
                Color: "#f2f2f2",
                LetterSpacingPx: 0,
                FontWeight: 400));
        var preview = renderer.RenderBlock(block, appSettings.FallbackFontId);
        return Results.File(CenterFontPreview(preview.PngData.Span, 720, 86), "image/png", enableRangeProcessing: false);
    }

    private static byte[] CenterFontPreview(ReadOnlySpan<byte> pngData, int width, int height)
    {
        using var source = SKBitmap.Decode(pngData.ToArray())
            ?? throw new InvalidOperationException("Unable to decode the rendered font preview.");
        var bounds = FindContentBounds(source);
        using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        if (!bounds.IsEmpty)
        {
            var left = 8f;
            var top = Math.Max(0, (height - bounds.Height) / 2f);
            canvas.DrawBitmap(source, bounds, new SKRect(left, top, left + bounds.Width, top + bounds.Height), SKSamplingOptions.Default);
        }
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode the centered font preview.");
        return encoded.ToArray();
    }

    private static SKRectI FindContentBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < left || bottom < top
            ? SKRectI.Empty
            : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
