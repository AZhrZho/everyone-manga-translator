using System.Globalization;
using MangaTranslator.Dialogue;

namespace MangaTranslator.Rendering;

/// <summary>
/// Renderer input for the smallest independently editable unit: one text block.
/// Layout coordinates remain page-relative; the block renderer uses only the box size.
/// </summary>
public sealed record TextBlockRenderData(
    string BlockId,
    string Text,
    DialogueTextLayout Layout);

/// <summary>A page-sized collection of independently renderable text blocks.</summary>
public sealed record PageRenderData(
    string PageId,
    int Width,
    int Height,
    IReadOnlyList<TextBlockRenderData> TextBlocks)
{
    public static PageRenderData FromDialogue(
        DialogueTranslationResult result,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(result);

        var blocks = result.Segments
            .Where(segment => segment.Status == DialogueSegmentStatus.Translated)
            .SelectMany(ExpandSegment)
            .ToArray();
        return new PageRenderData(result.PageId, width, height, blocks);
    }

    public static IEnumerable<TextBlockRenderData> ExpandSegment(DialogueSegment segment)
    {
        var advanced = segment.Layout.Advanced;
        var groups = advanced?.PlacementGroups;
        if (groups is null || groups.Count == 0)
        {
            var normalizedRotation = DialogueBoxRotationCoordinates
                .NormalizeLegacyPageRelativeCenter(
                    segment.Layout.Rotation,
                    segment.Layout.TextBox);
            yield return new TextBlockRenderData(
                segment.SegmentId,
                segment.TranslatedText,
                ReferenceEquals(normalizedRotation, segment.Layout.Rotation)
                    ? segment.Layout
                    : segment.Layout with { Rotation = normalizedRotation });
            yield break;
        }

        var envelopeRotation = DialogueBoxRotationCoordinates
            .NormalizeLegacyPageRelativeCenter(
                segment.Layout.Rotation,
                segment.Layout.TextBox);
        var lines = NormalizeLines(segment.TranslatedText);
        var projections = BuildPlacementProjections(segment, lines, groups);
        ValidateAdvancedProjection(segment, lines, projections);

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            var groupText = string.Join(
                '\n',
                projection.Lines.Select(line => lines[line.GlobalLineIndex]));
            var groupAdvanced = ProjectAdvanced(advanced!, projection);
            var groupLayout = segment.Layout with
            {
                TextBox = projection.Group.TextBox,
                Advanced = groupAdvanced,
                Rotation = ProjectRotation(
                    envelopeRotation,
                    segment.Layout.TextBox,
                    projection.Group.TextBox)
            };

            yield return new TextBlockRenderData(
                $"{segment.SegmentId}:placement:{index + 1:0000}",
                groupText,
                groupLayout);
        }
    }

    private static string[] NormalizeLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static List<PlacementProjection> BuildPlacementProjections(
        DialogueSegment segment,
        string[] lines,
        IReadOnlyList<DialoguePlacementGroup> groups)
    {
        var globalCharacterStarts = new int[lines.Length];
        var characterLengths = new int[lines.Length];
        var globalCharacterStart = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            globalCharacterStarts[lineIndex] = globalCharacterStart;
            characterLengths[lineIndex] = StringInfo.ParseCombiningCharacters(lines[lineIndex]).Length;
            globalCharacterStart += characterLengths[lineIndex];
        }

        var coveredLines = new HashSet<int>();
        var result = new List<PlacementProjection>(groups.Count);
        foreach (var group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(group.LineIndices);
            ArgumentNullException.ThrowIfNull(group.TextBox);
            if (group.LineIndices.Count == 0)
            {
                throw InvalidPlacementGroups(
                    segment,
                    "Every placement group must contain at least one translated line.");
            }

            var projectedLines = new List<LineProjection>(group.LineIndices.Count);
            var localCharacterStart = 0;
            var previousLineIndex = -1;
            for (var localLineIndex = 0;
                 localLineIndex < group.LineIndices.Count;
                 localLineIndex++)
            {
                var lineIndex = group.LineIndices[localLineIndex];
                if (lineIndex < 0 || lineIndex >= lines.Length)
                {
                    throw InvalidPlacementGroups(
                        segment,
                        $"Line index {lineIndex} is outside translated_text.");
                }

                if (localLineIndex > 0 && lineIndex != previousLineIndex + 1)
                {
                    throw InvalidPlacementGroups(
                        segment,
                        "Line indices in each placement group must be ascending and contiguous.");
                }

                if (!coveredLines.Add(lineIndex))
                {
                    throw InvalidPlacementGroups(
                        segment,
                        $"Translated line {lineIndex} is assigned to more than one placement group.");
                }

                projectedLines.Add(new LineProjection(
                    lineIndex,
                    localLineIndex,
                    globalCharacterStarts[lineIndex],
                    localCharacterStart,
                    characterLengths[lineIndex]));
                localCharacterStart += characterLengths[lineIndex];
                previousLineIndex = lineIndex;
            }

            result.Add(new PlacementProjection(group, projectedLines));
        }

        if (coveredLines.Count != lines.Length)
        {
            var missing = Enumerable.Range(0, lines.Length)
                .Where(index => !coveredLines.Contains(index));
            throw InvalidPlacementGroups(
                segment,
                $"Placement groups do not cover translated line(s): {string.Join(", ", missing)}.");
        }

        return result;
    }

    private static void ValidateAdvancedProjection(
        DialogueSegment segment,
        string[] lines,
        List<PlacementProjection> projections)
    {
        var advanced = segment.Layout.Advanced!;
        ArgumentNullException.ThrowIfNull(advanced.LineAdjustments);
        ArgumentNullException.ThrowIfNull(advanced.CharacterAdjustments);
        ArgumentNullException.ThrowIfNull(advanced.RubyAnnotations);

        foreach (var adjustment in advanced.LineAdjustments)
        {
            if (adjustment.LineIndex < 0 || adjustment.LineIndex >= lines.Length)
            {
                throw InvalidPlacementGroups(
                    segment,
                    $"Line adjustment index {adjustment.LineIndex} is outside translated_text.");
            }
        }

        foreach (var adjustment in advanced.CharacterAdjustments)
        {
            var ownerCount = projections.Count(projection =>
                TryMapCharacterIndex(projection, adjustment.CharacterIndex, out _));
            if (ownerCount != 1)
            {
                throw InvalidPlacementGroups(
                    segment,
                    $"Character adjustment index {adjustment.CharacterIndex} does not belong to exactly one placement group.");
            }
        }

        foreach (var ruby in advanced.RubyAnnotations)
        {
            var ownerCount = projections.Count(projection =>
                TryMapCharacterRange(
                    projection,
                    ruby.BaseCharacterIndex,
                    ruby.BaseCharacterLength,
                    out _));
            if (ownerCount != 1)
            {
                throw InvalidPlacementGroups(
                    segment,
                    $"Ruby range [{ruby.BaseCharacterIndex}, {ruby.BaseCharacterIndex + ruby.BaseCharacterLength}) crosses placement groups or is outside translated_text.");
            }
        }
    }

    private static DialogueAdvancedLayout ProjectAdvanced(
        DialogueAdvancedLayout advanced,
        PlacementProjection projection)
    {
        var lineAdjustments = advanced.LineAdjustments
            .Select(adjustment =>
            {
                var line = projection.Lines.FirstOrDefault(item =>
                    item.GlobalLineIndex == adjustment.LineIndex);
                return line is null
                    ? null
                    : adjustment with { LineIndex = line.LocalLineIndex };
            })
            .Where(adjustment => adjustment is not null)
            .Cast<DialogueLineAdjustment>()
            .ToArray();
        var characterAdjustments = advanced.CharacterAdjustments
            .Select(adjustment => TryMapCharacterIndex(
                    projection,
                    adjustment.CharacterIndex,
                    out var localIndex)
                ? adjustment with { CharacterIndex = localIndex }
                : null)
            .Where(adjustment => adjustment is not null)
            .Cast<DialogueCharacterAdjustment>()
            .ToArray();
        var rubyAnnotations = advanced.RubyAnnotations
            .Select(ruby => TryMapCharacterRange(
                    projection,
                    ruby.BaseCharacterIndex,
                    ruby.BaseCharacterLength,
                    out var localIndex)
                ? ruby with { BaseCharacterIndex = localIndex }
                : null)
            .Where(ruby => ruby is not null)
            .Cast<DialogueRubyAnnotation>()
            .ToArray();

        return advanced with
        {
            LineAdjustments = lineAdjustments,
            CharacterAdjustments = characterAdjustments,
            RubyAnnotations = rubyAnnotations,
            PlacementGroups = []
        };
    }

    private static bool TryMapCharacterIndex(
        PlacementProjection projection,
        int globalCharacterIndex,
        out int localCharacterIndex)
    {
        foreach (var line in projection.Lines)
        {
            if (globalCharacterIndex < line.GlobalCharacterStart
                || globalCharacterIndex >= line.GlobalCharacterStart + line.CharacterLength)
            {
                continue;
            }

            localCharacterIndex = line.LocalCharacterStart
                + globalCharacterIndex
                - line.GlobalCharacterStart;
            return true;
        }

        localCharacterIndex = -1;
        return false;
    }

    private static bool TryMapCharacterRange(
        PlacementProjection projection,
        int globalCharacterIndex,
        int characterLength,
        out int localCharacterIndex)
    {
        localCharacterIndex = -1;
        if (characterLength <= 0
            || !TryMapCharacterIndex(projection, globalCharacterIndex, out var localStart)
            || !TryMapCharacterIndex(
                projection,
                globalCharacterIndex + characterLength - 1,
                out var localEnd)
            || localEnd - localStart + 1 != characterLength)
        {
            return false;
        }

        localCharacterIndex = localStart;
        return true;
    }

    private static DialogueBoxRotation? ProjectRotation(
        DialogueBoxRotation? rotation,
        TextLayoutBox envelope,
        TextLayoutBox groupBox)
    {
        return rotation is null
            ? null
            : rotation with
            {
                CenterXPx = rotation.CenterXPx - (groupBox.X0 - envelope.X0),
                CenterYPx = rotation.CenterYPx - (groupBox.Y0 - envelope.Y0)
            };
    }

    private static ArgumentException InvalidPlacementGroups(
        DialogueSegment segment,
        string message) =>
        new(
            $"Segment '{segment.SegmentId}' has invalid placement groups. {message}",
            nameof(segment));

    private sealed record PlacementProjection(
        DialoguePlacementGroup Group,
        IReadOnlyList<LineProjection> Lines);

    private sealed record LineProjection(
        int GlobalLineIndex,
        int LocalLineIndex,
        int GlobalCharacterStart,
        int LocalCharacterStart,
        int CharacterLength);
}

/// <summary>A transparent PNG containing one text block rendered at its layout-box size.</summary>
public sealed record RenderedTextBlock(
    string BlockId,
    int Width,
    int Height,
    ReadOnlyMemory<byte> PngData);

/// <summary>
/// A rendered block plus its editable page position. Moving it never requires text rerendering.
/// </summary>
public sealed record TextBlockPlacement(
    RenderedTextBlock Block,
    float X,
    float Y,
    DialogueBoxRotation? Rotation = null);

public sealed record PageCompositionData(
    string PageId,
    int Width,
    int Height,
    IReadOnlyList<TextBlockPlacement> Placements);

/// <summary>An optional encoded source-page background. It must match the page dimensions.</summary>
public sealed record PageBackground(
    string MediaType,
    ReadOnlyMemory<byte> Data);

public sealed record RenderedPage(
    string PageId,
    string MediaType,
    int Width,
    int Height,
    ReadOnlyMemory<byte> Data);

public interface ITextBlockRenderer
{
    RenderedTextBlock Render(TextBlockRenderData block);
}

public interface IPageComposer
{
    /// <param name="debug">
    /// When true, draws a blue page outline and a red outline around every placed text block.
    /// </param>
    RenderedPage Compose(
        PageCompositionData page,
        PageBackground? background = null,
        bool debug = false);
}
