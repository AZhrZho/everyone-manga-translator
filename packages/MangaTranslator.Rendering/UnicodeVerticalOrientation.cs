using System.Text;

namespace MangaTranslator.Rendering;

/// <summary>
/// Data-driven fallback for Unicode Vertical_Orientation when the low-level Skia text API cannot
/// apply OpenType vert/vrt2 substitutions. R and Tr rotate clockwise; U remains upright; Tu stays
/// upright but is optically centered in its vertical cell by the renderer.
/// </summary>
internal static class UnicodeVerticalOrientation
{
    // Derived from Unicode 17.0.0 VerticalOrientation.txt (2025-07-24), UAX #50.
    // https://www.unicode.org/Public/17.0.0/ucd/VerticalOrientation.txt
    // Adjacent U and Tu ranges are merged because both remain upright in the rotation decision.
    private static readonly (int Start, int End)[] UprightRanges =
    [
        (0xA7, 0xA7),
        (0xA9, 0xA9),
        (0xAE, 0xAE),
        (0xB1, 0xB1),
        (0xBC, 0xBE),
        (0xD7, 0xD7),
        (0xF7, 0xF7),
        (0x2EA, 0x2EB),
        (0x1100, 0x11FF),
        (0x1401, 0x167F),
        (0x18B0, 0x18FF),
        (0x2016, 0x2016),
        (0x2020, 0x2021),
        (0x2030, 0x2031),
        (0x203B, 0x203C),
        (0x2042, 0x2042),
        (0x2047, 0x2049),
        (0x2051, 0x2051),
        (0x2065, 0x2065),
        (0x20DD, 0x20E0),
        (0x20E2, 0x20E4),
        (0x2100, 0x2101),
        (0x2103, 0x2109),
        (0x210F, 0x210F),
        (0x2113, 0x2114),
        (0x2116, 0x2117),
        (0x211E, 0x2123),
        (0x2125, 0x2125),
        (0x2127, 0x2127),
        (0x2129, 0x2129),
        (0x212E, 0x212E),
        (0x2135, 0x213F),
        (0x2145, 0x214A),
        (0x214C, 0x214D),
        (0x214F, 0x2189),
        (0x218C, 0x218F),
        (0x221E, 0x221E),
        (0x2234, 0x2235),
        (0x2300, 0x2307),
        (0x230C, 0x231F),
        (0x2324, 0x2328),
        (0x232B, 0x232B),
        (0x237D, 0x239A),
        (0x23BE, 0x23CD),
        (0x23CF, 0x23CF),
        (0x23D1, 0x23DB),
        (0x23E2, 0x2422),
        (0x2424, 0x24FF),
        (0x25A0, 0x2619),
        (0x2620, 0x2767),
        (0x2776, 0x2793),
        (0x2B12, 0x2B2F),
        (0x2B50, 0x2B59),
        (0x2B97, 0x2B97),
        (0x2BB8, 0x2BD1),
        (0x2BD3, 0x2BEB),
        (0x2BF0, 0x2BFF),
        (0x2E50, 0x2E51),
        (0x2E80, 0x3007),
        (0x3012, 0x3013),
        (0x3020, 0x302F),
        (0x3031, 0x309F),
        (0x30A1, 0x30FB),
        (0x30FD, 0xA4CF),
        (0xA960, 0xA97F),
        (0xAC00, 0xD7FF),
        (0xE000, 0xFAFF),
        (0xFE10, 0xFE1F),
        (0xFE30, 0xFE48),
        (0xFE50, 0xFE57),
        (0xFE5F, 0xFE62),
        (0xFE67, 0xFE6F),
        (0xFF01, 0xFF07),
        (0xFF0A, 0xFF0C),
        (0xFF0E, 0xFF19),
        (0xFF1F, 0xFF3A),
        (0xFF3C, 0xFF3C),
        (0xFF3E, 0xFF3E),
        (0xFF40, 0xFF5A),
        (0xFFE0, 0xFFE2),
        (0xFFE4, 0xFFE7),
        (0xFFF0, 0xFFF8),
        (0xFFFC, 0xFFFD),
        (0x10980, 0x1099F),
        (0x11580, 0x115FF),
        (0x11A00, 0x11ABF),
        (0x13000, 0x1467F),
        (0x16FE0, 0x18DFF),
        (0x1AFF0, 0x1B2FF),
        (0x1CEC0, 0x1CFCF),
        (0x1D000, 0x1D1FF),
        (0x1D2E0, 0x1D37F),
        (0x1D800, 0x1DAAF),
        (0x1F000, 0x1F7FF),
        (0x1F900, 0x1FAFF),
        (0x20000, 0x2FFFD),
        (0x30000, 0x3FFFD),
        (0xF0000, 0xFFFFD),
        (0x100000, 0x10FFFD)
    ];

    private static readonly (int Start, int End)[] TransformUprightRanges =
    [
        (0x3001, 0x3002),
        (0x3041, 0x3041),
        (0x3043, 0x3043),
        (0x3045, 0x3045),
        (0x3047, 0x3047),
        (0x3049, 0x3049),
        (0x3063, 0x3063),
        (0x3083, 0x3083),
        (0x3085, 0x3085),
        (0x3087, 0x3087),
        (0x308E, 0x308E),
        (0x3095, 0x3096),
        (0x309B, 0x309C),
        (0x30A1, 0x30A1),
        (0x30A3, 0x30A3),
        (0x30A5, 0x30A5),
        (0x30A7, 0x30A7),
        (0x30A9, 0x30A9),
        (0x30C3, 0x30C3),
        (0x30E3, 0x30E3),
        (0x30E5, 0x30E5),
        (0x30E7, 0x30E7),
        (0x30EE, 0x30EE),
        (0x30F5, 0x30F6),
        (0x3127, 0x3127),
        (0x31B4, 0x31B7),
        (0x31BB, 0x31BB),
        (0x31F0, 0x31FF),
        (0x32FF, 0x3357),
        (0x337B, 0x337F),
        (0xFE50, 0xFE52),
        (0xFF01, 0xFF01),
        (0xFF0C, 0xFF0C),
        (0xFF0E, 0xFF0E),
        (0xFF1F, 0xFF1F),
        (0x1B132, 0x1B132),
        (0x1B150, 0x1B152),
        (0x1B155, 0x1B155),
        (0x1B164, 0x1B167),
        (0x1F200, 0x1F201)
    ];

    public static VerticalGlyphOrientation Classify(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (!Rune.TryGetRuneAt(text, 0, out var rune))
        {
            return VerticalGlyphOrientation.Upright;
        }

        if (Contains(TransformUprightRanges, rune.Value))
        {
            return VerticalGlyphOrientation.TransformUpright;
        }

        return Contains(UprightRanges, rune.Value)
            ? VerticalGlyphOrientation.Upright
            : VerticalGlyphOrientation.Rotated;
    }

    public static VerticalGlyphResolution ResolveGlyph(
        string text,
        Func<string, bool> containsGlyph)
    {
        ArgumentNullException.ThrowIfNull(containsGlyph);
        var orientation = Classify(text);
        if (orientation == VerticalGlyphOrientation.TransformUpright
            && GetPresentationForm(text) is { } presentation
            && containsGlyph(presentation))
        {
            return new VerticalGlyphResolution(
                presentation,
                VerticalGlyphOrientation.PresentationForm);
        }

        return new VerticalGlyphResolution(text, orientation);
    }

    /// <summary>
    /// Returns a standardized Unicode vertical presentation form when one exists for common
    /// punctuation. The caller must still verify that the selected font contains the glyph.
    /// </summary>
    public static string? GetPresentationForm(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (!Rune.TryGetRuneAt(text, 0, out var rune)
            || rune.Utf16SequenceLength != text.Length)
        {
            return null;
        }

        return rune.Value switch
        {
            0xFF01 => "\uFE15",
            0xFF0C => "\uFE10",
            0xFF1F => "\uFE16",
            0x3001 => "\uFE11",
            0x3002 => "\uFE12",
            _ => null
        };
    }

    private static bool Contains((int Start, int End)[] ranges, int value)
    {
        var low = 0;
        var high = ranges.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var range = ranges[middle];
            if (value < range.Start)
            {
                high = middle - 1;
            }
            else if (value > range.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}

internal enum VerticalGlyphOrientation
{
    Upright,
    Rotated,
    TransformUpright,
    PresentationForm
}

internal readonly record struct VerticalGlyphResolution(
    string Text,
    VerticalGlyphOrientation Orientation);
