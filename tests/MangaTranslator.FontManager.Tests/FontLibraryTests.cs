using System.Security.Cryptography;
using System.Text.Json;

namespace MangaTranslator.FontManager.Tests;

public sealed class FontLibraryTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "manga-translator-font-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void InitializeCreatesPortableStoreLayout()
    {
        var library = new FontLibrary(rootDirectory);

        library.Initialize();

        Assert.True(File.Exists(library.CatalogPath));
        Assert.True(Directory.Exists(library.FontDirectory));
        Assert.True(Directory.Exists(library.SampleDirectory));
        var catalog = library.ReadCatalog();
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(0, catalog.LibraryRevision);
        Assert.Empty(catalog.Fonts);
    }

    [Fact]
    public void AddCopiesFontAndRemoveDoesNotTouchSource()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var sourceHash = ComputeHash(source);
        Directory.CreateDirectory(rootDirectory);
        var sourceLicense = Path.Combine(rootDirectory, "source-license.txt");
        File.WriteAllText(sourceLicense, "Test license");
        var library = new FontLibrary(rootDirectory);
        var entry = library.Add(new AddFontRequest
        {
            DisplayName = "Test Font",
            Category = "test",
            Tags = ["dialogue"],
            SourceKind = "test",
            LicenseFilePath = sourceLicense,
            Regular = new FontStyleInput { SourcePath = source }
        });

        Assert.Equal("F0001", entry.FontId);
        var managedPath = library.ResolveManagedPath(entry.Regular!.RelativePath);
        Assert.True(File.Exists(managedPath));
        Assert.Equal(sourceHash, ComputeHash(managedPath));
        Assert.Equal(sourceHash, ComputeHash(source));
        Assert.NotNull(entry.Source.LicenseRelativePath);
        Assert.True(File.Exists(
            library.ResolveManagedPath(entry.Source.LicenseRelativePath!)));

        Assert.True(library.Remove(entry.FontId));
        Assert.False(Directory.Exists(Path.Combine(library.FontDirectory, entry.FontId)));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(sourceLicense));
        Assert.False(library.Remove(entry.FontId));
    }

    [Fact]
    public void IDsAreMonotonicAfterRemoval()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        var first = library.Add(new AddFontRequest
        {
            Regular = new FontStyleInput { SourcePath = source }
        });
        Assert.True(library.Remove(first.FontId));
        var second = library.Add(new AddFontRequest
        {
            Regular = new FontStyleInput { SourcePath = source }
        });

        Assert.Equal("F0002", second.FontId);
    }

    [Fact]
    public void AddPreservesPreExistingOrphanDirectory()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        library.Initialize();
        var orphanDirectory = Path.Combine(library.FontDirectory, "F0001");
        Directory.CreateDirectory(orphanDirectory);
        var sentinelPath = Path.Combine(orphanDirectory, "sentinel.txt");
        File.WriteAllText(sentinelPath, "preserve me");

        var exception = Assert.Throws<FontLibraryException>(() => library.Add(new AddFontRequest
        {
            Regular = new FontStyleInput { SourcePath = source }
        }));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinelPath));
        Assert.Empty(library.ReadCatalog().Fonts);
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("CON")]
    [InlineData("COM1")]
    public void AddRejectsReservedDeviceNames(string fontId)
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        var exception = Assert.Throws<FontLibraryException>(() => library.Add(new AddFontRequest
        {
            FontId = fontId,
            Regular = new FontStyleInput { SourcePath = source }
        }));

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(library.ReadCatalog().Fonts);
    }

    [Fact]
    public void SampleRecordsFixedSizeAndSyntheticFallbacks()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        library.Add(new AddFontRequest
        {
            DisplayName = "Test Font",
            Regular = new FontStyleInput { SourcePath = source }
        });

        var sample = library.GenerateSample(new SampleOptions
        {
            FontSizePx = 40,
            SampleLines = ["测试 ABC", "かな 123"]
        });

        Assert.True(File.Exists(library.ResolveManagedPath(sample.RelativePath)));
        Assert.Equal(40, sample.FontSizePx);
        Assert.Collection(
            sample.Cells,
            cell =>
            {
                Assert.Equal(FontStyleSlot.Regular, cell.Style);
                Assert.Equal(SampleRenderMode.Native, cell.RenderMode);
            },
            cell =>
            {
                Assert.Equal(FontStyleSlot.Bold, cell.Style);
                Assert.Equal(SampleRenderMode.Synthetic, cell.RenderMode);
            },
            cell =>
            {
                Assert.Equal(FontStyleSlot.Italic, cell.Style);
                Assert.Equal(SampleRenderMode.Synthetic, cell.RenderMode);
            });
        var persisted = library.ReadCatalog();
        Assert.NotNull(persisted.LatestSample);
        Assert.Equal(sample.RelativePath, persisted.LatestSample!.RelativePath);
        Assert.Equal(
            sample.SourceLibraryRevision,
            persisted.LatestSample.SourceLibraryRevision);

        using var stream = File.OpenRead(library.ResolveManagedPath(sample.RelativePath));
        Assert.Equal(0x89, stream.ReadByte());
        Assert.Equal('P', stream.ReadByte());
        Assert.Equal('N', stream.ReadByte());
        Assert.Equal('G', stream.ReadByte());
    }

    [Fact]
    public void CatalogRoundTripsAsReadableJson()
    {
        var library = new FontLibrary(rootDirectory);
        library.Initialize();
        var json = File.ReadAllText(library.CatalogPath);
        var bytes = File.ReadAllBytes(library.CatalogPath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("fonts", out _));
        Assert.False(bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF);
    }

    [Fact]
    public void SampleRejectsOutputPathTraversal()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        library.Add(new AddFontRequest
        {
            Regular = new FontStyleInput { SourcePath = source }
        });

        Assert.Throws<FontLibraryException>(() => library.GenerateSample(new SampleOptions
        {
            OutputFileName = "..\\outside.png"
        }));
        Assert.False(File.Exists(Path.Combine(rootDirectory, "outside.png")));
    }

    [Fact]
    public void SampleRejectsContentThatWouldBeClipped()
    {
        var source = FindTestFont();
        if (source is null)
        {
            return;
        }

        var library = new FontLibrary(rootDirectory);
        library.Add(new AddFontRequest
        {
            Regular = new FontStyleInput { SourcePath = source }
        });

        var exception = Assert.Throws<FontLibraryException>(() => library.GenerateSample(
            new SampleOptions
            {
                FontSizePx = 256,
                SampleLines = ["This text cannot fit in one fixed comparison cell"]
            }));
        Assert.Contains("too wide", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(library.SampleDirectory, "*.png"));
    }

    private static string? FindTestFont()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                "arial.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
