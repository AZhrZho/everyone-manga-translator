using System.Text.Json;
using MangaTranslator.Api.Projects;
using MangaTranslator.Api.Settings;
using MangaTranslator.Api.Storage;
using MangaTranslator.Dialogue;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.ProjectSystem;
using MangaTranslator.Rendering;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SkiaSharp;

namespace MangaTranslator.Api.Tests;

public sealed class ProjectResultRendererTests
{
    private static readonly JsonSerializerOptions DialogueJsonOptions = CreateDialogueJsonOptions();

    [Fact]
    public void FallbackClipHasRequestedDimensionsTransparencyAndNoDebugBorder()
    {
        var layout = Layout("not-a-managed-font", rotation: 31);
        var segment = new ProjectResultService.ProjectLayoutSegment(
            "segment-1",
            "测试",
            layout,
            true);
        var rendered = ProjectResultRenderer.RenderClip(
            segment,
            new FontCatalogService(new AppDataPaths()),
            null);

        Assert.Equal(160, rendered.Width);
        Assert.Equal(96, rendered.Height);
        using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
        Assert.NotNull(bitmap);
        Assert.Equal(160, bitmap.Width);
        Assert.Equal(96, bitmap.Height);
        Assert.Contains(bitmap.Pixels, color => color.Alpha > 0);
        AssertCornersTransparent(bitmap);
        Assert.True(EdgePixels(bitmap).Count(color => color.Alpha == 0) > bitmap.Width / 2);
    }

    [Fact]
    public void ManagedClipIsTransparentAndUsesNonDebugRendering()
    {
        var root = FindRepositoryRoot();
        var renderer = new ManagedLetteringRenderer(new FontCatalogService(
            new TestHostEnvironment(root),
            new AppDataPaths()));
        var rendered = renderer.RenderBlock(new TextBlockRenderData(
            "segment-managed",
            "漫画",
            Layout("F0001", rotation: 47)));

        Assert.Equal(160, rendered.Width);
        Assert.Equal(96, rendered.Height);
        using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
        Assert.NotNull(bitmap);
        Assert.Contains(bitmap.Pixels, color => color.Alpha > 0);
        AssertCornersTransparent(bitmap);
        Assert.True(EdgePixels(bitmap).Count(color => color.Alpha == 0) > bitmap.Width / 2);
    }

    [Fact]
    public void ManagedClipExpandsAdvancedPlacementGroupsInsideSegmentEnvelope()
    {
        var root = FindRepositoryRoot();
        var renderer = new ManagedLetteringRenderer(new FontCatalogService(
            new TestHostEnvironment(root),
            new AppDataPaths()));
        var advanced = new DialogueAdvancedLayout([], [], [])
        {
            PlacementGroups =
            [
                new DialoguePlacementGroup([0], ["region-1"], new TextLayoutBox(20, 30, 140, 100)),
                new DialoguePlacementGroup([1], ["region-2"], new TextLayoutBox(250, 210, 370, 280))
            ]
        };
        var segment = new DialogueSegment(
            "segment-grouped",
            DialogueSegmentKind.Dialogue,
            DialogueSegmentStatus.Translated,
            1,
            ["region-1", "region-2"],
            "source",
            "M\nW",
            Layout("F0001", rotation: 18) with
            {
                TextBox = new TextLayoutBox(10, 20, 380, 300),
                Advanced = advanced,
                FontSizePx = 40
            },
            null,
            1,
            []);
        var result = new DialogueTranslationResult(
            "1.2.0",
            "run-1",
            "book-1",
            "zh-Hans",
            "page-1",
            "provider-1",
            "model-1",
            [segment],
            [],
            [],
            null,
            new ModelResponseMetadata(null, TimeSpan.Zero));
        var rendered = renderer.RenderClip(
            JsonSerializer.Serialize(result, DialogueJsonOptions),
            segment.SegmentId);

        Assert.Equal(370, rendered.Width);
        Assert.Equal(280, rendered.Height);
        using var bitmap = SKBitmap.Decode(rendered.PngData.ToArray());
        Assert.NotNull(bitmap);
        Assert.True(HasVisiblePixel(bitmap, 10, 10, 130, 90));
        Assert.True(HasVisiblePixel(bitmap, 240, 190, 360, 270));
        Assert.False(HasVisiblePixel(bitmap, 150, 110, 230, 180));
        AssertCornersTransparent(bitmap);
    }

    [Fact]
    public void FallbackPageRenderNormalizesLegacyPageRelativeRotationCenter()
    {
        var box = new TextLayoutBox(275, 1238, 470, 1345);
        var localLayout = Layout("not-a-managed-font", rotation: -15) with
        {
            TextBox = box,
            Rotation = new DialogueBoxRotation(97.5f, 53.5f, -15)
        };
        var legacyLayout = localLayout with
        {
            Rotation = new DialogueBoxRotation(372.5f, 1291.5f, -15)
        };
        var page = new ProjectPageSnapshot(
            "page-0059",
            59,
            "page-0059.png",
            1440,
            2048,
            "story",
            true,
            PageReviewTag.Unreviewed,
            "source/page-0059.png",
            true,
            false,
            1,
            null,
            null,
            null,
            0);
        var background = new PageBackgroundData(
            "image/png",
            CreateSolidPng(page.Width, page.Height, SKColors.Transparent));
        var fonts = new FontCatalogService(new AppDataPaths());

        var legacy = ProjectResultRenderer.Render(
            page,
            background,
            [new ProjectResultService.ProjectLayoutSegment("legacy", "东京大学", legacyLayout, true)],
            fonts,
            null);
        var local = ProjectResultRenderer.Render(
            page,
            background,
            [new ProjectResultService.ProjectLayoutSegment("local", "东京大学", localLayout, true)],
            fonts,
            null);

        Assert.Equal(local, legacy);
    }

    [Fact]
    public void EmptyClipIsFullyTransparent()
    {
        var rendered = ProjectResultService.TextClipRenderResult.Empty("empty", 73, 41);
        using var bitmap = SKBitmap.Decode(rendered.PngData);

        Assert.NotNull(bitmap);
        Assert.Equal(73, bitmap.Width);
        Assert.Equal(41, bitmap.Height);
        Assert.All(bitmap.Pixels, color => Assert.Equal(0, color.Alpha));
    }

    private static DialogueTextLayout Layout(string fontId, float rotation) => new(
        new TextLayoutBox(20, 30, 180, 126),
        DialogueTextOrientation.Horizontal,
        DialogueReadingDirection.LeftToRight,
        DialogueTextAlignment.Center,
        fontId,
        28,
        false,
        false,
        "#ffffff",
        0,
        FontWeight: 400,
        Rotation: new DialogueBoxRotation(80, 48, rotation),
        Stroke: new DialogueTextStroke(2, "#000000", true));

    private static IEnumerable<SKColor> EdgePixels(SKBitmap bitmap)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            yield return bitmap.GetPixel(x, 0);
            yield return bitmap.GetPixel(x, bitmap.Height - 1);
        }
        for (var y = 1; y < bitmap.Height - 1; y++)
        {
            yield return bitmap.GetPixel(0, y);
            yield return bitmap.GetPixel(bitmap.Width - 1, y);
        }
    }

    private static bool HasVisiblePixel(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            }
        }

        return false;
    }

    private static byte[] CreateSolidPng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void AssertCornersTransparent(SKBitmap bitmap)
    {
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).Alpha);
        Assert.Equal(0, bitmap.GetPixel(0, bitmap.Height - 1).Alpha);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).Alpha);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resources", "font-library", "font_catalog.json")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository font library was not found.");
    }

    private static JsonSerializerOptions CreateDialogueJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        return options;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MangaTranslator.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
