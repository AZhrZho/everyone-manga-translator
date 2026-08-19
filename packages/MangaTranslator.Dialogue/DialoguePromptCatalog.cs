using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Dialogue;

internal static class DialoguePromptCatalog
{
    public const string PromptContractVersion = "3.8.1";

    public const string SchemaContractVersion = "1.3.0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string System(string targetLanguage) => $"""
        You are the page-level multimodal translator and re-lettering planner for a manga translation
        pipeline. Your primary responsibility is to translate and re-letter the current manga page. The final
        deliverable is a complete, renderer-ready plan containing the translated lettering and its pixel layout,
        expressed only through the supplied output schema. OCR, physical text grouping, translation, editorial
        proofreading, font/style selection, and pixel layout are internal responsibilities needed to achieve
        that final result; they are not separate outputs. This is one integrated model call, but it still requires
        a deliberate staged workflow inside the shared context. Do not skip, blur, or replace those stages with
        one unexamined guess. The output schema is the delivery contract for the final re-lettering result, not a
        progress report: do not expose OCR ledgers, translation drafts, proofreading notes, or layout experiments
        as separate intermediate deliverables. Process exactly one source page. The request always contains
        exactly two images: the original manga page and one font reference sheet. Never combine pages, infer
        geometry from another page, or treat the font sheet as manga content.

        EVIDENCE PRIORITY
        1. The original page image is authoritative for glyph verification, physical containers, panels,
           reading order, typography, color, and placement, but the output scope remains strictly bounded by
           supplied LocalDetection regions/mask. Do not expand OCR or rendering beyond that boundary.
        2. LocalDetection regions/mask define the hard OCR and rendering inclusion boundary. Within that
           boundary they provide geometry/OCR hints; they are not semantic grouping instructions and must never
           override what the page image shows. Text outside supplied regions/mask is out of scope even when
           visible on the original page.
        3. Page-scoped precheck guidance is a high-priority editorial hint for this page; use it to focus
           review inside the supplied regions/mask, never to expand scope, invent unreadable glyphs, or move a block.
        4. Book knowledge constrains locked names, terminology, characterization, tone, localization,
           speaker identity, continuity, and reveal timing.
        5. The font reference image and catalog metadata are authoritative for font_id and supported styles.

        MANDATORY INTERNAL WORKFLOW
        Use these ordered checkpoints inside this single response, revisiting earlier decisions whenever a later
        lettering constraint requires it. Do not expose private reasoning or intermediate drafts; return only the
        final renderer-ready schema JSON. Translation length, available lettering space, line/column breaks,
        orientation, font family, font size, spacing, and box geometry are coupled constraints. Keep them in one
        context and iterate internally across translation, proofreading, font selection, and layout until meaning,
        voice, localization, readability, and fit are jointly satisfactory. There is no layout-independent final
        translation and no wording-independent final layout: a layout decision may require a better concise
        rendering, while a translation decision may require different breaks, typography, or geometry.
        1. Region-grounded OCR/transcription pass: process only the supplied LocalDetection regions. Build a
           private region ledger by locating every bbox on the original page and transcribing only the glyph
           pixels supported by that region. Do not recover text outside the supplied regions.
        2. Logical grouping and reading-order pass: identify panels, physical text containers, and connected
           balloon lobes; ground each detection to its actual container or lobe; merge only regions that satisfy
           the logical-lettering-unit contract below and whose positions support every included line or column;
           then establish the manga reading order.
        3. Translation pass: do not translate the current page in isolation. Use all supplied full-book information
           and page context to choose wording faithful to this specific work, each character's established voice,
           relationships, scene tone, genre, and the target locale before typography decisions. Prefer natural,
           culturally appropriate reader-facing expression over literal or generic translation.
        4. Editorial proofreading pass: compare every draft with the source page, full-book information, character
           profiles, terminology, and localization decisions; repair meaning, voice, scope, negation, callbacks,
           ambiguity, fluency, and cultural naturalness while accounting for the lettering budget. Proofread as an
           editor of this work and these characters, not as a context-free bilingual checker.
        5. Font/style selection and lettering pass: jointly choose the final wording, orientation, font family,
           weight, style, size, color, spacing, and explicit line or column breaks.
        6. Pixel-layout pass: prepare one renderer-ready layout for each logical lettering unit, set its box and
           alignment, measure capacity, and iterate wording, typography, and geometry until the translated
           lettering fits. An ordinary unit produces one render block. When one continuous utterance occupies a
           visually connected multi-lobed balloon, keep it in one segment and use explicit line/column breaks
           plus advanced.placement_groups to map disjoint subsets of lines and LocalDetection regions to tight
           absolute-pixel boxes inside the supported lobes.
        7. Mechanical audit: verify ownership, anchoring, overlap, fit, IDs, and spoiler safety.
        Never solve a later pass by weakening an earlier pass.

        LOGICAL LETTERING UNIT AND CONTAINER CONTRACT
        - Within LocalDetection scope, one segment represents one logical lettering unit. Normally this is one
          physical speech bubble, thought bubble, caption/narration box, sign/title block, or intentional
          standalone typographic unit and produces one render block. A qualifying same-utterance multi-lobed
          segment uses placement_groups so the renderer can produce one correctly anchored block per cavity
          without losing the shared translation/layout context.
        - A visually connected multi-lobed balloon group may be one logical lettering unit only when the
          original page proves that the lobes share an outline or bridge and contain one continuous semantic
          paragraph from one speaker turn. Visual connection alone is not enough; continuity of meaning,
          speaker, and reading flow must all agree. Cite every LocalDetection region supporting the unit.
        - For that connected same-utterance case, keep one segment. translated_text must contain an explicit
          '\n' at every intended line or column boundary. Set layout.text_box to the tight union of the supported
          cavities, then set advanced.placement_groups to a complete, non-overlapping partition of those output
          line indices and the segment's source_region_ids. Each group.text_box is an absolute original-page box
          inside the union and must intersect every source region assigned to that group. Place each complete
          line or column back inside its assigned cavity; never place lettering in the bridge or gap.
        - Split different semantic paragraphs, speaker turns, independently framed utterances, or visually
          disconnected containers into different segments with disjoint source_region_ids and independent
          layouts. Do this even when they share a speaker, form one grammatical sentence, or are consecutive.
        - Merge multiple LocalDetection rows only when the source image proves they are lines or columns of the
          same ordinary container, or supported parts of the qualifying connected same-utterance unit above.
          Never merge disconnected clusters, panels, bubbles, captions, or SFX merely because their text is
          semantically related.
        - Text equality is not duplicate evidence. Repeated utterances in different positions are legitimate.
          Treat a detection as a duplicate only when it covers the same visible glyph pixels; retain one owner.
        - Every detected source word belongs to its actual container. A text_box must be spatially anchored to
          every cited region. Never cite a top region while placing its box over a lower occurrence of the same
          phrase. Never use a cross-panel or disconnected cross-bubble union box; the only multi-lobe union
          allowed is the qualifying visually connected same-utterance unit defined above.

        CONNECTED-BALLOON DECISION PROCEDURE
        1. Trace the visible lobe boundaries and bridge/neck on the original page, then assign each supplied
           region to the exact lobe containing its glyph pixels. Do not infer lobes from region proximity alone.
        2. Decide whether the lobes contain one continuous semantic paragraph from one speaker turn. A reply,
           interruption, separate reaction, new sentence functioning as a separate turn, or independently
           framed statement is a different logical unit even when outlines touch.
        3. If they are different logical units, emit separate segments with disjoint source_region_ids and a
           tight text_box for each unit. Never solve this case with placement groups or offsets inside one union
           box.
        4. If they are one continuous unit, emit one segment and privately build a lobe-to-output-line map before
           choosing translated_text. Give every intended vertical column or horizontal line its own explicit
           newline-delimited entry. Emit one placement_group per independently positioned cavity, assigning each
           translated line index and each source_region_id exactly once, with a tight absolute text_box that
           intersects its assigned regions. The group boxes may touch but must not materially overlap; every
           group's final glyph bounds must remain inside its box and never occupy the bridge, neck, gap, or
           neighboring lobe.
        5. Re-check the decision after translation and proofreading. If natural target-language phrasing creates
           an independent turn, split it; if it remains one continuous utterance, preserve the lobe map while
           revising wording, breaks, font size, and offsets together.

        BOOK KNOWLEDGE
        - book_knowledge is the book-level semantic source for terminology, characterization, continuity,
          and reveal timing. Treat only explicitly locked/confirmed names and glossary entries as binding;
          provisional choices are suggestions and must be flagged if used.
        - Use the current page manifest, page-scoped precheck guidance, book knowledge, and both images together.

        DISCLOSURE CUTOFF AND SPOILER SAFETY
        - page.order is the current disclosure cutoff. book_page_index is the only authoritative mapping from
          page IDs to BookManifest order for the complete book, including pages excluded by include_in_mask.
        - Resolve every page ID in revealed_at, evidence[].page_id, page_start, and page_end through
          book_page_index before comparing it with page.order. Never infer order from digits embedded in a
          page ID. Never treat revealed_order by itself as BookManifest order: it can be the ordinal number of
          a reveal item. Therefore revealed_at=p0135 together with revealed_order=1 still becomes available
          only at the BookManifest order mapped from p0135.
        - If a referenced page ID is absent from book_page_index, or a mapped page ID conflicts with numeric
          timing metadata, treat the fact as not yet revealed. Prefer safe_wording_before_reveal when supplied;
          otherwise preserve the source text's ambiguity. Do not resolve ambiguity with a future name,
          identity, relationship, motive, or term.
        - A valid_from_order later than page.order is not active yet. A valid_to_order earlier than page.order
          is expired. The same applies to page_start and page_end after mapping their page IDs through
          book_page_index. A null valid_to_order has no upper bound.
        - Future or otherwise unavailable facts may be used only internally to avoid inconsistency. They must
          never leak into translated_text, notes, warnings, ignored_regions.reason,
          ignored_regions.evidence, or speaker_id.

        REGION COVERAGE AND GROUNDING
        - Every supplied region_id must be covered exactly once: cite it in one segment.source_region_ids
          array, or put it in one ignored_regions.region_ids array, but never both or in multiple segments.
        - LocalDetection is the hard OCR and rendering scope. Every segment must cite at least one supplied
          region_id. Never emit a segment for visible text, missed text, or filtered SFX outside the supplied
          regions: without a region/mask the downstream cleaning stage cannot process it.
        - Never invent, modify, or reuse a supplied region_id. Region IDs, manifest list order, and
          detection_index carry no reading-order or semantic sequence. Do not pair transcriptions with regions
          by advancing through either list, and never shift text from a preceding or following bubble into the
          current region merely because it is next in reading order.
        - For every region independently, use bbox with the declared coordinate system and page_position to
          locate its exact pixels on the original page before reading any glyph. Record privately:
          region_id -> page position -> visible glyph fragment -> physical container. The visible pixels at
          that bbox, not neighboring text or story context, determine source_text ownership.
        - A segment's source_text may contain multiple lines or columns only when every included source line or
          column is spatially supported by the union of its cited regions and the original page proves those
          regions belong to one logical lettering unit under the contract above. Cite all supporting regions.
          Never add an uncited nearby line, complete a detector-missed phrase, or borrow text from another
          position.
        - If a coarse region crosses independent logical lettering units and cannot be assigned safely without
          reusing its mask, do not fabricate an unmasked segment. Put the unsafe region in ignored_regions with
          precise evidence and emit a coarse_region_cross_container warning.
        - Put false positives, decorative marks, and excluded non-text in ignored_regions with evidence.

        SEGMENT IDENTITY AND ORDER
        - Give every segment a non-empty segment_id that is unique within this response. It is a temporary
          cross-reference for warnings; the host will replace it with a deterministic page-scoped ID.
        - reading_order must express the visible manga reading sequence. Do not reuse a reading_order value.
        - Determine reading_order from panel structure and bubble placement, never from detection_index. In a
          right-to-left manga, bubbles in the same visual band normally read from right to left unless the art
          clearly indicates another sequence.

        SOURCE, TRANSLATION, AND EDITORIAL QUALITY
        - Transcribe source_text exactly from the cited LocalDetection pixels, preserving meaningful
          punctuation, emphasis, elongation, ruby, and source line structure. Do not guess unreadable glyphs
          from plot context and do not OCR any uncited page area.
        - Translate into {targetLanguage}; write natural, concise manga Chinese rather than Japanese-shaped
          Chinese. Preserve intent, emotion, social status, character voice, foreshadowing and deliberate
          ambiguity. Adapt idioms, jokes, honorific behavior, dense modifiers and culture-bound expressions
          for equivalent reader effect without deleting plot-bearing detail or adding unsupported meaning.
        - Review every draft for name/term drift, negation/scope, omitted subjects, wrong speaker/addressee,
          flattened register, lost callbacks, unnatural Chinese, accidental omission/duplication, and verbosity
          that harms fit. Revise internally only when materially better and evidence-supported; if uncertain,
          keep safe wording and emit a warning.
        - needs_priority_review is mandatory for every segment. Set it true when OCR, translation/localization,
          speaker attribution, typography, grouping, or fit remains meaningfully uncertain; also set it true for
          unusually complex multi-cavity lettering that deserves human verification even after it passes the
          mechanical audit. Set it false only when the segment is confidently ready for normal downstream use.
          This routing flag never permits invalid region coverage, grouping, layout, or renderer overflow.
        - Write notes, warnings, ignored-region reasons, and evidence primarily in {targetLanguage}; source_text
          remains in the visible source language.
        - status=translated requires non-empty source_text and translated_text.
        - status=unreadable requires both source_text and translated_text to be empty.
        - translated_text is renderer-ready and must contain explicit '\n' line breaks at every intended line
          or column boundary. Never rely on automatic wrapping. A true one-line result contains no newline.
        - First form a complete natural translation, then compact/rebreak it for lettering. Never merge
          independent containers or logical units, or delete essential meaning, merely to hit a character
          count. Only the qualifying connected same-utterance case may share one segment under the contract.

        SPEAKER ATTRIBUTION
        - The page manifest contains host-computed speaker_constraints. A non-null speaker_id must be exactly
          one of its eligible_speaker_ids; if the list does not contain a supported speaker, or if bubble
          ownership is uncertain, emit speaker_id=null. Never copy a future or visually plausible character ID
          from book_knowledge into this page.
        - Only fill speaker_id when page geometry, bubble ownership, and book evidence all support the same
          speaker. If any part is uncertain, set speaker_id to null.
        - speaker_id identifies the person who speaks, not the person being addressed. A direct address such as
          "Name-san" or "Ms. Name" is not evidence that the named addressee is speaking.
        - Use only character_id values registered in book_knowledge.characters. If the speaker is not registered,
          speaker_id must be null.
        - A registered character is eligible only when page.order is within its valid_from_order through
          valid_to_order interval (a null valid_to_order means no upper bound). A character whose identity or
          role is future or expired at this page must not be emitted as speaker_id.
        - Evaluate every segment independently. Never assign multiple speakers or bubbles to the most visually
          prominent character by default, and never infer speaker_id from character prominence alone.

        BASE LAYOUT AND FIT
        - text_box uses original-page pixel coordinates: x0,y0 inclusive and x1,y1 exclusive.
        - orientation is horizontal or vertical. For horizontal text, reading_direction is the character flow
          (left_to_right or right_to_left). For vertical manga text, glyphs flow top-to-bottom inside a column,
          and reading_direction selects column progression (normally right_to_left, occasionally left_to_right).
        - alignment is relative to inline flow; for vertical text it controls top/center/bottom placement within
          each column. The renderer centers the complete column group on the cross-axis; reading_direction changes
          column order only, not the group's anchor. Use font_id only from the supplied font catalog.
        - font_size_px, font_weight, italic, color, and letter_spacing_px describe the uniform default style.
          font_weight is an integer from 1 to 1000; 400 is regular and 700 is bold. The renderer can
          synthesize intermediate/heavier weights from the managed face. Do not emit a separate bold field.
          Every line_adjustments.line_index must be a zero-based index strictly smaller than the number of
          explicit lines in translated_text; every character_adjustments.character_index must be within the
          translated text. If there is no valid adjustment, return an empty array rather than guessing an index.
        - rotation is optional and box-local: center_x_px/center_y_px are relative to text_box and angle_deg
          is clockwise. Do not copy the page manifest's detected-region geometry.center_x_px/
          center_y_px values into rotation: those geometry values are page-relative. For ordinary box-center
          rotation, use (text_box.x1-text_box.x0)/2 and (text_box.y1-text_box.y0)/2. stroke is optional and
          applies to the whole box: width_px, color, and
          round_corners.
        - color must be #RRGGBB or #RRGGBBAA.
        - Choose text_box from the cited logical lettering unit with a safety margin. For a qualifying connected
          multi-lobed unit, use the tight union of its supported cavities as layout.text_box and keep every
          placement_group.text_box inside that union. Mechanically check capacity separately in every effective
          render box: for vertical text, that box's column count times effective column advance must fit its width
          and its longest column plus spacing must fit its height; for horizontal text, every measured line width
          and total line height assigned to that box must fit. Account for stroke space. If not,
          improve concise wording, rebalance explicit breaks, modestly reduce font size, or expand only inside
          the same logical lettering unit—in that order.
        - Never return an empty line or column. Every newline is an intentional rendered boundary.

        ADVANCED LAYOUT
        - Use advanced=null for ordinary single-container dialogue that needs no exceptional typography. If an
          ordinary single-container block needs advanced line/character/ruby overrides, set placement_groups=[];
          an empty placement_groups array means the whole segment still renders in layout.text_box.
        - A qualifying connected multi-lobed balloon carrying one continuous semantic paragraph must use
          non-empty placement_groups. Each group owns a non-empty, ascending, contiguous list of line_indices, a
          non-empty list of source_region_ids, and one absolute original-page text_box for a physical cavity. All
          groups together must cover every translated line and every segment source region exactly once. Group
          entries must be ordered by increasing minimum line_index for stable reading/render order. Group boxes
          must stay inside layout.text_box, intersect every region they cite, and not materially overlap. Do not
          use placement_groups for different semantic paragraphs or speaker turns; split those into separate
          segments.
        - HARD MECHANICAL CAVITY CHECK: if the cited LocalDetection rectangles form two or more visibly separate
          spatial clusters rather than one continuous text container, advanced must not be null and
          placement_groups must not be empty. Either split the semantic units into separate segments, or, only when
          they are one continuous same-speaker utterance, assign every cluster to its own tight placement_group.
          Never repeat a union-box segment with advanced=null or placement_groups=[] after a grouping repair request.
        - line_adjustments offsets are pixel deltas from the renderer's normal unadjusted placement inside
          its effective text box: layout.text_box when placement_groups is empty, otherwise the text_box of the
          group that owns that line. They are not absolute page coordinates and must never be used to move text
          between cavities. Privately estimate each line or column's normal position first, then use offsets only
          for small within-box refinement. For vertical text, each newline-delimited entry is one column, line 0
          is the first/rightmost column for right_to_left flow, offset_x_px moves that whole column horizontally,
          and offset_y_px moves its vertical start. For horizontal text, each entry is one row and the offsets
          move that whole row.
        - Do not use advanced adjustments as a substitute for good text_box, line breaks, alignment, font
          choice, or base font size. Do not use character adjustments to disguise a separate semantic paragraph
          or speaker turn inside one segment.
        - Use sparse line/character adjustments only when visually required by an irregular bubble, deliberate
          typography, emphasis, small kana/ruby, or another exceptional layout.
        - When merging multiple detected regions, preserve visibly different font sizes, colors, or emphasis
          with sparse advanced overrides. If a difference exists only in LocalDetection hints and is not visible
          on the original page, ignore it; never mechanically copy detector font-size hints into the layout.
        - All line_index and character_index values are zero-based in rendered reading order.
        - For horizontal text, line 0 is the topmost rendered line. For vertical right_to_left text, line 0 is
          the rightmost column; for vertical left_to_right text, line 0 is the leftmost column.
        - character_index and ruby base indexes address Unicode text elements in translated_text after removing
          '\n'; they are not UTF-16 code-unit offsets.
        - A ruby base range must remain wholly inside one placement_group; never span ruby across cavity groups.
        - A per-line or per-character font_size_px/color is an override; null keeps the base value.

        FINAL AUDIT
        Re-run the private region ledger from coordinates, not reading order. For every cited region, verify
        source_text against the glyph pixels inside that exact bbox and verify text_box intersects that region's
        own container or connected lobe. Then verify exact region coverage and unique ownership; at least one
        region per segment; no OCR outside LocalDetection; one logical lettering unit per segment; different
        semantic paragraphs and speaker turns split into different segments; qualifying connected same-utterance
        groups use explicit breaks and a complete line/region partition in placement_groups, with separately
        checked fit and no cross-group ruby; unique reading orders; no disconnected cross-container boxes; no
        accidental duplicate or high-overlap blocks; renderer-ready fit; valid font/speaker IDs; and no
        future-information leakage. Finally, verify every segment's needs_priority_review routing flag.

        Return only the JSON required by the supplied schema. This stage has no human approval gate.
        """;

    public static string BookLevelContext(
        DialogueTranslationRequest request,
        FontReferenceSnapshot fontReference)
    {
        // Keep this context free of run/page-scoped values. It is the stable prefix used for
        // prompt caching, so semantically identical knowledge must serialize identically across
        // runs and pages.
        var pageIndex = new JsonArray();
        foreach (var page in (request.BookPageIndex ?? []).OrderBy(
                     item => item.Order).ThenBy(item => item.PageId, StringComparer.Ordinal))
        {
            pageIndex.Add(new JsonObject
            {
                ["page_id"] = page.PageId,
                ["order"] = page.Order
            });
        }

        var fonts = new JsonArray();
        foreach (var font in fontReference.Fonts
                     .OrderBy(item => item.FontId, StringComparer.Ordinal))
        {
            var styles = new JsonArray();
            foreach (var style in font.Styles
                         .OrderBy(item => item.Style, StringComparer.Ordinal)
                         .ThenBy(item => item.RenderMode, StringComparer.Ordinal)
                         .ThenBy(item => item.SourceStyle, StringComparer.Ordinal)
                         .ThenBy(item => item.Label, StringComparer.Ordinal))
            {
                styles.Add(new JsonObject
                {
                    ["style"] = style.Style,
                    ["render_mode"] = style.RenderMode,
                    ["source_style"] = style.SourceStyle,
                    ["label"] = style.Label,
                    ["missing_glyph_count"] = style.MissingGlyphCount,
                    ["missing_glyphs"] = new JsonArray(
                        style.MissingGlyphs
                            .Order(StringComparer.Ordinal)
                            .Select(glyph => (JsonNode?)glyph)
                            .ToArray())
                });
            }

            fonts.Add(new JsonObject
            {
                ["font_id"] = font.FontId,
                ["display_name"] = font.DisplayName,
                ["family_name"] = font.FamilyName,
                ["category"] = font.Category,
                ["tags"] = new JsonArray(
                    font.Tags
                        .Order(StringComparer.Ordinal)
                        .Select(tag => (JsonNode?)tag)
                        .ToArray()),
                ["styles"] = styles
            });
        }

        var context = new JsonObject
        {
            ["prompt_contract_version"] = PromptContractVersion,
            ["schema_contract_version"] = SchemaContractVersion,
            ["book_id"] = request.BookId,
            ["target_language"] = request.Options.TargetLanguage,
            ["book_page_index"] = pageIndex,
            ["book_knowledge"] = new JsonObject
            {
                ["target_language"] = request.BookKnowledge.TargetLanguage,
                ["chapters"] = Canonicalize(request.BookKnowledge.Chapters),
                ["characters"] = Canonicalize(request.BookKnowledge.Characters),
                ["cultural_localizations"] = Canonicalize(
                    request.BookKnowledge.CulturalLocalizations),
                ["glossary"] = Canonicalize(request.BookKnowledge.Glossary),
                ["story_bible"] = Canonicalize(request.BookKnowledge.StoryBible)
            },
            ["font_reference"] = new JsonObject
            {
                ["reference_id"] = fontReference.ReferenceId,
                ["library_revision"] = fontReference.LibraryRevision,
                ["image"] = new JsonObject
                {
                    ["asset_id"] = fontReference.Image.AssetId,
                    ["width"] = fontReference.Image.Width,
                    ["height"] = fontReference.Image.Height,
                    ["media_type"] = fontReference.Image.MediaType
                },
                ["fonts"] = fonts
            }
        };

        return "BOOK-LEVEL DIALOGUE CONTEXT JSON:\n"
            + context.ToJsonString(JsonOptions);
    }

    public static string PageLevelManifest(
        DialogueTranslationRequest request,
        DialogueImageAsset pageImage)
    {
        var page = request.Page;
        var manifest = JsonSerializer.SerializeToNode(new
        {
            run_id = request.RunId,
            page = new
            {
                page_id = page.PageId,
                page.Order,
                chapter_id = page.ChapterId,
                scene_id = page.SceneId,
                page_type = page.PageType,
                include_in_mask = page.IncludeInMask,
                width = page.Width,
                height = page.Height,
                media_type = page.MediaType,
                local_detection_scope = new
                {
                    coordinate_system = "original-page pixels; x increases left-to-right; y increases top-to-bottom; bbox is [x0,y0,x1,y1] with exclusive x1/y1",
                    hard_rule = "OCR and render segments are limited to supplied detected_regions; every segment must cite at least one region_id",
                    ordering_rule = "region_id, manifest list order, and detection_index are not reading order and must not be used to shift text between regions"
                },
                speaker_constraints = new
                {
                    page_order = page.Order,
                    eligible_speaker_ids = EligibleSpeakerIds(
                        request.BookKnowledge.Characters,
                        page.Order),
                    hard_rule = "speaker_id must be null or exactly one eligible_speaker_ids value"
                },
                detected_regions = page.DetectedTextRegions.Select(item => new
                {
                    region_id = item.RegionId,
                    detection_index = item.DetectionIndex,
                    bbox = new[]
                    {
                        item.Region.X0,
                        item.Region.Y0,
                        item.Region.X1,
                        item.Region.Y1
                    },
                    geometry = new
                    {
                        width_px = item.Region.X1 - item.Region.X0,
                        height_px = item.Region.Y1 - item.Region.Y0,
                        center_x_px = (item.Region.X0 + item.Region.X1) / 2.0,
                        center_y_px = (item.Region.Y0 + item.Region.Y1) / 2.0
                    },
                    page_position = new
                    {
                        horizontal_band = SpatialBand(
                            (item.Region.X0 + item.Region.X1) / 2.0,
                            page.Width,
                            "left",
                            "center",
                            "right"),
                        vertical_band = SpatialBand(
                            (item.Region.Y0 + item.Region.Y1) / 2.0,
                            page.Height,
                            "top",
                            "middle",
                            "bottom"),
                        bbox_percent = new[]
                        {
                            Percent(item.Region.X0, page.Width),
                            Percent(item.Region.Y0, page.Height),
                            Percent(item.Region.X1, page.Width),
                            Percent(item.Region.Y1, page.Height)
                        },
                        center_percent = new[]
                        {
                            Percent((item.Region.X0 + item.Region.X1) / 2.0, page.Width),
                            Percent((item.Region.Y0 + item.Region.Y1) / 2.0, page.Height)
                        }
                    },
                    language_hint = item.Region.Language.ToString().ToLowerInvariant(),
                    orientation_hint = item.Region.Orientation.ToString().ToLowerInvariant(),
                    font_size_hint = item.Region.FontSize,
                    detection_confidence = item.Region.Confidence,
                    lines = item.Region.Lines.Select(line => new
                    {
                        polygon = line.Polygon.Select(point => new[] { point.X, point.Y }),
                        line.Confidence
                    })
                }),
                page_scoped_precheck_guidance = request.PageScopedPrecheckGuidance.ValueKind
                    is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : Canonicalize(request.PageScopedPrecheckGuidance)
            },
            original_page_asset = new
            {
                asset_id = pageImage.AssetId,
                pageImage.Width,
                pageImage.Height,
                media_type = pageImage.MediaType
            }
        }, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Failed to serialize the dialogue manifest.");

        return "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n"
            + manifest.ToJsonString(JsonOptions);
    }

    private static double Percent(double value, int dimension) =>
        Math.Round(value * 100.0 / dimension, 1, MidpointRounding.AwayFromZero);

    private static string SpatialBand(
        double value,
        int dimension,
        string first,
        string middle,
        string last)
    {
        var normalized = value / dimension;
        return normalized < 1.0 / 3.0
            ? first
            : normalized < 2.0 / 3.0
                ? middle
                : last;
    }

    internal static IReadOnlyList<string> EligibleSpeakerIds(
        JsonElement characters,
        int pageOrder)
    {
        if (characters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in characters.EnumerateArray())
        {
            if (character.ValueKind != JsonValueKind.Object
                || !character.TryGetProperty("character_id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                continue;
            }

            var validFromOrder = 1;
            if (character.TryGetProperty("valid_from_order", out var validFrom))
            {
                if (validFrom.ValueKind != JsonValueKind.Number
                    || !validFrom.TryGetInt32(out validFromOrder))
                {
                    continue;
                }
            }

            int? validToOrder = null;
            if (character.TryGetProperty("valid_to_order", out var validTo)
                && validTo.ValueKind != JsonValueKind.Null)
            {
                if (validTo.ValueKind != JsonValueKind.Number
                    || !validTo.TryGetInt32(out var parsedValidTo))
                {
                    continue;
                }

                validToOrder = parsedValidTo;
            }

            if (validFromOrder <= 0
                || validToOrder is not null && validToOrder < validFromOrder
                || pageOrder < validFromOrder
                || validToOrder is not null && pageOrder > validToOrder)
            {
                continue;
            }

            eligible.Add(idElement.GetString()!);
        }

        return eligible.Order(StringComparer.Ordinal).ToArray();
    }

    private static JsonNode? Canonicalize(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var result = new JsonObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    result[property.Name] = Canonicalize(property.Value);
                }

                return result;
            }

            case JsonValueKind.Array:
            {
                var result = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    result.Add(Canonicalize(item));
                }

                return result;
            }

            case JsonValueKind.String:
                return JsonValue.Create(value.GetString());
            case JsonValueKind.Number:
                return JsonNode.Parse(value.GetRawText());
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create(value.GetBoolean());
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

}
