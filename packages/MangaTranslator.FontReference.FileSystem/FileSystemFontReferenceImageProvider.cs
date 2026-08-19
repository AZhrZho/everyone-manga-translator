using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.Dialogue;

namespace MangaTranslator.FontReference.FileSystem;

/// <summary>
/// Reads one immutable font-reference snapshot from a FontManager-compatible directory.
/// The provider never creates, changes, or removes catalog, sample, or font files.
/// </summary>
public sealed class FileSystemFontReferenceImageProvider : IFontReferenceImageProvider
{
    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _loadLock = new();
    private readonly string _catalogPath;
    private readonly string _rootDirectory;
    private FontReferenceSnapshot? _snapshot;

    public FileSystemFontReferenceImageProvider(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "The font-reference library root cannot be empty.",
                nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _catalogPath = Path.Combine(_rootDirectory, "font_catalog.json");
    }

    public string RootDirectory => _rootDirectory;

    public string CatalogPath => _catalogPath;

    public ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(
        FontReferenceImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        FontReferenceSnapshot? snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null)
        {
            lock (_loadLock)
            {
                snapshot = _snapshot;
                if (snapshot is null)
                {
                    snapshot = LoadSnapshot();
                    Volatile.Write(ref _snapshot, snapshot);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureRequestedReference(request.ReferenceId, snapshot.ReferenceId);
        return ValueTask.FromResult(snapshot);
    }

    private FontReferenceSnapshot LoadSnapshot()
    {
        if (!File.Exists(_catalogPath))
        {
            throw new FontReferenceProviderException(
                $"Font catalog does not exist: {_catalogPath}");
        }

        FontCatalogDocument catalog;
        try
        {
            using FileStream stream = File.OpenRead(_catalogPath);
            catalog = JsonSerializer.Deserialize<FontCatalogDocument>(stream, JsonOptions)
                ?? throw new FontReferenceProviderException(
                    $"Font catalog is empty: {_catalogPath}");
        }
        catch (FontReferenceProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new FontReferenceProviderException(
                $"Could not read font catalog: {_catalogPath}",
                exception);
        }

        ValidateCatalog(catalog);
        FontSampleDocument sample = catalog.LatestSample!;
        string samplePath = ResolveSamplePath(sample.RelativePath!);
        if (!File.Exists(samplePath))
        {
            throw new FontReferenceProviderException(
                $"Font reference sample image does not exist: {samplePath}");
        }

        byte[] imageBytes;
        try
        {
            imageBytes = File.ReadAllBytes(samplePath);
        }
        catch (IOException exception)
        {
            throw new FontReferenceProviderException(
                $"Could not read font reference sample image: {samplePath}",
                exception);
        }

        (int imageWidth, int imageHeight) = ReadPngDimensions(imageBytes, samplePath);
        if (imageWidth != sample.WidthPx || imageHeight != sample.HeightPx)
        {
            throw new FontReferenceProviderException(
                $"Font reference PNG is {imageWidth}x{imageHeight}, but the catalog declares "
                + $"{sample.WidthPx}x{sample.HeightPx}.");
        }

        ReadOnlyCollection<FontReferenceFont> fonts = MapFonts(
            catalog.Fonts!,
            sample.Cells!);
        string referenceId = Path.GetFileNameWithoutExtension(samplePath);
        if (string.IsNullOrWhiteSpace(referenceId))
        {
            throw new FontReferenceProviderException(
                "The font reference sample file name cannot produce a reference ID.");
        }

        DialogueImageAsset image = new(
            referenceId,
            "image/png",
            imageBytes,
            imageWidth,
            imageHeight);
        return new FontReferenceSnapshot(
            referenceId,
            catalog.LibraryRevision,
            image,
            fonts);
    }

    private static void ValidateRequest(FontReferenceImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BookId)
            || string.IsNullOrWhiteSpace(request.PageId)
            || string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            throw new ArgumentException(
                "BookId, PageId, and TargetLanguage are required.",
                nameof(request));
        }
    }

    private static void EnsureRequestedReference(string? requested, string actual)
    {
        if (!string.IsNullOrWhiteSpace(requested)
            && !StringComparer.Ordinal.Equals(requested.Trim(), actual))
        {
            throw new FontReferenceProviderException(
                $"Requested font reference '{requested.Trim()}' is not available; "
                + $"the loaded snapshot is '{actual}'.");
        }
    }

    private static void ValidateCatalog(FontCatalogDocument catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new FontReferenceProviderException(
                $"Unsupported font catalog schema version {catalog.SchemaVersion}; expected 1.");
        }

        if (catalog.LibraryRevision < 0)
        {
            throw new FontReferenceProviderException(
                "Font catalog libraryRevision cannot be negative.");
        }

        if (catalog.Fonts is not { Count: > 0 })
        {
            throw new FontReferenceProviderException(
                "Font catalog must contain at least one font.");
        }

        string[] emptyFontIds = catalog.Fonts
            .Where(font => string.IsNullOrWhiteSpace(font.FontId))
            .Select(font => font.FontId ?? "(null)")
            .ToArray();
        if (emptyFontIds.Length > 0)
        {
            throw new FontReferenceProviderException(
                "Font catalog contains an empty fontId.");
        }

        string[] duplicateFontIds = catalog.Fonts
            .GroupBy(font => font.FontId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateFontIds.Length > 0)
        {
            throw new FontReferenceProviderException(
                "Font catalog contains duplicate font IDs: "
                + string.Join(", ", duplicateFontIds));
        }

        if (catalog.Fonts.Any(font => string.IsNullOrWhiteSpace(font.DisplayName)))
        {
            throw new FontReferenceProviderException(
                "Every font catalog entry must have a displayName.");
        }

        FontSampleDocument? sample = catalog.LatestSample;
        if (sample is null)
        {
            throw new FontReferenceProviderException(
                "Font catalog has no latestSample. Generate a sample sheet before using it.");
        }

        if (sample.SourceLibraryRevision != catalog.LibraryRevision)
        {
            throw new FontReferenceProviderException(
                $"Font reference sample revision {sample.SourceLibraryRevision} is stale; "
                + $"catalog revision is {catalog.LibraryRevision}.");
        }

        if (string.IsNullOrWhiteSpace(sample.RelativePath)
            || sample.WidthPx <= 0
            || sample.HeightPx <= 0)
        {
            throw new FontReferenceProviderException(
                "Font catalog latestSample has invalid path or dimensions.");
        }

        if (sample.Cells is not { Count: > 0 })
        {
            throw new FontReferenceProviderException(
                "Font catalog latestSample must contain style cells.");
        }
    }

    private static ReadOnlyCollection<FontReferenceFont> MapFonts(
        IReadOnlyList<FontEntryDocument> catalogFonts,
        IReadOnlyList<FontSampleCellDocument> cells)
    {
        Dictionary<string, FontEntryDocument> fontById = catalogFonts.ToDictionary(
            font => font.FontId!.Trim(),
            StringComparer.OrdinalIgnoreCase);
        foreach (FontSampleCellDocument cell in cells)
        {
            if (string.IsNullOrWhiteSpace(cell.FontId)
                || !fontById.ContainsKey(cell.FontId.Trim()))
            {
                throw new FontReferenceProviderException(
                    $"Font reference sample contains unknown font ID '{cell.FontId}'.");
            }

            if (string.IsNullOrWhiteSpace(cell.Style)
                || string.IsNullOrWhiteSpace(cell.RenderMode)
                || string.IsNullOrWhiteSpace(cell.SourceStyle)
                || string.IsNullOrWhiteSpace(cell.Label)
                || cell.MissingGlyphCount < 0
                || cell.MissingGlyphs is null
                || cell.MissingGlyphCount != cell.MissingGlyphs.Count)
            {
                throw new FontReferenceProviderException(
                    $"Font reference sample has an invalid style cell for '{cell.FontId}'.");
            }
        }

        return new ReadOnlyCollection<FontReferenceFont>(
            catalogFonts.Select(font =>
            {
                string fontId = font.FontId!.Trim();
                FontSampleCellDocument[] fontCells = cells
                    .Where(cell => StringComparer.OrdinalIgnoreCase.Equals(
                        cell.FontId!.Trim(),
                        fontId))
                    .ToArray();
                if (fontCells.Length == 0)
                {
                    throw new FontReferenceProviderException(
                        $"Font '{fontId}' has no cells in the current reference sample.");
                }

                string[] duplicateStyles = fontCells
                    .GroupBy(cell => cell.Style!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicateStyles.Length > 0)
                {
                    throw new FontReferenceProviderException(
                        $"Font '{fontId}' has duplicate sample styles: "
                        + string.Join(", ", duplicateStyles));
                }

                ReadOnlyCollection<FontReferenceStyle> styles =
                    new ReadOnlyCollection<FontReferenceStyle>(
                        fontCells.Select(cell => new FontReferenceStyle(
                            cell.Style!.Trim(),
                            cell.RenderMode!.Trim(),
                            cell.SourceStyle!.Trim(),
                            cell.Label!.Trim(),
                            cell.MissingGlyphCount,
                            new ReadOnlyCollection<string>(cell.MissingGlyphs!.ToArray())))
                            .ToArray());
                ReadOnlyCollection<string> tags = new(
                    (font.Tags ?? [])
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .Select(tag => tag.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray());
                return new FontReferenceFont(
                    fontId,
                    font.DisplayName!.Trim(),
                    NormalizeOptional(font.FamilyName),
                    NormalizeOptional(font.Category),
                    tags,
                    styles);
            }).ToArray());
    }

    private string ResolveSamplePath(string relativePath)
    {
        string samplesDirectory = Path.GetFullPath(Path.Combine(_rootDirectory, "samples"));
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));
        EnsureUnderDirectory(fullPath, samplesDirectory);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(fullPath), ".png"))
        {
            throw new FontReferenceProviderException(
                $"Font reference sample must be a PNG: {relativePath}");
        }

        return fullPath;
    }

    private static void EnsureUnderDirectory(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = Path.TrimEndingDirectorySeparator(directory)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison))
        {
            throw new FontReferenceProviderException(
                $"Font reference sample path escapes the managed samples directory: {path}");
        }
    }

    private static (int Width, int Height) ReadPngDimensions(
        ReadOnlySpan<byte> bytes,
        string path)
    {
        if (bytes.Length < 24
            || !bytes[..PngSignature.Length].SequenceEqual(PngSignature)
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new FontReferenceProviderException(
                $"Font reference sample is not a valid PNG header: {path}");
        }

        int headerLength = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(8, 4));
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (headerLength < 13 || width <= 0 || height <= 0)
        {
            throw new FontReferenceProviderException(
                $"Font reference sample has invalid PNG dimensions: {path}");
        }

        return (width, height);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class FontCatalogDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("libraryRevision")]
        public int LibraryRevision { get; init; }

        [JsonPropertyName("fonts")]
        public List<FontEntryDocument>? Fonts { get; init; }

        [JsonPropertyName("latestSample")]
        public FontSampleDocument? LatestSample { get; init; }
    }

    private sealed class FontEntryDocument
    {
        [JsonPropertyName("fontId")]
        public string? FontId { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("familyName")]
        public string? FamilyName { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; init; }
    }

    private sealed class FontSampleDocument
    {
        [JsonPropertyName("relativePath")]
        public string? RelativePath { get; init; }

        [JsonPropertyName("sourceLibraryRevision")]
        public int SourceLibraryRevision { get; init; }

        [JsonPropertyName("widthPx")]
        public int WidthPx { get; init; }

        [JsonPropertyName("heightPx")]
        public int HeightPx { get; init; }

        [JsonPropertyName("cells")]
        public List<FontSampleCellDocument>? Cells { get; init; }
    }

    private sealed class FontSampleCellDocument
    {
        [JsonPropertyName("fontId")]
        public string? FontId { get; init; }

        [JsonPropertyName("style")]
        public string? Style { get; init; }

        [JsonPropertyName("renderMode")]
        public string? RenderMode { get; init; }

        [JsonPropertyName("sourceStyle")]
        public string? SourceStyle { get; init; }

        [JsonPropertyName("missingGlyphCount")]
        public int MissingGlyphCount { get; init; }

        [JsonPropertyName("missingGlyphs")]
        public List<string>? MissingGlyphs { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }
}

public sealed class FontReferenceProviderException : Exception
{
    public FontReferenceProviderException(string message)
        : base(message)
    {
    }

    public FontReferenceProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
