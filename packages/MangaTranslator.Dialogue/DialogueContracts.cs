using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.LocalDetection;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;

namespace MangaTranslator.Dialogue;

/// <summary>
/// Performs multimodal dialogue recognition, translation and layout planning for exactly one
/// source page per request. The implementation must not build contact sheets or combine pages.
/// </summary>
public interface IMultimodalDialogueTranslator
{
    Task<DialogueTranslationResult> TranslateAsync(
        DialogueTranslationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMultimodalDialogueBatchTranslator
{
    Task<IReadOnlyList<DialogueTranslationResult>> TranslateAsync(
        DialogueTranslationBatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DialogueTranslationBatchRequest(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("pages")] IReadOnlyList<DialogueTranslationRequest> Pages);

/// <summary>
/// Supplies the unmodified, full-resolution source page. Storage and image decoding remain the
/// host's responsibility.
/// </summary>
public interface IPageImageProvider
{
    ValueTask<DialogueImageAsset> GetOriginalPageAsync(
        string pageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies the comparison image that maps visible font numbers to locally available fonts.
/// A host may return the same immutable asset for every page.
/// </summary>
public interface IFontReferenceImageProvider
{
    ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(
        FontReferenceImageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional convenience interface for hosts that expose both required images together.</summary>
public interface IDialogueImageProvider : IPageImageProvider, IFontReferenceImageProvider;

public sealed record DialogueImageAsset(
    [property: JsonPropertyName("asset_id")] string AssetId,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data")] ReadOnlyMemory<byte> Data,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record FontReferenceImageRequest(
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("reference_id")] string? ReferenceId = null);

/// <summary>
/// One immutable font comparison sheet and the exact catalog metadata represented by it. FontId
/// is the stable value the model must return; the image's visual ordering is never an identity.
/// </summary>
public sealed record FontReferenceSnapshot(
    [property: JsonPropertyName("reference_id")] string ReferenceId,
    [property: JsonPropertyName("library_revision")] int LibraryRevision,
    [property: JsonPropertyName("image")] DialogueImageAsset Image,
    [property: JsonPropertyName("fonts")] IReadOnlyList<FontReferenceFont> Fonts);

public sealed record FontReferenceFont(
    [property: JsonPropertyName("font_id")] string FontId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("family_name")] string? FamilyName,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("styles")] IReadOnlyList<FontReferenceStyle> Styles);

/// <summary>
/// Describes one labelled style cell in the comparison image. RenderMode identifies native,
/// synthetic or fallback previews so downstream rendering does not mistake a preview for an
/// installed font face.
/// </summary>
public sealed record FontReferenceStyle(
    [property: JsonPropertyName("style")] string Style,
    [property: JsonPropertyName("render_mode")] string RenderMode,
    [property: JsonPropertyName("source_style")] string SourceStyle,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("missing_glyph_count")] int MissingGlyphCount,
    [property: JsonPropertyName("missing_glyphs")] IReadOnlyList<string> MissingGlyphs);

/// <summary>
/// A page-shaped request. The precheck manifest controls whether the host should call this module;
/// callers normally submit only pages whose IncludeInMask value is true.
/// </summary>
public sealed record DialogueTranslationRequest(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("page")] DialoguePageInput Page,
    [property: JsonPropertyName("book_knowledge")]
    GlobalSemanticMergeResult BookKnowledge,
    [property: JsonPropertyName("page_images")] IPageImageProvider PageImages,
    [property: JsonPropertyName("font_reference_images")]
    IFontReferenceImageProvider FontReferenceImages,
    [property: JsonPropertyName("options")] DialogueTranslationOptions Options,
    /// <summary>
    /// Reader-safe, page-scoped processing guidance produced by Precheck. This is supplemental
    /// evidence for the same multimodal call; it does not replace the full book knowledge.
    /// </summary>
    [property: JsonPropertyName("page_scoped_precheck_guidance")]
    JsonElement PageScopedPrecheckGuidance = default)
{
    [JsonPropertyName("book_page_index")]
    public IReadOnlyList<DialogueBookPageIndexEntry> BookPageIndex { get; init; } = [];
}

public sealed record DialogueDetectedRegion(
    [property: JsonPropertyName("region_id")] string RegionId,
    [property: JsonPropertyName("detection_index")] int DetectionIndex,
    [property: JsonPropertyName("region")] DetectedTextRegion Region);

/// <summary>Canonical full-book mapping from stable page IDs to BookManifest order.</summary>
public sealed record DialogueBookPageIndexEntry(
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("order")] int Order);

public sealed record DialoguePageInput(
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("chapter_id")] string? ChapterId,
    [property: JsonPropertyName("scene_id")] string SceneId,
    [property: JsonPropertyName("page_type")] string PageType,
    [property: JsonPropertyName("include_in_mask")] bool IncludeInMask,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("detected_text_regions")]
    IReadOnlyList<DialogueDetectedRegion> DetectedTextRegions);

public sealed record DialogueTranslationOptions
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "gpt-5.6-sol";

    [JsonPropertyName("target_language")]
    public string TargetLanguage { get; init; } = "zh-Hans";

    [JsonPropertyName("page_image_detail")]
    public ImageDetailLevel PageImageDetail { get; init; } = ImageDetailLevel.Original;

    [JsonPropertyName("font_reference_image_detail")]
    public ImageDetailLevel FontReferenceImageDetail { get; init; } = ImageDetailLevel.Original;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; } = 16_000;

    [JsonPropertyName("max_model_attempts")]
    public int MaxModelAttempts { get; init; } = 3;

    [JsonPropertyName("retry_base_delay")]
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Set false when the selected endpoint does not support prompt caching.</summary>
    [JsonPropertyName("enable_prompt_caching")]
    public bool EnablePromptCaching { get; init; } = true;

    /// <summary>
    /// Optional endpoint-specific override. When null, the host derives a stable short hash from
    /// the model, system prompt, canonical book-level context, and static output schema.
    /// Run IDs and page-scoped manifests are intentionally excluded so equivalent runs can share
    /// the provider's prompt cache.
    /// </summary>
    [JsonPropertyName("prompt_cache_key")]
    public string? PromptCacheKey { get; init; }

    [JsonPropertyName("prompt_cache_retention")]
    public PromptCacheRetention PromptCacheRetention { get; init; } =
        PromptCacheRetention.TwentyFourHours;

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("total_timeout")]
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(15);
}

[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueSegmentKind
{
    Dialogue,
    Narration,
    Caption,
    Sign,
    Title,
    SoundEffect,
    Other
}

[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueSegmentStatus
{
    Translated,
    Unreadable
}

[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueTextOrientation
{
    Horizontal,
    Vertical
}

/// <summary>
/// The natural reading progression for the selected orientation. Horizontal text uses this as
/// its character direction. Vertical manga text flows top-to-bottom inside a column, while this
/// value selects whether columns progress right-to-left or left-to-right.
/// </summary>
[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueReadingDirection
{
    LeftToRight,
    RightToLeft
}

/// <summary>Start and End are interpreted relative to the selected reading direction.</summary>
[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueTextAlignment
{
    Start,
    Center,
    End,
    Justify
}

public sealed record TextLayoutBox(
    [property: JsonPropertyName("x0")] int X0,
    [property: JsonPropertyName("y0")] int Y0,
    [property: JsonPropertyName("x1")] int X1,
    [property: JsonPropertyName("y1")] int Y1);

/// <summary>
/// Complete renderer-facing layout for one logical lettering unit. This is normally one physical
/// text container; a visually connected multi-lobed balloon carrying one continuous semantic
/// paragraph may share one union TextBox and use Advanced placement groups to assign explicit
/// lines or columns to absolute boxes inside its separate lobes. TranslatedText contains explicit
/// '\n' characters; renderers must not infer semantic line breaks by wrapping it differently.
/// </summary>
public sealed record DialogueTextLayout(
    [property: JsonPropertyName("text_box")] TextLayoutBox TextBox,
    [property: JsonPropertyName("orientation")] DialogueTextOrientation Orientation,
    [property: JsonPropertyName("reading_direction")] DialogueReadingDirection ReadingDirection,
    [property: JsonPropertyName("alignment")] DialogueTextAlignment Alignment,
    [property: JsonPropertyName("font_id")] string FontId,
    [property: JsonPropertyName("font_size_px")] float FontSizePx,
    // Kept as a deserialization-compatible legacy constructor slot. It is not part of the
    // renderer schema; font weight is the single source of truth for emphasis.
    [property: JsonIgnore] bool Bold,
    [property: JsonPropertyName("italic")] bool Italic,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("letter_spacing_px")] float LetterSpacingPx,
    [property: JsonPropertyName("advanced")]
    DialogueAdvancedLayout? Advanced = null,
    [property: JsonPropertyName("font_weight")] int FontWeight = 400,
    [property: JsonPropertyName("rotation")] DialogueBoxRotation? Rotation = null,
    [property: JsonPropertyName("stroke")] DialogueTextStroke? Stroke = null)
{
    /// <summary>Numeric font weight is the only effective emphasis value.</summary>
    [JsonIgnore]
    public int EffectiveFontWeight => FontWeight;
}

/// <summary>
/// Box-local rotation. Center coordinates are relative to the text box, and positive angles are
/// clockwise in the rendered page coordinate system.
/// </summary>
public sealed record DialogueBoxRotation(
    [property: JsonPropertyName("center_x_px")] float CenterXPx,
    [property: JsonPropertyName("center_y_px")] float CenterYPx,
    [property: JsonPropertyName("angle_deg")] float AngleDeg);

public static class DialogueBoxRotationCoordinates
{
    /// <summary>
    /// Converts legacy model output that stored the rotation center in page coordinates to the
    /// schema's box-local coordinates. Already-local centers, including intentional pivots outside
    /// the box, are preserved.
    /// </summary>
    public static DialogueBoxRotation? NormalizeLegacyPageRelativeCenter(
        DialogueBoxRotation? rotation,
        TextLayoutBox textBox)
    {
        if (rotation is null)
        {
            return null;
        }

        var width = textBox.X1 - textBox.X0;
        var height = textBox.Y1 - textBox.Y0;
        var isBoxLocal = IsInside(
            rotation.CenterXPx,
            rotation.CenterYPx,
            0,
            0,
            width,
            height);
        var isPageRelativeCenterInsideBox = rotation.CenterXPx >= textBox.X0
            && rotation.CenterXPx <= textBox.X1
            && rotation.CenterYPx >= textBox.Y0
            && rotation.CenterYPx <= textBox.Y1;
        if (!isPageRelativeCenterInsideBox)
        {
            return rotation;
        }

        var convertedCenterX = rotation.CenterXPx - textBox.X0;
        var convertedCenterY = rotation.CenterYPx - textBox.Y0;
        var convertedIsBoxLocal = IsInside(
            convertedCenterX,
            convertedCenterY,
            0,
            0,
            width,
            height);
        return !isBoxLocal && convertedIsBoxLocal
            ? rotation with
            {
                CenterXPx = convertedCenterX,
                CenterYPx = convertedCenterY
            }
            : rotation;
    }

    private static bool IsInside(
        float x,
        float y,
        float x0,
        float y0,
        float x1,
        float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
}

/// <summary>
/// Uniform text outline for the whole block. RoundCorners selects rounded glyph joins and caps.
/// </summary>
public sealed record DialogueTextStroke(
    [property: JsonPropertyName("width_px")] float WidthPx,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("round_corners")] bool RoundCorners);

/// <summary>
/// Sparse overrides for exceptional typography plus optional cavity placement for a connected
/// multi-lobed balloon that carries one continuous semantic paragraph. Placement groups partition
/// translated lines and source regions into absolute page boxes; an empty list keeps the segment in
/// its base TextBox. Character and line indexes are zero-based in rendered reading order.
/// </summary>
public sealed record DialogueAdvancedLayout(
    [property: JsonPropertyName("line_adjustments")]
    IReadOnlyList<DialogueLineAdjustment> LineAdjustments,
    [property: JsonPropertyName("character_adjustments")]
    IReadOnlyList<DialogueCharacterAdjustment> CharacterAdjustments,
    [property: JsonPropertyName("ruby_annotations")]
    IReadOnlyList<DialogueRubyAnnotation> RubyAnnotations)
{
    [JsonPropertyName("placement_groups")]
    public IReadOnlyList<DialoguePlacementGroup> PlacementGroups { get; init; } = [];
}

public sealed record DialoguePlacementGroup(
    [property: JsonPropertyName("line_indices")]
    IReadOnlyList<int> LineIndices,
    [property: JsonPropertyName("source_region_ids")]
    IReadOnlyList<string> SourceRegionIds,
    [property: JsonPropertyName("text_box")]
    TextLayoutBox TextBox);

public sealed record DialogueLineAdjustment(
    [property: JsonPropertyName("line_index")] int LineIndex,
    [property: JsonPropertyName("offset_x_px")] float OffsetXPx,
    [property: JsonPropertyName("offset_y_px")] float OffsetYPx,
    [property: JsonPropertyName("font_size_px")] float? FontSizePx = null,
    [property: JsonPropertyName("color")] string? Color = null);

public sealed record DialogueCharacterAdjustment(
    [property: JsonPropertyName("character_index")] int CharacterIndex,
    [property: JsonPropertyName("offset_x_px")] float OffsetXPx,
    [property: JsonPropertyName("offset_y_px")] float OffsetYPx,
    [property: JsonPropertyName("font_size_px")] float? FontSizePx = null,
    [property: JsonPropertyName("color")] string? Color = null);

[JsonConverter(typeof(DialogueSnakeCaseEnumConverter))]
public enum DialogueRubyPlacement
{
    Auto,
    Before,
    After
}

/// <summary>
/// Optional pronunciation annotation. BaseCharacterIndex and BaseCharacterLength address the
/// translated text after removing explicit newline characters.
/// </summary>
public sealed record DialogueRubyAnnotation(
    [property: JsonPropertyName("base_character_index")] int BaseCharacterIndex,
    [property: JsonPropertyName("base_character_length")] int BaseCharacterLength,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("placement")] DialogueRubyPlacement Placement,
    [property: JsonPropertyName("offset_x_px")] float OffsetXPx = 0,
    [property: JsonPropertyName("offset_y_px")] float OffsetYPx = 0,
    [property: JsonPropertyName("font_size_px")] float? FontSizePx = null,
    [property: JsonPropertyName("color")] string? Color = null);

/// <summary>
/// One logical lettering unit. This is normally one physical visual text container and one render
/// block. A visually connected multi-lobed balloon may remain one segment only when its cited
/// regions form one continuous semantic paragraph and one speaker turn; Advanced placement groups
/// then place its explicit lines or columns back into separate cavity render blocks.
/// Different semantic paragraphs, speaker turns, and disconnected containers require different
/// segments with disjoint SourceRegionIds and independent layouts. SourceRegionIds are the
/// authoritative links to the page's stable DialogueDetectedRegion IDs. A detector region has
/// exactly one segment/ignored owner; repeated text at different locations remains separate.
/// </summary>
public sealed record DialogueSegment(
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("kind")] DialogueSegmentKind Kind,
    [property: JsonPropertyName("status")] DialogueSegmentStatus Status,
    [property: JsonPropertyName("reading_order")] int ReadingOrder,
    [property: JsonPropertyName("source_region_ids")]
    IReadOnlyList<string> SourceRegionIds,
    [property: JsonPropertyName("source_text")] string SourceText,
    [property: JsonPropertyName("translated_text")] string TranslatedText,
    [property: JsonPropertyName("layout")] DialogueTextLayout Layout,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes,
    [property: JsonPropertyName("needs_priority_review")]
    bool NeedsPriorityReview = false);

/// <summary>
/// LocalDetection regions that do not belong to any rendered text segment. Every supplied region
/// ID must be covered by at least one segment or by exactly one ignored-region item, never both.
/// </summary>
public sealed record DialogueIgnoredRegion(
    [property: JsonPropertyName("region_ids")] IReadOnlyList<string> RegionIds,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("evidence")] string Evidence);

public sealed record DialogueTranslationResult(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("segments")]
    IReadOnlyList<DialogueSegment> Segments,
    [property: JsonPropertyName("ignored_regions")]
    IReadOnlyList<DialogueIgnoredRegion> IgnoredRegions,
    [property: JsonPropertyName("warnings")]
    IReadOnlyList<DialogueWarning> Warnings,
    [property: JsonPropertyName("usage")] ModelUsage? Usage,
    [property: JsonPropertyName("metadata")]
    ModelResponseMetadata Metadata,
    [property: JsonPropertyName("needs_priority_review")]
    bool NeedsPriorityReview = false);

public sealed record DialogueWarning(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("segment_id")] string? SegmentId = null,
    [property: JsonPropertyName("region_id")] string? RegionId = null);

public sealed class DialogueTranslationException : Exception
{
    public DialogueTranslationException(string message)
        : base(message)
    {
    }

    public DialogueTranslationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DialogueSnakeCaseEnumConverter : JsonStringEnumConverter
{
    public DialogueSnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower)
    {
    }
}
