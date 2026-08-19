using System.Text.Json;
using System.Security.Cryptography;
using MangaTranslator.Api.Settings;
using MangaTranslator.Dialogue;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Rendering;

namespace MangaTranslator.Api.Projects;

public sealed class ProjectResultService(
    IProjectStore projects,
    IProjectAssetStore assets,
    FontCatalogService fonts,
    ManagedLetteringRenderer managedLettering,
    ILogger<ProjectResultService> logger) : IProjectResultService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ProjectAsset> RenderPageAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        var layout = await projects.GetPageLayoutAsync(projectId, pageId, cancellationToken);
        var backgroundAssetKey = await ResolveBackgroundAssetKeyAsync(project, page, cancellationToken);
        return await RenderPageAsync(
            projectId,
            pageId,
            backgroundAssetKey,
            layout.LayoutJson,
            cancellationToken);
    }

    public async Task<ProjectAsset> RenderBackgroundAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        var backgroundAssetKey = await ResolveBackgroundAssetKeyAsync(project, page, cancellationToken);
        await using var backgroundAsset = await assets.OpenReadAsync(projectId, backgroundAssetKey, cancellationToken);
        var background = await ReadBackgroundAsync(backgroundAsset, cancellationToken);
        return CreateResultAsset(page, ProjectResultRenderer.EncodeBackground(background));
    }

    public async Task<ProjectAsset> RenderLetteringAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        var layout = await projects.GetPageLayoutAsync(projectId, pageId, cancellationToken);
        if (string.IsNullOrWhiteSpace(layout.LayoutJson))
        {
            return CreateResultAsset(page, ProjectResultRenderer.CreateTransparentPage(page.Width, page.Height));
        }

        if (managedLettering.CanRender(layout.LayoutJson))
        {
            var bytes = await Task.Run(
                () => managedLettering.RenderLettering(page, layout.LayoutJson, project.FallbackFontId),
                cancellationToken);
            return CreateResultAsset(page, bytes);
        }

        var segments = ParseLayout(layout.LayoutJson);
        var rendered = await Task.Run(
            () => ProjectResultRenderer.RenderLettering(page, segments, fonts, project.DefaultFontId),
            cancellationToken);
        return CreateResultAsset(page, rendered);
    }

    public async Task<TextClipRenderResult> RenderTextClipAsync(
        string projectId,
        string pageId,
        string layoutJson,
        string segmentId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        _ = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        var segment = ParseLayout(layoutJson).FirstOrDefault(item =>
            string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The page layout does not contain segment '{segmentId}'.");
        if (!segment.IsRenderable || string.IsNullOrWhiteSpace(segment.TranslatedText))
        {
            var box = segment.Layout.TextBox;
            return TextClipRenderResult.Empty(
                segment.SegmentId,
                Math.Max(1, box.X1 - box.X0),
                Math.Max(1, box.Y1 - box.Y0));
        }

        RenderedTextBlock rendered;
        if (managedLettering.CanRenderFont(segment.Layout.FontId))
        {
            rendered = managedLettering.RenderClip(layoutJson, segment.SegmentId, project.FallbackFontId);
        }
        else
        {
            rendered = await Task.Run(
                () => ProjectResultRenderer.RenderClip(
                    segment,
                    fonts,
                    project.DefaultFontId),
                cancellationToken);
        }

        var data = rendered.PngData.ToArray();
        return new TextClipRenderResult(
            rendered.BlockId,
            rendered.Width,
            rendered.Height,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            data);
    }

    public async Task<ProjectAsset> RenderPageAsync(
        string projectId,
        string pageId,
        string backgroundAssetKey,
        string? layoutJson,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
            ?? throw new ProjectPageNotFoundException(projectId, pageId);
        await using var backgroundAsset = await assets.OpenReadAsync(projectId, backgroundAssetKey, cancellationToken);
        var background = await ReadBackgroundAsync(backgroundAsset, cancellationToken);

        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            return CreateResultAsset(page, ProjectResultRenderer.EncodeBackground(background));
        }

        if (managedLettering.CanRender(layoutJson))
        {
            var managedBytes = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return managedLettering.Render(page, background, layoutJson, project.FallbackFontId);
            }, cancellationToken);
            return CreateResultAsset(page, managedBytes);
        }

        var segments = ParseLayout(layoutJson);
        if (segments.Count == 0)
        {
            return CreateResultAsset(page, ProjectResultRenderer.EncodeBackground(background));
        }

        var rendered = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProjectResultRenderer.Render(
                page,
                background,
                segments,
                fonts,
                project.DefaultFontId);
        }, cancellationToken);
        return CreateResultAsset(page, rendered);
    }

    private async Task<string> ResolveBackgroundAssetKeyAsync(
        ProjectSnapshot project,
        ProjectPageSnapshot page,
        CancellationToken cancellationToken)
    {
        var imageEdit = await projects.GetPageImageEditAsync(project.ProjectId, page.PageId, cancellationToken);
        var candidates = new[]
        {
            imageEdit.RenderedAssetKey,
            imageEdit.DerivedCleanAssetKey,
            page.SourceAssetKey
        }.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var assetKey in candidates)
        {
            if (await assets.ExistsAsync(project.ProjectId, assetKey, cancellationToken))
            {
                if (!string.Equals(assetKey, candidates[0], StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Result background asset fallback selected for project {ProjectId}, page {PageId}. Selected {AssetKey}; preferred candidates were {Candidates}",
                        project.ProjectId,
                        page.PageId,
                        assetKey,
                        candidates);
                }

                return assetKey;
            }

            logger.LogWarning(
                "Result background asset is missing for project {ProjectId}, page {PageId}: {AssetKey}",
                project.ProjectId,
                page.PageId,
                assetKey);
        }

        throw new FileNotFoundException(
            $"No result background asset exists for project '{project.ProjectId}', page '{page.PageId}'. Tried: {string.Join(", ", candidates)}.");
    }

    private static async Task<PageBackgroundData> ReadBackgroundAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await asset.Content.CopyToAsync(buffer, cancellationToken);
        return new PageBackgroundData(asset.MediaType, buffer.ToArray());
    }

    private static IReadOnlyList<ProjectLayoutSegment> ParseLayout(string layoutJson)
    {
        using var document = JsonDocument.Parse(layoutJson);
        if (!document.RootElement.TryGetProperty("segments", out var segmentsElement)
            || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The page layout does not contain a segments array.");
        }

        var segments = new List<ProjectLayoutSegment>();
        foreach (var element in segmentsElement.EnumerateArray())
        {
            var layoutElement = element.TryGetProperty("layout", out var candidateLayout)
                ? candidateLayout
                : throw new InvalidOperationException("A page layout segment is missing its layout object.");
            var textLayout = JsonSerializer.Deserialize<DialogueTextLayout>(layoutElement.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException("A page layout segment contains an unreadable layout object.");
            if (textLayout.Advanced is not null)
            {
                textLayout = textLayout with
                {
                    Advanced = textLayout.Advanced with
                    {
                        LineAdjustments = textLayout.Advanced.LineAdjustments ?? [],
                        CharacterAdjustments = textLayout.Advanced.CharacterAdjustments ?? [],
                        RubyAnnotations = textLayout.Advanced.RubyAnnotations ?? [],
                        PlacementGroups = textLayout.Advanced.PlacementGroups ?? []
                    }
                };
            }

            segments.Add(new ProjectLayoutSegment(
                ReadString(element, "segment_id") ?? $"segment-{segments.Count + 1:0000}",
                ReadString(element, "translated_text") ?? string.Empty,
                textLayout,
                !element.TryGetProperty("status", out var statusElement)
                || !string.Equals(statusElement.GetString(), "unreadable", StringComparison.OrdinalIgnoreCase)));
        }
        return segments;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ProjectAsset CreateResultAsset(ProjectPageSnapshot page, byte[] data) => new(
        new MemoryStream(data, writable: false),
        "image/png",
        $"{Path.GetFileNameWithoutExtension(page.FileName)}.png",
        data.LongLength);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        return options;
    }

    public sealed record ProjectLayoutSegment(
        string SegmentId,
        string TranslatedText,
        DialogueTextLayout Layout,
        bool IsRenderable);

    public sealed record TextClipRenderResult(
        string SegmentId,
        int Width,
        int Height,
        string Fingerprint,
        byte[] PngData)
    {
        public static TextClipRenderResult Empty(string segmentId, int width, int height)
        {
            using var bitmap = new SkiaSharp.SKBitmap(
                width,
                height,
                SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul);
            bitmap.Erase(SkiaSharp.SKColors.Transparent);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Unable to encode an empty text clip.");
            var data = encoded.ToArray();
            return new TextClipRenderResult(
                segmentId,
                width,
                height,
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                data);
        }
    }
}
