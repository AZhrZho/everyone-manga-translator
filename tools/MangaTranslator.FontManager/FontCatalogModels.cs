using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace MangaTranslator.FontManager;

public enum FontStyleSlot
{
    Regular,
    Bold,
    Italic
}

public enum SampleRenderMode
{
    Native,
    Synthetic,
    Fallback
}

public sealed class FontCatalog
{
    public int SchemaVersion { get; set; } = 1;

    public int LibraryRevision { get; set; }

    public int NextFontNumber { get; set; } = 1;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<FontEntry> Fonts { get; set; } = [];

    public FontSampleInfo? LatestSample { get; set; }
}

public sealed class FontEntry
{
    public required string FontId { get; set; }

    public required string DisplayName { get; set; }

    public string? FamilyName { get; set; }

    public string? Category { get; set; }

    public List<string> Tags { get; set; } = [];

    public FontSourceInfo Source { get; set; } = new();

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public FontFaceAsset? Regular { get; set; }

    public FontFaceAsset? Bold { get; set; }

    public FontFaceAsset? Italic { get; set; }

    public FontFaceAsset? GetStyle(FontStyleSlot slot)
    {
        return slot switch
        {
            FontStyleSlot.Regular => Regular,
            FontStyleSlot.Bold => Bold,
            FontStyleSlot.Italic => Italic,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
        };
    }
}

public sealed class FontFaceAsset
{
    public required string RelativePath { get; set; }

    public required string OriginalFileName { get; set; }

    public int FaceIndex { get; set; }

    public required string Sha256 { get; set; }

    public string? DetectedFamilyName { get; set; }

    public int? DetectedWeight { get; set; }

    public string? DetectedSlant { get; set; }
}

public sealed class FontSourceInfo
{
    public string Kind { get; set; } = "file";

    public string? Reference { get; set; }

    public string? LicenseNote { get; set; }

    public string? LicenseRelativePath { get; set; }

    public string? LicenseSha256 { get; set; }
}

public sealed class FontSampleInfo
{
    public required string RelativePath { get; set; }

    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int SourceLibraryRevision { get; set; }

    public int FontSizePx { get; set; }

    public required List<string> SampleLines { get; set; }

    public int WidthPx { get; set; }

    public int HeightPx { get; set; }

    public List<FontSampleCell> Cells { get; set; } = [];
}

public sealed class FontSampleCell
{
    public required string FontId { get; set; }

    public FontStyleSlot Style { get; set; }

    public SampleRenderMode RenderMode { get; set; }

    public FontStyleSlot SourceStyle { get; set; }

    public int MissingGlyphCount { get; set; }

    public List<string> MissingGlyphs { get; set; } = [];

    public required string Label { get; set; }
}

public sealed class FontStyleInput
{
    public required string SourcePath { get; init; }

    public int FaceIndex { get; init; }
}

public sealed class AddFontRequest
{
    public string? FontId { get; init; }

    public string? DisplayName { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string SourceKind { get; init; } = "file";

    public string? SourceReference { get; init; }

    public string? LicenseNote { get; init; }

    public string? LicenseFilePath { get; init; }

    public required FontStyleInput Regular { get; init; }

    public FontStyleInput? Bold { get; init; }

    public FontStyleInput? Italic { get; init; }
}

public sealed class SampleOptions
{
    public int FontSizePx { get; init; } = 52;

    public IReadOnlyList<string> SampleLines { get; init; } =
    [
        "漫画对白「你好，世界！」",
        "かなカナ ABC 123"
    ];

    public string? OutputFileName { get; init; }
}

public sealed class ValidationIssue
{
    public required string Severity { get; init; }

    public required string Message { get; init; }
}

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = [];

    public bool IsValid => Issues.All(issue => !string.Equals(
        issue.Severity,
        "error",
        StringComparison.OrdinalIgnoreCase));
}

internal static class FontCatalogJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
