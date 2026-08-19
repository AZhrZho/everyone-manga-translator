using System.Text.Json;
using MangaTranslator.Dialogue;
using MangaTranslator.FontReference.FileSystem;

namespace MangaTranslator.FontReference.FileSystem.Tests;

public sealed class FontReferenceProviderTests : IDisposable
{
    private const string OneByOnePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    private static readonly string[] FontTags = ["dialogue", "sans"];

    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "manga-translator-font-reference-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadsSnapshotAndReusesImmutableCatalogImage()
    {
        WriteLibrary();
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);
        var request = CreateRequest();

        FontReferenceSnapshot first = await provider.GetFontReferenceImageAsync(request);

        Assert.Equal("font_samples_r0003", first.ReferenceId);
        Assert.Equal(3, first.LibraryRevision);
        Assert.Equal("font_samples_r0003", first.Image.AssetId);
        Assert.Equal("image/png", first.Image.MediaType);
        Assert.Equal(1, first.Image.Width);
        Assert.Equal(1, first.Image.Height);
        Assert.Single(first.Fonts);
        Assert.Equal("F0001", first.Fonts[0].FontId);
        Assert.Equal("测试字体", first.Fonts[0].DisplayName);
        Assert.Equal(["dialogue", "sans"], first.Fonts[0].Tags);
        var style = Assert.Single(first.Fonts[0].Styles);
        Assert.Equal("regular", style.Style);
        Assert.Equal("native", style.RenderMode);

        File.Delete(Path.Combine(_rootDirectory, "samples", "font_samples_r0003.png"));
        File.Delete(Path.Combine(_rootDirectory, "font_catalog.json"));
        FontReferenceSnapshot second = await provider.GetFontReferenceImageAsync(request);

        Assert.Same(first, second);
        Assert.Equal(OneByOnePngBase64, Convert.ToBase64String(second.Image.Data.ToArray()));
    }

    [Fact]
    public async Task RejectsStaleSampleRevision()
    {
        WriteLibrary(sampleRevision: 2);
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);

        FontReferenceProviderException exception = await Assert.ThrowsAsync<FontReferenceProviderException>(
            () => provider.GetFontReferenceImageAsync(CreateRequest()).AsTask());

        Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsDuplicateFontIdsIgnoringCase()
    {
        WriteLibrary(
            fonts: [
                new FontFixture("F0001", "测试字体"),
                new FontFixture("f0001", "重复字体")
            ],
            cells: [
                new CellFixture("F0001", "regular"),
                new CellFixture("f0001", "regular")
            ]);
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);

        FontReferenceProviderException exception = await Assert.ThrowsAsync<FontReferenceProviderException>(
            () => provider.GetFontReferenceImageAsync(CreateRequest()).AsTask());

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsMissingSampleImage()
    {
        WriteLibrary();
        File.Delete(Path.Combine(_rootDirectory, "samples", "font_samples_r0003.png"));
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);

        FontReferenceProviderException exception = await Assert.ThrowsAsync<FontReferenceProviderException>(
            () => provider.GetFontReferenceImageAsync(CreateRequest()).AsTask());

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUnknownRequestedReference()
    {
        WriteLibrary();
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);

        FontReferenceProviderException exception = await Assert.ThrowsAsync<FontReferenceProviderException>(
            () => provider.GetFontReferenceImageAsync(CreateRequest("font_samples_r0002")).AsTask());

        Assert.Contains("not available", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsSamplePathOutsideSamplesDirectory()
    {
        WriteLibrary(relativePath: "../font_samples_r0003.png", writeSample: false);
        var provider = new FileSystemFontReferenceImageProvider(_rootDirectory);

        FontReferenceProviderException exception = await Assert.ThrowsAsync<FontReferenceProviderException>(
            () => provider.GetFontReferenceImageAsync(CreateRequest()).AsTask());

        Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadsCurrentRepositoryFontLibrary()
    {
        string repositoryRoot = FindRepositoryRoot();
        var provider = new FileSystemFontReferenceImageProvider(
            Path.Combine(repositoryRoot, "resources", "font-library"));

        FontReferenceSnapshot snapshot = await provider.GetFontReferenceImageAsync(CreateRequest());

        Assert.True(snapshot.LibraryRevision >= 0);
        Assert.NotEmpty(snapshot.Image.Data.ToArray());
        Assert.Equal("image/png", snapshot.Image.MediaType);
        Assert.True(snapshot.Image.Width > 0);
        Assert.True(snapshot.Image.Height > 0);
        Assert.NotEmpty(snapshot.Fonts);
        Assert.Equal(
            snapshot.Fonts.Count,
            snapshot.Fonts.Select(font => font.FontId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(snapshot.Fonts, font => Assert.NotEmpty(font.Styles));
        TestArtifactWriter.WriteJson(
            nameof(LoadsCurrentRepositoryFontLibrary),
            "font_reference_snapshot.json",
            new
            {
                reference_id = snapshot.ReferenceId,
                library_revision = snapshot.LibraryRevision,
                image = new
                {
                    asset_id = snapshot.Image.AssetId,
                    media_type = snapshot.Image.MediaType,
                    width = snapshot.Image.Width,
                    height = snapshot.Image.Height,
                    byte_length = snapshot.Image.Data.Length
                },
                fonts = snapshot.Fonts
            });
        TestArtifactWriter.WriteBytes(
            nameof(LoadsCurrentRepositoryFontLibrary),
            "font_reference.png",
            snapshot.Image.Data);
    }

    private static FontReferenceImageRequest CreateRequest(string? referenceId = null)
    {
        return new FontReferenceImageRequest(
            "book-test",
            "p0001",
            "zh-Hans",
            referenceId);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the MangaTranslator repository root.");
    }

    private void WriteLibrary(
        int sampleRevision = 3,
        string relativePath = "samples/font_samples_r0003.png",
        IReadOnlyList<FontFixture>? fonts = null,
        IReadOnlyList<CellFixture>? cells = null,
        bool writeSample = true)
    {
        fonts ??= [new FontFixture("F0001", "测试字体")];
        cells ??= [new CellFixture("F0001", "regular")];
        var sampleDirectory = Path.Combine(_rootDirectory, "samples");
        Directory.CreateDirectory(sampleDirectory);
        if (writeSample)
        {
            var imagePath = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            File.WriteAllBytes(imagePath, Convert.FromBase64String(OneByOnePngBase64));
        }

        var catalog = new
        {
            schemaVersion = 1,
            libraryRevision = 3,
            fonts = fonts.Select(font => new
            {
                fontId = font.FontId,
                displayName = font.DisplayName,
                familyName = "TestFamily",
                category = "dialogue",
                tags = FontTags
            }),
            latestSample = new
            {
                relativePath,
                sourceLibraryRevision = sampleRevision,
                fontSizePx = 52,
                widthPx = 1,
                heightPx = 1,
                cells = cells.Select(cell => new
                {
                    fontId = cell.FontId,
                    style = cell.Style,
                    renderMode = "native",
                    sourceStyle = "regular",
                    missingGlyphCount = 0,
                    missingGlyphs = Array.Empty<string>(),
                    label = "native"
                })
            }
        };
        File.WriteAllText(
            Path.Combine(_rootDirectory, "font_catalog.json"),
            JsonSerializer.Serialize(catalog, TestJsonOptions));
    }

    private sealed record FontFixture(string FontId, string DisplayName);

    private sealed record CellFixture(string FontId, string Style);
}
