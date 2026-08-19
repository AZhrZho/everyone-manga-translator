using System.Text.Json;
using MangaTranslator.Api.Settings;
using MangaTranslator.Dialogue;
using MangaTranslator.FontManager;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Rendering;
using SkiaSharp;

namespace MangaTranslator.Api.Projects;

public sealed class ManagedLetteringRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly FontLibrary fontLibrary;
    private readonly HashSet<string> managedFontIds;

    public ManagedLetteringRenderer(FontCatalogService fonts)
    {
        fontLibrary = new FontLibrary(fonts.LibraryRoot);
        managedFontIds = fontLibrary.ReadCatalog().Fonts
            .Select(item => item.FontId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool CanRender(string layoutJson)
    {
        try
        {
            var result = JsonSerializer.Deserialize<DialogueTranslationResult>(layoutJson, JsonOptions);
            return result is not null
                   && result.Segments
                       .Where(segment => segment.Status == DialogueSegmentStatus.Translated)
                       .All(segment => managedFontIds.Contains(segment.Layout.FontId));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool CanRenderFont(string fontId) => managedFontIds.Contains(fontId);

    public RenderedTextBlock RenderBlock(TextBlockRenderData block, string? fallbackFontId = null)
    {
        using var textRenderer = new SkiaTextBlockRenderer(fontLibrary, fallbackFontId);
        return textRenderer.Render(block);
    }

    public RenderedTextBlock RenderClip(string layoutJson, string segmentId, string? fallbackFontId = null)
    {
        var dialogue = JsonSerializer.Deserialize<DialogueTranslationResult>(layoutJson, JsonOptions)
            ?? throw new InvalidOperationException("The dialogue layout JSON could not be read.");
        var segment = dialogue.Segments.FirstOrDefault(item =>
            string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The dialogue layout does not contain segment '{segmentId}'.");
        var box = segment.Layout.TextBox;
        var width = Math.Max(1, box.X1 - box.X0);
        var height = Math.Max(1, box.Y1 - box.Y0);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var textRenderer = new SkiaTextBlockRenderer(fontLibrary, fallbackFontId);
        var unrotated = segment with { Layout = segment.Layout with { Rotation = null } };
        foreach (var block in PageRenderData.ExpandSegment(unrotated))
        {
            var rendered = textRenderer.Render(block);
            using var blockBitmap = SKBitmap.Decode(rendered.PngData.ToArray())
                ?? throw new InvalidOperationException($"Unable to decode rendered block '{rendered.BlockId}'.");
            canvas.DrawBitmap(
                blockBitmap,
                block.Layout.TextBox.X0 - box.X0,
                block.Layout.TextBox.Y0 - box.Y0,
                SKSamplingOptions.Default,
                null);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode the rendered text clip.");
        return new RenderedTextBlock(segment.SegmentId, width, height, encoded.ToArray());
    }

    internal byte[] Render(
        ProjectPageSnapshot page,
        PageBackgroundData background,
        string layoutJson,
        string? fallbackFontId = null)
    {
        var dialogue = JsonSerializer.Deserialize<DialogueTranslationResult>(layoutJson, JsonOptions)
            ?? throw new InvalidOperationException("The dialogue layout JSON could not be read.");
        var renderData = PageRenderData.FromDialogue(dialogue, page.Width, page.Height);
        using var textRenderer = new SkiaTextBlockRenderer(fontLibrary, fallbackFontId);
        var renderer = new DialoguePageRenderer(textRenderer, new SkiaPageComposer());
        return renderer.Render(
            renderData,
            new PageBackground(background.MediaType, background.Data),
            debug: false).Data.ToArray();
    }

    internal byte[] RenderLettering(
        ProjectPageSnapshot page,
        string layoutJson,
        string? fallbackFontId = null)
    {
        var dialogue = JsonSerializer.Deserialize<DialogueTranslationResult>(layoutJson, JsonOptions)
            ?? throw new InvalidOperationException("The dialogue layout JSON could not be read.");
        var renderData = PageRenderData.FromDialogue(dialogue, page.Width, page.Height);
        using var textRenderer = new SkiaTextBlockRenderer(fontLibrary, fallbackFontId);
        var renderer = new DialoguePageRenderer(textRenderer, new SkiaPageComposer());
        var transparentBackground = new PageBackground("image/png", ProjectResultRenderer.CreateTransparentPage(page.Width, page.Height));
        return renderer.Render(renderData, transparentBackground, debug: false).Data.ToArray();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        return options;
    }
}
