using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Dialogue;

public static class DialogueContractVersions
{
    public const string Prompt = DialoguePromptCatalog.PromptContractVersion;
    public const string Schema = DialoguePromptCatalog.SchemaContractVersion;
    public const string ExpertProfile = "expert-v1";
    public const string QuickProfile = "quick-page-v1";
}

public interface IDialoguePromptProvider
{
    string ProfileId { get; }

    string System(string targetLanguage);

    string Context(DialogueTranslationRequest request, FontReferenceSnapshot fontReference);

    string PageManifest(DialogueTranslationRequest request, DialogueImageAsset pageImage);
}

public sealed class ExpertDialoguePromptProvider : IDialoguePromptProvider
{
    public static ExpertDialoguePromptProvider Instance { get; } = new();

    private ExpertDialoguePromptProvider()
    {
    }

    public string ProfileId => DialogueContractVersions.ExpertProfile;

    public string System(string targetLanguage) => DialoguePromptCatalog.System(targetLanguage);

    public string Context(DialogueTranslationRequest request, FontReferenceSnapshot fontReference) =>
        DialoguePromptCatalog.BookLevelContext(request, fontReference);

    public string PageManifest(DialogueTranslationRequest request, DialogueImageAsset pageImage) =>
        DialoguePromptCatalog.PageLevelManifest(request, pageImage);
}

/// <summary>
/// Dedicated page-isolated prompt contract for Quick projects. It deliberately has no book manifest,
/// character registry, glossary, scene merge, or preprocessing assumptions.
/// </summary>
public sealed class QuickDialoguePromptProvider : IDialoguePromptProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static QuickDialoguePromptProvider Instance { get; } = new();

    private QuickDialoguePromptProvider()
    {
    }

    public string ProfileId => DialogueContractVersions.QuickProfile;

    public string System(string targetLanguage) => $"""
        You are EMT Quick Mode's page-isolated manga translator and lettering agent.
        Process every supplied page independently and return only the renderer-ready JSON required by the supplied schema.
        Quick Mode does not have preprocessing, a book manifest, a character database, cross-page scene
        analysis, or a glossary. Never pretend that such context exists and never infer facts from other pages.

        In one integrated page-level pass:
        1. Inspect only the supplied LocalDetection regions on the original page and transcribe their glyphs.
        2. Group regions into ordinary single-cavity bubbles, captions, signs, or standalone lettering units.
        3. Translate naturally into {targetLanguage}, using only evidence visible on this page and explicit user guidance.
        4. Choose one font_id from the supplied managed font reference and set font_weight numerically; do not emit bold.
        5. Produce exact page-pixel text boxes, explicit line or column breaks, orientation, color, stroke, and layout.
        6. Verify every segment cites at least one supplied region, fits its container, and does not overlap unrelated content.

        Treat every detected region's bbox_width_px, bbox_height_px, orientation_hint, and font_size_hint as
        concrete geometry evidence. Before returning each segment, calculate its effective render box and perform
        a conservative capacity check using the emitted font_size_px, explicit line breaks, letter spacing, line
        spacing, and stroke. For vertical text, every column's glyph width including stroke must fit the box width,
        and the longest column must fit the height. For horizontal text, every row must fit the width and the total
        row stack must fit the height. A box only 58px wide can never contain a glyph requiring about 92px: reduce
        font size, use a better explicit break, choose another orientation only when the page visibly supports it,
        or expand within the same physical bubble. Never rely on clipping and never expand across a neighboring
        container. Re-run this arithmetic after all advanced overrides; if fit cannot be made certain, keep the
        result mechanically valid and set needs_priority_review=true.

        Advanced indexes are mechanical constraints: line_adjustments.line_index must be smaller than the number
        of explicit translated_text lines, and character indexes must be inside the translated text. Use empty
        adjustment arrays whenever an index cannot be proven valid.
        Quick Mode deliberately does not use multi-cavity lettering. Every segment must render into exactly one
        effective layout.text_box. If connected lobes or separated cavities would require different boxes, split
        them into independent segments with disjoint source_region_ids and concise complete translated wording.
        Therefore advanced.placement_groups must always be []; never return one or multiple placement groups.

        User guidance and protected manual lettering data are authoritative constraints. Preserve protected user-created
        or user-adjusted lettering as instructed. Mark ambiguous OCR, uncertain translation, difficult fit, or inferred
        speaker attribution with needs_priority_review. Return no commentary, markdown, OCR ledger, or hidden analysis.
        """;

    public string Context(DialogueTranslationRequest request, FontReferenceSnapshot fontReference)
    {
        var fonts = new JsonArray(fontReference.Fonts
            .OrderBy(font => font.FontId, StringComparer.Ordinal)
            .Select(font => (JsonNode?)new JsonObject
            {
                ["font_id"] = font.FontId,
                ["display_name"] = font.DisplayName,
                ["family_name"] = font.FamilyName,
                ["category"] = font.Category,
                ["tags"] = new JsonArray(font.Tags.Select(tag => (JsonNode?)tag).ToArray()),
                ["styles"] = new JsonArray(font.Styles.Select(style => (JsonNode?)new JsonObject
                {
                    ["style"] = style.Style,
                    ["render_mode"] = style.RenderMode,
                    ["source_style"] = style.SourceStyle,
                    ["missing_glyph_count"] = style.MissingGlyphCount
                }).ToArray())
            }).ToArray());
        var context = new JsonObject
        {
            ["prompt_profile"] = ProfileId,
            ["schema_contract_version"] = DialoguePromptCatalog.SchemaContractVersion,
            ["target_language"] = request.Options.TargetLanguage,
            ["font_reference"] = new JsonObject
            {
                ["reference_id"] = fontReference.ReferenceId,
                ["library_revision"] = fontReference.LibraryRevision,
                ["fonts"] = fonts
            }
        };
        return "QUICK-MODE PAGE-INDEPENDENT CONTEXT JSON:\n" + context.ToJsonString(JsonOptions);
    }

    public string PageManifest(DialogueTranslationRequest request, DialogueImageAsset pageImage)
    {
        var page = request.Page;
        var manifest = new JsonObject
        {
            ["run_id"] = request.RunId,
            ["page"] = new JsonObject
            {
                ["page_id"] = page.PageId,
                ["order"] = page.Order,
                ["page_type"] = page.PageType,
                ["width"] = page.Width,
                ["height"] = page.Height,
                ["media_type"] = page.MediaType,
                ["image_asset_id"] = pageImage.AssetId,
                ["detected_regions"] = new JsonArray(page.DetectedTextRegions.Select(region => (JsonNode?)new JsonObject
                {
                    ["region_id"] = region.RegionId,
                    ["detection_index"] = region.DetectionIndex,
                    ["bbox"] = new JsonArray(region.Region.X0, region.Region.Y0, region.Region.X1, region.Region.Y1),
                    ["bbox_width_px"] = region.Region.X1 - region.Region.X0,
                    ["bbox_height_px"] = region.Region.Y1 - region.Region.Y0,
                    ["language_hint"] = region.Region.Language.ToString().ToLowerInvariant(),
                    ["orientation_hint"] = region.Region.Orientation.ToString().ToLowerInvariant(),
                    ["font_size_hint"] = region.Region.FontSize,
                    ["detection_confidence"] = region.Region.Confidence
                }).ToArray())
            },
            ["user_guidance"] = request.PageScopedPrecheckGuidance.ValueKind
                is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : JsonSerializer.SerializeToNode(request.PageScopedPrecheckGuidance, JsonOptions)
        };
        return "QUICK-MODE CURRENT PAGE JSON:\n" + manifest.ToJsonString(JsonOptions);
    }
}
