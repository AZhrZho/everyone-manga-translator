using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MangaTranslator.LocalDetection;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;

namespace MangaTranslator.Dialogue.Tests;

public sealed class ModelBackedMultimodalDialogueTranslatorTests
{
    private static readonly byte[] PageBytes = [1, 2, 3, 4];
    private static readonly byte[] FontReferenceBytes = [5, 6, 7, 8];

    [Fact]
    public async Task TranslateAsyncUsesExactlyTwoImagesAndReturnsInspectableLayoutArtifact()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var options = new DialogueTranslationOptions();

        var result = await translator.TranslateAsync(CreateRequest(images, options));

        Assert.Equal("gpt-5.6-sol", options.Model);
        Assert.Equal("gpt-5.6-sol", Assert.Single(model.Requests).Model);
        Assert.Equal(1, images.PageCallCount);
        Assert.Equal(1, images.FontReferenceCallCount);
        var userMessage = Assert.Single(model.Requests[0].Messages, message =>
            message.Role == ModelRole.User);
        var imageContents = userMessage.Content.OfType<ImageContent>().ToArray();
        Assert.Collection(
            imageContents,
            image =>
            {
                Assert.Equal(PageBytes, image.Data.ToArray());
                Assert.Equal("image/png", image.MediaType);
                Assert.Equal(ImageDetailLevel.Original, image.Detail);
            },
            image =>
            {
                Assert.Equal(FontReferenceBytes, image.Data.ToArray());
                Assert.Equal("image/png", image.MediaType);
                Assert.Equal(ImageDetailLevel.Original, image.Detail);
            });

        var segment = Assert.Single(result.Segments);
        Assert.Equal(
            ["p0001:region:0001", "p0001:region:0002"],
            segment.SourceRegionIds);
        Assert.Equal("p0001:segment:0001", segment.SegmentId);
        Assert.Equal("F0001", segment.Layout.FontId);
        Assert.Contains('\n', segment.TranslatedText);
        Assert.DoesNotContain('\r', segment.TranslatedText);
        Assert.Equal(2, segment.TranslatedText.Split('\n').Length);
        Assert.Equal(new TextLayoutBox(92, 84, 356, 530), segment.Layout.TextBox);
        Assert.Equal(DialogueTextOrientation.Vertical, segment.Layout.Orientation);
        Assert.Equal(DialogueReadingDirection.RightToLeft, segment.Layout.ReadingDirection);
        Assert.Equal(DialogueTextAlignment.Center, segment.Layout.Alignment);

        var advanced = Assert.IsType<DialogueAdvancedLayout>(segment.Layout.Advanced);
        Assert.Collection(
            advanced.LineAdjustments,
            first => Assert.Equal(0, first.LineIndex),
            second => Assert.Equal(1, second.LineIndex));
        Assert.Equal(2, Assert.Single(advanced.CharacterAdjustments).CharacterIndex);
        var ruby = Assert.Single(advanced.RubyAnnotations);
        Assert.Equal(0, ruby.BaseCharacterIndex);
        Assert.Equal(2, ruby.BaseCharacterLength);

        var ignored = Assert.Single(result.IgnoredRegions);
        Assert.Equal(["p0001:region:0003"], ignored.RegionIds);
        Assert.Equal("装饰性笔画", ignored.Reason);
        Assert.Equal("zh-Hans", result.TargetLanguage);
        Assert.Equal("fake-model-provider", result.ProviderId);
        Assert.False(segment.NeedsPriorityReview);
        Assert.False(result.NeedsPriorityReview);

        var artifactPath = TestArtifactWriter.WriteDialogueTranslation(result);
        Assert.True(File.Exists(artifactPath));
        var artifact = await File.ReadAllTextAsync(artifactPath);
        Assert.Contains("\"ignored_regions\"", artifact, StringComparison.Ordinal);
        Assert.Contains("\"translated_text\": \"今天辛苦了\\n我们继续吧\"", artifact, StringComparison.Ordinal);
        Assert.Contains("\"font_id\": \"F0001\"", artifact, StringComparison.Ordinal);
        Assert.Contains(
            "\"needs_priority_review\": false",
            artifact,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncNormalizesPageRelativeRotationCenterFromModelOutput()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        root["segments"]![0]!["layout"]!["rotation"] = new JsonObject
        {
            ["center_x_px"] = 342,
            ["center_y_px"] = 307,
            ["angle_deg"] = -15
        };
        var model = new SequenceModelProvider(root.ToJsonString(ModelJsonOptions));
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        var result = await translator.TranslateAsync(CreateRequest(images));

        var rotation = Assert.IsType<DialogueBoxRotation>(Assert.Single(result.Segments).Layout.Rotation);
        Assert.Equal(250, rotation.CenterXPx);
        Assert.Equal(223, rotation.CenterYPx);
        Assert.Equal(-15, rotation.AngleDeg);
    }

    [Fact]
    public async Task TranslateAsyncPreservesModelPriorityReviewFlagForDownstreamProofreading()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        root["segments"]![0]!["needs_priority_review"] = true;
        var model = new SequenceModelProvider(root.ToJsonString(ModelJsonOptions));
        var translator = new ModelBackedMultimodalDialogueTranslator(
            model);

        var result = await translator.TranslateAsync(
            CreateRequest(new RecordingImageProvider()));

        Assert.True(Assert.Single(result.Segments).NeedsPriorityReview);
        Assert.True(result.NeedsPriorityReview);
    }

    [Fact]
    public async Task TranslateAsyncPlacesStableBookAndFontContextBeforeDynamicPageContent()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(images) with
        {
            BookKnowledge = CreateBookKnowledge("FULL_BOOK_ALWAYS_PRESENT"),
        };

        await translator.TranslateAsync(request);

        var modelRequest = Assert.Single(model.Requests);
        var userMessage = Assert.Single(modelRequest.Messages, message =>
            message.Role == ModelRole.User);
        Assert.StartsWith(
            "BOOK-LEVEL DIALOGUE CONTEXT JSON:\n",
            Assert.IsType<TextContent>(userMessage.Content[0]).Text,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n",
            Assert.IsType<TextContent>(userMessage.Content[1]).Text,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "ORIGINAL_PAGE_IMAGE PAGE_ID=",
            Assert.IsType<TextContent>(userMessage.Content[2]).Text,
            StringComparison.Ordinal);
        Assert.IsType<ImageContent>(userMessage.Content[3]);
        Assert.StartsWith(
            "FONT_REFERENCE_IMAGE REFERENCE_ID=",
            Assert.IsType<TextContent>(userMessage.Content[4]).Text,
            StringComparison.Ordinal);
        Assert.IsType<ImageContent>(userMessage.Content[5]);

        var bookContextText = RequestJsonText(
            modelRequest,
            "BOOK-LEVEL DIALOGUE CONTEXT JSON:\n");
        var pageManifestText = RequestJsonText(
            modelRequest,
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n");
        using var bookDocument = JsonDocument.Parse(bookContextText);
        using var pageDocument = JsonDocument.Parse(pageManifestText);
        var bookContext = bookDocument.RootElement;
        var pageManifest = pageDocument.RootElement;
        Assert.Equal(
            ["prompt_contract_version", "schema_contract_version"],
            bookContext.EnumerateObject().Take(2).Select(property => property.Name));
        Assert.Equal("3.8.1", bookContext.GetProperty("prompt_contract_version").GetString());
        Assert.Equal("1.3.0", bookContext.GetProperty("schema_contract_version").GetString());
        Assert.False(bookContext.TryGetProperty("run_id", out _));
        Assert.Equal("dialogue_unit_run", pageManifest.GetProperty("run_id").GetString());
        var bookKnowledge = bookContext.GetProperty("book_knowledge");
        Assert.Equal(
            "FULL_BOOK_ALWAYS_PRESENT",
            bookKnowledge.GetProperty("story_bible").GetProperty("test_marker").GetString());
        Assert.True(bookKnowledge.TryGetProperty("chapters", out _));
        Assert.True(bookKnowledge.TryGetProperty("characters", out _));
        Assert.True(bookKnowledge.TryGetProperty("cultural_localizations", out _));
        Assert.True(bookKnowledge.TryGetProperty("glossary", out _));
        Assert.True(bookKnowledge.TryGetProperty("story_bible", out _));
        Assert.False(pageManifest.TryGetProperty("book_knowledge", out _));
        var page = pageManifest.GetProperty("page");
        var localDetectionScope = page.GetProperty("local_detection_scope");
        Assert.Contains(
            "x increases left-to-right; y increases top-to-bottom",
            localDetectionScope.GetProperty("coordinate_system").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "every segment must cite at least one region_id",
            localDetectionScope.GetProperty("hard_rule").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "are not reading order",
            localDetectionScope.GetProperty("ordering_rule").GetString(),
            StringComparison.Ordinal);
        var firstRegionPosition = page
            .GetProperty("detected_regions")[0]
            .GetProperty("page_position");
        Assert.Equal("left", firstRegionPosition.GetProperty("horizontal_band").GetString());
        Assert.Equal("top", firstRegionPosition.GetProperty("vertical_band").GetString());
        Assert.Equal(
            [8.3, 5.6, 26.7, 16.7],
            firstRegionPosition
                .GetProperty("bbox_percent")
                .EnumerateArray()
                .Select(item => item.GetDouble())
                .ToArray());
        Assert.Equal(
            [17.5, 11.1],
            firstRegionPosition
                .GetProperty("center_percent")
                .EnumerateArray()
                .Select(item => item.GetDouble())
                .ToArray());
        var speakerConstraints = page
            .GetProperty("speaker_constraints");
        Assert.Equal(
            ["character_001"],
            speakerConstraints
                .GetProperty("eligible_speaker_ids")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal(1, speakerConstraints.GetProperty("page_order").GetInt32());

        var pageIndexEntries = bookContext.GetProperty("book_page_index")
            .EnumerateArray()
            .Select(item => (PageId: item.GetProperty("page_id").GetString(),
                Order: item.GetProperty("order").GetInt32()))
            .ToArray();
        Assert.Equal(
            [("p0001", 1), ("p0002", 2), ("p0003", 3)],
            pageIndexEntries);

        var bookKnowledgeIndex = bookContextText.IndexOf(
            "\"book_knowledge\"",
            StringComparison.Ordinal);
        var fontReferenceIndex = bookContextText.IndexOf(
            "\"font_reference\"",
            StringComparison.Ordinal);
        Assert.True(bookKnowledgeIndex >= 0);
        Assert.True(bookKnowledgeIndex < fontReferenceIndex);
        var pageRunIndex = pageManifestText.IndexOf("\"run_id\"", StringComparison.Ordinal);
        var pageIndex = pageManifestText.IndexOf("\"page\"", StringComparison.Ordinal);
        var originalAssetIndex = pageManifestText.IndexOf(
            "\"original_page_asset\"",
            StringComparison.Ordinal);
        Assert.True(pageRunIndex >= 0 && pageRunIndex < pageIndex);
        Assert.True(pageIndex < originalAssetIndex);

        var systemMessage = Assert.Single(modelRequest.Messages, message =>
            message.Role == ModelRole.System);
        var prompt = NormalizeWhitespace(Assert.Single(
            systemMessage.Content.OfType<TextContent>()).Text);
        Assert.Contains(
            "Your primary responsibility is to translate and re-letter the current manga page",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "The final deliverable is a complete, renderer-ready plan containing the translated lettering and its pixel layout, expressed only through the supplied output schema",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "This is one integrated model call, but it still requires a deliberate staged workflow inside the shared context",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "The output schema is the delivery contract for the final re-lettering result, not a progress report",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "1. Region-grounded OCR/transcription pass",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "2. Logical grouping and reading-order pass",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("3. Translation pass", prompt, StringComparison.Ordinal);
        Assert.Contains("4. Editorial proofreading pass", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "5. Font/style selection and lettering pass",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("6. Pixel-layout pass", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Translation length, available lettering space, line/column breaks, orientation, font family, font size, spacing, and box geometry are coupled constraints",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "There is no layout-independent final translation and no wording-independent final layout",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "do not translate the current page in isolation",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use all supplied full-book information and page context to choose wording faithful to this specific work, each character's established voice, relationships, scene tone, genre, and the target locale",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Proofread as an editor of this work and these characters, not as a context-free bilingual checker",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "LocalDetection is the hard OCR and rendering scope",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never emit a segment for visible text, missed text, or filtered SFX outside the supplied regions",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "LocalDetection regions/mask define the hard OCR and rendering inclusion boundary",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text outside supplied regions/mask is out of scope even when visible on the original page",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not pair transcriptions with regions by advancing through either list",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "region_id -> page position -> visible glyph fragment -> physical container",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "one segment represents one logical lettering unit",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A visually connected multi-lobed balloon group may be one logical lettering unit only when",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "one continuous semantic paragraph from one speaker turn",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "advanced.placement_groups to a complete, non-overlapping partition",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Split different semantic paragraphs, speaker turns, independently framed utterances, or visually disconnected containers into different segments",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never merge disconnected clusters, panels, bubbles, captions, or SFX merely because their text is semantically related",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "the only multi-lobe union allowed is the qualifying visually connected same-utterance unit defined above",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("CONNECTED-BALLOON DECISION PROCEDURE", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "privately build a lobe-to-output-line map",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "must use non-empty placement_groups",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "set placement_groups=[]",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not use placement_groups for different semantic paragraphs or speaker turns; split those into separate segments",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "All groups together must cover every translated line and every segment source region exactly once",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A ruby base range must remain wholly inside one placement_group",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "line_adjustments offsets are pixel deltas from the renderer's normal unadjusted placement",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never add an uncited nearby line, complete a detector-missed phrase, or borrow text from another position",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncSendsFullBookKnowledgeForEveryRequest()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(images) with
        {
            BookKnowledge = CreateBookKnowledge("FULL_BOOK_NO_SUPPLEMENT")
        };
        await translator.TranslateAsync(request);

        var modelRequest = Assert.Single(model.Requests);
        var bookContextText = RequestJsonText(
            modelRequest,
            "BOOK-LEVEL DIALOGUE CONTEXT JSON:\n");
        var pageManifestText = RequestJsonText(
            modelRequest,
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n");
        using var bookDocument = JsonDocument.Parse(bookContextText);
        using var pageDocument = JsonDocument.Parse(pageManifestText);
        var bookKnowledge = bookDocument.RootElement.GetProperty("book_knowledge");
        Assert.Equal("zh-Hans", bookKnowledge.GetProperty("target_language").GetString());
        Assert.Equal(
            "FULL_BOOK_NO_SUPPLEMENT",
            bookKnowledge
                .GetProperty("story_bible")
                .GetProperty("test_marker")
                .GetString());
        Assert.Equal(
            JsonValueKind.Null,
            pageDocument.RootElement
                .GetProperty("page")
                .GetProperty("page_scoped_precheck_guidance")
                .ValueKind);
    }

    [Fact]
    public async Task SystemPromptRequiresJointEvidenceForEverySpeakerAttribution()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        await translator.TranslateAsync(CreateRequest(images));

        var request = Assert.Single(model.Requests);
        var systemMessage = Assert.Single(request.Messages, message =>
            message.Role == ModelRole.System);
        var prompt = NormalizeWhitespace(Assert.Single(
            systemMessage.Content.OfType<TextContent>()).Text);
        Assert.Contains(
            "Only fill speaker_id when page geometry, bubble ownership, and book evidence all support the same speaker.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "If any part is uncertain, set speaker_id to null.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never assign multiple speakers or bubbles to the most visually prominent character by default",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "never infer speaker_id from character prominence alone",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "speaker_id identifies the person who speaks, not the person being addressed",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A direct address such as \"Name-san\" or \"Ms. Name\" is not evidence that the named addressee is speaking",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use only character_id values registered in book_knowledge.characters",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("speaker_id must be null", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "page.order is within its valid_from_order through valid_to_order interval",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "The page manifest contains host-computed speaker_constraints",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickPromptUsesSingleCavityContractAndIncludesMechanicalRegionHints()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var translator = new ModelBackedMultimodalDialogueTranslator(
            model,
            QuickDialoguePromptProvider.Instance);

        await translator.TranslateAsync(CreateRequest(new RecordingImageProvider()));

        var request = Assert.Single(model.Requests);
        var system = string.Join(
            "\n",
            request.Messages
                .Where(message => message.Role == ModelRole.System)
                .SelectMany(message => message.Content.OfType<TextContent>())
                .Select(content => content.Text));
        var user = string.Join(
            "\n",
            request.Messages
                .Where(message => message.Role == ModelRole.User)
                .SelectMany(message => message.Content.OfType<TextContent>())
                .Select(content => content.Text));

        Assert.Contains("advanced.placement_groups must always be []", system, StringComparison.Ordinal);
        Assert.Contains("Quick Mode deliberately does not use multi-cavity lettering", system, StringComparison.Ordinal);
        Assert.Contains("\"bbox_width_px\"", user, StringComparison.Ordinal);
        Assert.Contains("\"bbox_height_px\"", user, StringComparison.Ordinal);
        Assert.Contains("\"orientation_hint\":\"vertical\"", user, StringComparison.Ordinal);
        Assert.Contains("\"font_size_hint\":28", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickProfileAutoFitsSingleCavityWithoutWeakeningFinalValidation()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var layout = root["segments"]![0]!["layout"]!.AsObject();
        layout["font_size_px"] = 140;
        layout["advanced"] = null;
        var model = new SequenceModelProvider(root.ToJsonString(ModelJsonOptions));
        var translator = new ModelBackedMultimodalDialogueTranslator(
            model,
            QuickDialoguePromptProvider.Instance);

        var result = await translator.TranslateAsync(CreateRequest(
            new RecordingImageProvider(),
            new DialogueTranslationOptions { MaxModelAttempts = 1 }));

        var segment = Assert.Single(result.Segments);
        Assert.True(segment.Layout.FontSizePx < 140);
        Assert.True(segment.NeedsPriorityReview);
    }

    [Fact]
    public async Task QuickProfileUsesBoundedBoxExpansionAfterMinimumFontSize()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var segment = root["segments"]![0]!.AsObject();
        segment["translated_text"] = "甲\n乙\n丙";
        var layout = segment["layout"]!.AsObject();
        layout["text_box"] = new JsonObject
        {
            ["x0"] = 100,
            ["y0"] = 100,
            ["x1"] = 129,
            ["y1"] = 500
        };
        layout["font_size_px"] = 10;
        layout["letter_spacing_px"] = 0;
        layout["advanced"] = null;
        var model = new SequenceModelProvider(root.ToJsonString(ModelJsonOptions));
        var translator = new ModelBackedMultimodalDialogueTranslator(
            model,
            QuickDialoguePromptProvider.Instance);

        var result = await translator.TranslateAsync(CreateRequest(
            new RecordingImageProvider(),
            new DialogueTranslationOptions { MaxModelAttempts = 1 }));

        var fitted = Assert.Single(result.Segments);
        Assert.True(fitted.Layout.TextBox.X1 - fitted.Layout.TextBox.X0 > 29);
        Assert.True(fitted.Layout.TextBox.X1 - fitted.Layout.TextBox.X0 <= 77);
        Assert.True(fitted.NeedsPriorityReview);
    }

    [Fact]
    public async Task SystemPromptEnforcesPageOrderSpoilerCutoffAndAmbiguityPreservation()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        await translator.TranslateAsync(CreateRequest(images));

        var request = Assert.Single(model.Requests);
        var systemMessage = Assert.Single(request.Messages, message =>
            message.Role == ModelRole.System);
        var prompt = NormalizeWhitespace(Assert.Single(
            systemMessage.Content.OfType<TextContent>()).Text);
        Assert.Contains(
            "page.order is the current disclosure cutoff",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "book_page_index is the only authoritative mapping from page IDs to BookManifest order",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never treat revealed_order by itself as BookManifest order",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "revealed_at=p0135 together with revealed_order=1 still becomes available only",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "If a referenced page ID is absent from book_page_index, or a mapped page ID conflicts with numeric timing metadata, treat the fact as not yet revealed",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Prefer safe_wording_before_reveal when supplied",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A valid_from_order later than page.order is not active yet",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A valid_to_order earlier than page.order is expired",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "The same applies to page_start and page_end after mapping their page IDs through book_page_index",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "must never leak into translated_text, notes, warnings, ignored_regions.reason, ignored_regions.evidence, or speaker_id",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "preserve the source text's ambiguity",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemPromptUsesVisualReadingOrderAndSparseOverridesForVisibleDifferences()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        await translator.TranslateAsync(CreateRequest(images));

        var request = Assert.Single(model.Requests);
        var systemMessage = Assert.Single(request.Messages, message =>
            message.Role == ModelRole.System);
        var prompt = NormalizeWhitespace(Assert.Single(
            systemMessage.Content.OfType<TextContent>()).Text);
        Assert.Contains(
            "Determine reading_order from panel structure and bubble placement, never from detection_index",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "bubbles in the same visual band normally read from right to left",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "preserve visibly different font sizes, colors, or emphasis with sparse advanced overrides",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "never mechanically copy detector font-size hints into the layout",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "needs_priority_review is mandatory for every segment",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "This routing flag never permits invalid region coverage",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncRejectsPageExcludedByIncludeInMaskBeforeAnyExternalCall()
    {
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(images) with
        {
            Page = CreatePage(includeInMask: false)
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => translator.TranslateAsync(request));

        Assert.Contains("include_in_mask", exception.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        Assert.Equal(0, images.PageCallCount);
        Assert.Equal(0, images.FontReferenceCallCount);
    }

    [Fact]
    public async Task TranslateAsyncRejectsTwoLogicalLetteringUnitsSharingOneRegion()
    {
        var model = new SequenceModelProvider(CreateSplitRegionModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        var exception = await Assert.ThrowsAsync<DialogueTranslationException>(() =>
            translator.TranslateAsync(CreateRequest(
                images,
                new DialogueTranslationOptions { MaxModelAttempts = 1 })));

        Assert.Contains("more than one segment", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncAcceptsCompletePlacementGroupsForOneMultiCavityUtterance()
    {
        var model = new SequenceModelProvider(CreatePlacementGroupModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        var result = await translator.TranslateAsync(CreateRequest(images));

        var segment = Assert.Single(result.Segments);
        var advanced = Assert.IsType<DialogueAdvancedLayout>(segment.Layout.Advanced);
        Assert.Collection(
            advanced.PlacementGroups,
            first =>
            {
                Assert.Equal([0], first.LineIndices);
                Assert.Equal(["p0001:region:0001"], first.SourceRegionIds);
                Assert.Equal(new TextLayoutBox(92, 84, 356, 305), first.TextBox);
            },
            second =>
            {
                Assert.Equal([1], second.LineIndices);
                Assert.Equal(["p0001:region:0002"], second.SourceRegionIds);
                Assert.Equal(new TextLayoutBox(92, 305, 356, 530), second.TextBox);
            });
        Assert.True(segment.NeedsPriorityReview);
        Assert.True(result.NeedsPriorityReview);
    }

    [Fact]
    public async Task TranslateAsyncAcceptsUngroupedGeometricallySeparatedRegionsForPriorityReview()
    {
        var model = new SequenceModelProvider(
            CreateSpatiallySeparatedModelJson(includePlacementGroups: false));
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(
            images,
            new DialogueTranslationOptions
            {
                MaxModelAttempts = 1,
                RetryBaseDelay = TimeSpan.Zero
            }) with
        {
            Page = CreateSpatiallySeparatedPage()
        };

        var result = await translator.TranslateAsync(request);

        var segment = Assert.Single(result.Segments);
        Assert.Empty(Assert.IsType<DialogueAdvancedLayout>(
            segment.Layout.Advanced).PlacementGroups);
        Assert.True(segment.NeedsPriorityReview);
        Assert.True(result.NeedsPriorityReview);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData("empty_lines", "at least one line_index")]
    [InlineData("single_group", "at least two placement groups")]
    [InlineData("duplicate_line_in_group", "repeats translated line")]
    [InlineData("duplicate_line_across_groups", "more than one placement group")]
    [InlineData("missing_line", "do not cover translated line")]
    [InlineData("noncontiguous_lines", "ascending and contiguous")]
    [InlineData("groups_out_of_order", "increasing minimum line_index")]
    [InlineData("duplicate_region_in_group", "empty or duplicate source_region_id")]
    [InlineData("duplicate_region_across_groups", "more than one placement group")]
    [InlineData("missing_region", "do not assign source region")]
    [InlineData("region_not_owned_by_segment", "not owned by the segment")]
    [InlineData("box_outside_page", "inside both the page and the segment union")]
    [InlineData("box_outside_union", "inside both the page and the segment union")]
    [InlineData("box_not_intersecting_region", "does not intersect its referenced region_id")]
    [InlineData("overlapping_boxes", "materially overlapping text boxes")]
    [InlineData("group_overflow", "placement_groups[0].text_box")]
    [InlineData("explicit_stroke_overflow", "placement_groups[0].text_box")]
    [InlineData("automatic_contrast_stroke_overflow", "placement_groups[0].text_box")]
    [InlineData("offset_overflow", "placement_groups[0].text_box")]
    [InlineData("ruby_crosses_groups", "ruby range crosses placement groups")]
    [InlineData("null_placement_groups", "Advanced layout collections cannot be null")]
    public async Task TranslateAsyncRejectsInvalidPlacementGroups(
        string invalidKind,
        string expectedMessage)
    {
        var model = new SequenceModelProvider(
            CreateInvalidPlacementGroupModelJson(invalidKind));
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        var exception = await Assert.ThrowsAsync<DialogueTranslationException>(() =>
            translator.TranslateAsync(CreateRequest(
                images,
                new DialogueTranslationOptions { MaxModelAttempts = 1 })));

        Assert.Contains(
            expectedMessage,
            exception.InnerException?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncDoesNotRecoverTextWhenDetectionRegionListIsEmpty()
    {
        var model = new SequenceModelProvider(CreateNoDetectionModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(images) with
        {
            Page = CreatePage(includeInMask: true) with { DetectedTextRegions = [] }
        };

        var result = await translator.TranslateAsync(request);

        Assert.Empty(result.Segments);
        Assert.Empty(result.IgnoredRegions);
        var outputFormat = Assert.IsType<JsonSchemaOutputFormat>(Assert.Single(model.Requests).OutputFormat);
        var regionIdsSchema = outputFormat.Schema
            .GetProperty("properties")
            .GetProperty("segments")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("source_region_ids");
        Assert.Equal(1, regionIdsSchema.GetProperty("minItems").GetInt32());
        var regionItemSchema = regionIdsSchema.GetProperty("items");
        Assert.Equal("string", regionItemSchema.GetProperty("type").GetString());
        Assert.False(regionItemSchema.TryGetProperty("enum", out _));
        var segmentSchema = outputFormat.Schema
            .GetProperty("properties")
            .GetProperty("segments")
            .GetProperty("items");
        Assert.Equal(
            "boolean",
            segmentSchema
                .GetProperty("properties")
                .GetProperty("needs_priority_review")
                .GetProperty("type")
                .GetString());
        Assert.Contains(
            "needs_priority_review",
            segmentSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!));

        var advancedSchema = outputFormat.Schema
            .GetProperty("properties")
            .GetProperty("segments")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("layout")
            .GetProperty("properties")
            .GetProperty("advanced");
        Assert.Contains(
            "placement_groups",
            advancedSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!));
        var placementGroupSchema = advancedSchema
            .GetProperty("properties")
            .GetProperty("placement_groups")
            .GetProperty("items");
        Assert.Equal(
            ["line_indices", "source_region_ids", "text_box"],
            placementGroupSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        foreach (var propertyName in new[] { "line_indices", "source_region_ids" })
        {
            var arraySchema = placementGroupSchema
                .GetProperty("properties")
                .GetProperty(propertyName);
            Assert.Equal(1, arraySchema.GetProperty("minItems").GetInt32());
            // The strict wire schema intentionally omits uniqueItems for provider compatibility;
            // ModelBackedMultimodalDialogueTranslator.ValidatePlacementGroups enforces uniqueness.
            Assert.False(arraySchema.TryGetProperty("uniqueItems", out _));
        }
    }

    [Fact]
    public async Task TranslateAsyncKeepsStablePrefixSchemaAndCacheIdentityAcrossPages()
    {
        var firstModel = new SequenceModelProvider(CreateValidModelJson());
        var secondModel = new SequenceModelProvider(CreateSecondPageModelJson());
        var firstImages = new RecordingImageProvider("p0001");
        var secondImages = new RecordingImageProvider("p0002");
        var firstTranslator = new ModelBackedMultimodalDialogueTranslator(firstModel);
        var secondTranslator = new ModelBackedMultimodalDialogueTranslator(secondModel);
        var firstRequest = CreateRequest(firstImages);
        var secondRequest = CreateRequest(secondImages) with
        {
            RunId = "dialogue_unit_run_second",
            Page = CreateSecondPage(),
            PageImages = secondImages,
            FontReferenceImages = secondImages
        };

        await firstTranslator.TranslateAsync(firstRequest);
        await secondTranslator.TranslateAsync(secondRequest);

        var firstModelRequest = Assert.Single(firstModel.Requests);
        var secondModelRequest = Assert.Single(secondModel.Requests);
        var firstUser = Assert.Single(firstModelRequest.Messages, message =>
            message.Role == ModelRole.User);
        var secondUser = Assert.Single(secondModelRequest.Messages, message =>
            message.Role == ModelRole.User);
        Assert.Equal(
            Assert.IsType<TextContent>(firstUser.Content[0]).Text,
            Assert.IsType<TextContent>(secondUser.Content[0]).Text);
        Assert.NotEqual(
            Assert.IsType<TextContent>(firstUser.Content[1]).Text,
            Assert.IsType<TextContent>(secondUser.Content[1]).Text);
        Assert.Equal(
            Assert.IsType<TextContent>(firstUser.Content[4]).Text,
            Assert.IsType<TextContent>(secondUser.Content[4]).Text);
        Assert.Equal(
            Assert.IsType<ImageContent>(firstUser.Content[5]).Data.ToArray(),
            Assert.IsType<ImageContent>(secondUser.Content[5]).Data.ToArray());
        using var firstPageDocument = JsonDocument.Parse(RequestJsonText(
            firstModelRequest,
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n"));
        using var secondPageDocument = JsonDocument.Parse(RequestJsonText(
            secondModelRequest,
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n"));
        Assert.Equal(
            "dialogue_unit_run",
            firstPageDocument.RootElement.GetProperty("run_id").GetString());
        Assert.Equal(
            "dialogue_unit_run_second",
            secondPageDocument.RootElement.GetProperty("run_id").GetString());
        Assert.StartsWith(
            "PAGE-LEVEL DIALOGUE MANIFEST JSON:\n",
            Assert.IsType<TextContent>(firstUser.Content[1]).Text,
            StringComparison.Ordinal);

        var firstSchema = Assert.IsType<JsonSchemaOutputFormat>(
            firstModelRequest.OutputFormat).Schema.GetRawText();
        var secondSchema = Assert.IsType<JsonSchemaOutputFormat>(
            secondModelRequest.OutputFormat).Schema.GetRawText();
        Assert.Equal(firstSchema, secondSchema);
        Assert.Equal(
            firstModelRequest.Options.PromptCacheKey,
            secondModelRequest.Options.PromptCacheKey);
        Assert.Matches(
            "^dlg_[0-9a-f]{24}$",
            Assert.IsType<string>(firstModelRequest.Options.PromptCacheKey));
        Assert.Equal(
            PromptCacheRetention.TwentyFourHours,
            firstModelRequest.Options.PromptCacheRetention);
        Assert.Equal(
            PromptCacheRetention.TwentyFourHours,
            secondModelRequest.Options.PromptCacheRetention);
    }

    [Fact]
    public async Task TranslateAsyncAllowsPromptCacheOverrideOrDisable()
    {
        var overrideModel = new SequenceModelProvider(CreateValidModelJson());
        var overrideImages = new RecordingImageProvider();
        var overrideTranslator = new ModelBackedMultimodalDialogueTranslator(overrideModel);
        var overrideRequest = CreateRequest(
            overrideImages,
            new DialogueTranslationOptions
            {
                PromptCacheKey = "compat-endpoint-cache-key",
                PromptCacheRetention = PromptCacheRetention.InMemory
            });

        await overrideTranslator.TranslateAsync(overrideRequest);

        var overrideOptions = Assert.Single(overrideModel.Requests).Options;
        Assert.Equal("compat-endpoint-cache-key", overrideOptions.PromptCacheKey);
        Assert.Equal(PromptCacheRetention.InMemory, overrideOptions.PromptCacheRetention);

        var disabledModel = new SequenceModelProvider(CreateValidModelJson());
        var disabledImages = new RecordingImageProvider();
        var disabledTranslator = new ModelBackedMultimodalDialogueTranslator(disabledModel);
        var disabledRequest = CreateRequest(
            disabledImages,
            new DialogueTranslationOptions
            {
                EnablePromptCaching = false,
                PromptCacheKey = "must-be-ignored"
            });

        await disabledTranslator.TranslateAsync(disabledRequest);

        var disabledOptions = Assert.Single(disabledModel.Requests).Options;
        Assert.Null(disabledOptions.PromptCacheKey);
        Assert.Null(disabledOptions.PromptCacheRetention);
    }

    [Fact]
    public async Task TranslateAsyncAddsValidationFeedbackToSecondModelRequest()
    {
        var model = new SequenceModelProvider(
            CreateInvalidModelJson("invalid_font_id"),
            CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(
            images,
            new DialogueTranslationOptions
            {
                MaxModelAttempts = 2,
                RetryBaseDelay = TimeSpan.Zero
            });

        await translator.TranslateAsync(request);

        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain(
            "RETRY_VALIDATION_FEEDBACK",
            RequestText(model.Requests[0]),
            StringComparison.Ordinal);
        var retryText = RequestText(model.Requests[1]);
        Assert.Contains("RETRY_VALIDATION_FEEDBACK", retryText, StringComparison.Ordinal);
        Assert.Contains("Unknown font_id 'F9999'", retryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsyncClassifiesPlacementGroupRepairAsGrouping()
    {
        var model = new SequenceModelProvider(
            CreateInvalidPlacementGroupModelJson("missing_line"),
            CreatePlacementGroupModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(
            images,
            new DialogueTranslationOptions
            {
                MaxModelAttempts = 2,
                RetryBaseDelay = TimeSpan.Zero
            });

        await translator.TranslateAsync(request);

        Assert.Equal(2, model.Requests.Count);
        var retryText = RequestText(model.Requests[1]);
        Assert.Contains("CATEGORY=grouping", retryText, StringComparison.Ordinal);
        Assert.Contains("do not cover translated line", retryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("character_future")]
    [InlineData("character_expired")]
    public async Task TranslateAsyncRetriesSpeakerOutsideCurrentValidityInterval(
        string invalidSpeakerId)
    {
        var model = new SequenceModelProvider(
            CreateSpeakerModelJson(invalidSpeakerId),
            CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(
            images,
            new DialogueTranslationOptions
            {
                MaxModelAttempts = 2,
                RetryBaseDelay = TimeSpan.Zero
            }) with
        {
            Page = CreatePage(includeInMask: true) with { Order = 2 },
            BookKnowledge = CreateBookKnowledgeWithTemporalCharacters(),
            BookPageIndex = [new DialogueBookPageIndexEntry("p0001", 2)]
        };

        var result = await translator.TranslateAsync(request);

        Assert.Equal("character_001", Assert.Single(result.Segments).SpeakerId);
        Assert.Equal(2, model.Requests.Count);
        var retryText = RequestText(model.Requests[1]);
        Assert.Contains(invalidSpeakerId, retryText, StringComparison.Ordinal);
        Assert.Contains("not valid at page.order 2", retryText, StringComparison.Ordinal);
        Assert.Contains(
            "valid_from_order/valid_to_order",
            retryText,
            StringComparison.Ordinal);
        Assert.Contains(
            "eligible_speaker_ids",
            retryText,
            StringComparison.Ordinal);
        Assert.Contains(
            "set speaker_id=null",
            retryText,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate_page_id")]
    [InlineData("duplicate_order")]
    [InlineData("missing_current_page")]
    [InlineData("mismatched_current_order")]
    public async Task TranslateAsyncRejectsInvalidNonEmptyBookPageIndexBeforeExternalCalls(
        string invalidIndexKind)
    {
        IReadOnlyList<DialogueBookPageIndexEntry> invalidIndex = invalidIndexKind switch
        {
            "duplicate_page_id" =>
            [
                new DialogueBookPageIndexEntry("p0001", 1),
                new DialogueBookPageIndexEntry("p0001", 2)
            ],
            "duplicate_order" =>
            [
                new DialogueBookPageIndexEntry("p0001", 1),
                new DialogueBookPageIndexEntry("p0002", 1)
            ],
            "missing_current_page" =>
            [new DialogueBookPageIndexEntry("p0002", 2)],
            "mismatched_current_order" =>
            [new DialogueBookPageIndexEntry("p0001", 2)],
            _ => throw new ArgumentOutOfRangeException(
                nameof(invalidIndexKind),
                invalidIndexKind,
                null)
        };
        var model = new SequenceModelProvider(CreateValidModelJson());
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);
        var request = CreateRequest(images) with { BookPageIndex = invalidIndex };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => translator.TranslateAsync(request));

        Assert.Contains("BookPageIndex", exception.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        Assert.Equal(0, images.PageCallCount);
        Assert.Equal(0, images.FontReferenceCallCount);
    }

    [Fact]
    public async Task TranslateAsyncRejectsProviderWithoutStreamingBeforeFetchingImages()
    {
        var model = new SequenceModelProvider(CreateValidModelJson())
        {
            Capabilities = new ModelProviderCapabilities(
                SupportsVision: true,
                SupportsMultipleImages: true,
                SupportsStructuredOutputs: true,
                SupportsStreaming: false,
                SupportsOriginalImageDetail: true,
                SupportsReasoningConfiguration: true)
        };
        var images = new RecordingImageProvider();
        var translator = new ModelBackedMultimodalDialogueTranslator(model);

        var exception = await Assert.ThrowsAsync<DialogueTranslationException>(
            () => translator.TranslateAsync(CreateRequest(images)));

        Assert.Contains("streaming", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.Requests);
        Assert.Equal(0, images.PageCallCount);
        Assert.Equal(0, images.FontReferenceCallCount);
    }

    [Theory]
    [InlineData("duplicate_region")]
    [InlineData("out_of_range_advanced_index")]
    [InlineData("invalid_font_id")]
    [InlineData("invalid_font_weight")]
    [InlineData("invalid_rotation")]
    [InlineData("invalid_stroke")]
    [InlineData("unknown_region_id")]
    [InlineData("unknown_speaker_id")]
    [InlineData("empty_source_region_ids")]
    [InlineData("unanchored_merged_region")]
    public async Task TranslateAsyncRetriesInvalidModelLayoutsAndAcceptsCorrectedOutput(
        string invalidOutputKind)
    {
        var model = new SequenceModelProvider(
            CreateInvalidModelJson(invalidOutputKind),
            CreateValidModelJson());
        var images = new RecordingImageProvider();
        var progress = new RecordingProgressReporter();
        var translator = new ModelBackedMultimodalDialogueTranslator(model, progress);
        var request = CreateRequest(
            images,
            new DialogueTranslationOptions
            {
                MaxModelAttempts = 2,
                RetryBaseDelay = TimeSpan.Zero
            });

        var result = await translator.TranslateAsync(request);

        Assert.Single(result.Segments);
        Assert.True(result.NeedsPriorityReview);
        Assert.Equal(2, model.Requests.Count);
        var retry = Assert.Single(progress.Events, item => item.Action == "retry_scheduled");
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(2, retry.MaximumAttempts);
        Assert.Equal("invalid_dialogue_output", retry.ErrorCode);
        var retryText = RequestText(model.Requests[1]);
        Assert.Contains(
            "keep one logical lettering unit per segment",
            retryText,
            StringComparison.Ordinal);
        if (invalidOutputKind is "duplicate_region" or "unknown_region_id" or "empty_source_region_ids")
        {
            Assert.Contains(
                "visually connected multi-lobed balloon",
                retryText,
                StringComparison.Ordinal);
            Assert.Contains(
                "Never recover OCR outside supplied regions",
                retryText,
                StringComparison.Ordinal);
        }
        Assert.Equal(1, images.PageCallCount);
        Assert.Equal(1, images.FontReferenceCallCount);
    }

    private static DialogueTranslationRequest CreateRequest(
        RecordingImageProvider images,
        DialogueTranslationOptions? options = null)
    {
        return new DialogueTranslationRequest(
            "dialogue_unit_run",
            "book_unit",
            CreatePage(includeInMask: true),
            CreateBookKnowledge(),
            images,
            images,
            options ?? new DialogueTranslationOptions())
        {
            // Deliberately unsorted: the stable book-level prefix must canonicalize this index.
            BookPageIndex =
            [
                new DialogueBookPageIndexEntry("p0003", 3),
                new DialogueBookPageIndexEntry("p0001", 1),
                new DialogueBookPageIndexEntry("p0002", 2)
            ]
        };
    }

    private static DialoguePageInput CreatePage(bool includeInMask)
    {
        return new DialoguePageInput(
            "p0001",
            1,
            "c001",
            "c001:scene_001",
            "body",
            includeInMask,
            1200,
            1800,
            "image/png",
            [
                Region("p0001:region:0001", 0, 100, 100, 320, 300),
                Region("p0001:region:0002", 1, 100, 310, 320, 500),
                Region("p0001:region:0003", 2, 800, 1200, 900, 1300)
            ]);
    }

    private static DialoguePageInput CreateSecondPage()
    {
        return new DialoguePageInput(
            "p0002",
            2,
            "c001",
            "c001:scene_001",
            "body",
            true,
            1200,
            1800,
            "image/png",
            [
                Region("p0002:region:0101", 0, 140, 130, 360, 330),
                Region("p0002:region:0102", 1, 140, 340, 360, 550),
                Region("p0002:region:0103", 2, 760, 1180, 880, 1320)
            ]);
    }

    private static DialoguePageInput CreateSpatiallySeparatedPage()
    {
        return CreatePage(includeInMask: true) with
        {
            DetectedTextRegions =
            [
                Region("p0001:region:0001", 0, 600, 100, 820, 300),
                Region("p0001:region:0002", 1, 100, 100, 320, 300),
                Region("p0001:region:0003", 2, 800, 1200, 900, 1300)
            ]
        };
    }

    private static DialogueDetectedRegion Region(
        string id,
        int detectionIndex,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        return new DialogueDetectedRegion(
            id,
            detectionIndex,
            new DetectedTextRegion(
                x0,
                y0,
                x1,
                y1,
                DetectedTextLanguage.Japanese,
                DetectedTextOrientation.Vertical,
                28,
                0.96f,
                [
                    new DetectedTextLine(
                        [
                            new ImagePoint(x0, y0),
                            new ImagePoint(x1, y0),
                            new ImagePoint(x1, y1),
                            new ImagePoint(x0, y1)
                        ],
                        0.95f)
                ]));
    }

    private static GlobalSemanticMergeResult CreateBookKnowledge(
        string testMarker = "default_book_knowledge")
    {
        return new GlobalSemanticMergeResult(
            "zh-Hans",
            Json("[]"),
            Json("[{\"character_id\":\"character_001\"}]"),
            Json("[]"),
            Json("[]"),
            JsonSerializer.SerializeToElement(new
            {
                translation_policy = new { target_language = "zh-Hans" },
                test_marker = testMarker
            }));
    }

    private static GlobalSemanticMergeResult CreateBookKnowledgeWithTemporalCharacters()
    {
        return new GlobalSemanticMergeResult(
            "zh-Hans",
            Json("[]"),
            Json("""
                [
                  {
                    "character_id": "character_001",
                    "valid_from_order": 1,
                    "valid_to_order": null
                  },
                  {
                    "character_id": "character_future",
                    "valid_from_order": 3,
                    "valid_to_order": null,
                    "revealed_order": 1
                  },
                  {
                    "character_id": "character_expired",
                    "valid_from_order": 1,
                    "valid_to_order": 1
                  }
                ]
                """),
            Json("[]"),
            Json("[]"),
            Json("{}"));
    }

    private static string CreateValidModelJson()
    {
        var segment = new DialogueSegment(
            "model-segment-a",
            DialogueSegmentKind.Dialogue,
            DialogueSegmentStatus.Translated,
            8,
            ["p0001:region:0001", "p0001:region:0002"],
            "お疲れさま。\n続けましょう。",
            "今天辛苦了\r\n我们继续吧",
            new DialogueTextLayout(
                new TextLayoutBox(92, 84, 356, 530),
                DialogueTextOrientation.Vertical,
                DialogueReadingDirection.RightToLeft,
                DialogueTextAlignment.Center,
                "f0001",
                34,
                false,
                false,
                "#202020",
                1.5f,
                new DialogueAdvancedLayout(
                    [
                        new DialogueLineAdjustment(0, 2, -1, 33, "#202020"),
                        new DialogueLineAdjustment(1, -4, 3, 34, "#202020")
                    ],
                    [new DialogueCharacterAdjustment(2, -2, 1, 32, "#303030")],
                    [new DialogueRubyAnnotation(
                        0,
                        2,
                        "jīntiān",
                        DialogueRubyPlacement.Before,
                        0,
                        -12,
                        14,
                        "#202020")])),
            "character_001",
            0.96,
            []);
        var ignored = new DialogueIgnoredRegion(
            ["p0001:region:0003"],
            "装饰性笔画",
            "该孤立笔画属于装饰元素，不是可读文字。");

        return JsonSerializer.Serialize(
            new
            {
                segments = new[] { segment },
                ignored_regions = new[] { ignored },
                warnings = Array.Empty<DialogueWarning>()
            },
            ModelJsonOptions);
    }

    private static string CreateInvalidModelJson(string kind)
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var segment = root["segments"]![0]!.AsObject();
        switch (kind)
        {
            case "duplicate_region":
                segment["source_region_ids"] = new JsonArray(
                    "p0001:region:0001",
                    "p0001:region:0001");
                break;
            case "out_of_range_advanced_index":
                segment["layout"]!["advanced"]!["line_adjustments"]![0]!["line_index"] = 2;
                break;
            case "invalid_font_id":
                segment["layout"]!["font_id"] = "F9999";
                break;
            case "invalid_font_weight":
                segment["layout"]!["font_weight"] = 0;
                break;
            case "invalid_rotation":
                segment["layout"]!["rotation"] = new JsonObject
                {
                    ["center_x_px"] = 120,
                    ["center_y_px"] = 220,
                    ["angle_deg"] = 1e100
                };
                break;
            case "invalid_stroke":
                segment["layout"]!["stroke"] = new JsonObject
                {
                    ["width_px"] = -1,
                    ["color"] = "#FFFFFF",
                    ["round_corners"] = true
                };
                break;
            case "unknown_region_id":
                segment["source_region_ids"]![0] = "p0001:region:9999";
                break;
            case "unknown_speaker_id":
                segment["speaker_id"] = "character_not_registered";
                break;
            case "empty_source_region_ids":
                segment["source_region_ids"] = new JsonArray();
                break;
            case "unanchored_merged_region":
                segment["layout"]!["text_box"]!["y1"] = 305;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreateSpeakerModelJson(string speakerId)
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        root["segments"]![0]!["speaker_id"] = speakerId;
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreateSecondPageModelJson()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        root["segments"]![0]!["source_region_ids"] = new JsonArray(
            "p0002:region:0101",
            "p0002:region:0102");
        root["ignored_regions"]![0]!["region_ids"] = new JsonArray(
            "p0002:region:0103");
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreateSplitRegionModelJson()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var segments = root["segments"]!.AsArray();
        var first = segments[0]!.AsObject();
        first["reading_order"] = 3;
        first["source_region_ids"] = new JsonArray("p0001:region:0001");
        first["source_text"] = "お疲れさま。";
        first["translated_text"] = "今天辛苦了";
        first["layout"]!["advanced"]!["line_adjustments"] = new JsonArray(
            first["layout"]!["advanced"]!["line_adjustments"]![0]!.DeepClone());

        var second = first.DeepClone().AsObject();
        second["segment_id"] = "model-segment-b";
        second["reading_order"] = 8;
        second["source_region_ids"] = new JsonArray(
            "p0001:region:0001",
            "p0001:region:0002");
        second["source_text"] = "続けましょう。";
        second["translated_text"] = "我们继续吧";
        second["layout"]!["text_box"]!["y0"] = 300;
        second["layout"]!["text_box"]!["y1"] = 560;
        segments.Add(second);
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreatePlacementGroupModelJson()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var advanced = root["segments"]![0]!["layout"]!["advanced"]!.AsObject();
        advanced["placement_groups"] = new JsonArray(
            PlacementGroup(
                [0],
                ["p0001:region:0001"],
                new TextLayoutBox(92, 84, 356, 305)),
            PlacementGroup(
                [1],
                ["p0001:region:0002"],
                new TextLayoutBox(92, 305, 356, 530)));
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreateSpatiallySeparatedModelJson(bool includePlacementGroups)
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        var layout = root["segments"]![0]!["layout"]!.AsObject();
        layout["text_box"] = TextBox(92, 84, 840, 530);
        var advanced = layout["advanced"]!.AsObject();
        advanced["placement_groups"] = includePlacementGroups
            ? new JsonArray(
                PlacementGroup(
                    [0],
                    ["p0001:region:0001"],
                    new TextLayoutBox(580, 84, 840, 320)),
                PlacementGroup(
                    [1],
                    ["p0001:region:0002"],
                    new TextLayoutBox(92, 84, 356, 320)))
            : new JsonArray();
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string CreateInvalidPlacementGroupModelJson(string kind)
    {
        var root = JsonNode.Parse(CreatePlacementGroupModelJson())!.AsObject();
        var segment = root["segments"]![0]!.AsObject();
        var advanced = segment["layout"]!["advanced"]!.AsObject();
        var groups = advanced["placement_groups"]!.AsArray();
        var first = groups[0]!.AsObject();
        var second = groups[1]!.AsObject();
        switch (kind)
        {
            case "empty_lines":
                first["line_indices"] = new JsonArray();
                break;
            case "single_group":
                first["line_indices"] = new JsonArray(0, 1);
                first["source_region_ids"] = new JsonArray(
                    "p0001:region:0001",
                    "p0001:region:0002");
                first["text_box"] = TextBox(92, 84, 356, 530);
                groups.RemoveAt(1);
                break;
            case "duplicate_line_in_group":
                first["line_indices"] = new JsonArray(0, 0);
                break;
            case "duplicate_line_across_groups":
                segment["translated_text"] = "甲\n乙\n丙";
                first["line_indices"] = new JsonArray(0, 1);
                second["line_indices"] = new JsonArray(1, 2);
                break;
            case "missing_line":
                segment["translated_text"] = "甲\n乙\n丙";
                break;
            case "noncontiguous_lines":
                segment["translated_text"] = "甲\n乙\n丙";
                first["line_indices"] = new JsonArray(0, 2);
                second["line_indices"] = new JsonArray(1);
                break;
            case "groups_out_of_order":
                first["line_indices"] = new JsonArray(1);
                first["source_region_ids"] = new JsonArray("p0001:region:0002");
                first["text_box"] = TextBox(92, 305, 356, 530);
                second["line_indices"] = new JsonArray(0);
                second["source_region_ids"] = new JsonArray("p0001:region:0001");
                second["text_box"] = TextBox(92, 84, 356, 305);
                break;
            case "duplicate_region_in_group":
                first["source_region_ids"] = new JsonArray(
                    "p0001:region:0001",
                    "p0001:region:0001");
                break;
            case "duplicate_region_across_groups":
                second["source_region_ids"] = new JsonArray("p0001:region:0001");
                break;
            case "missing_region":
                segment["source_region_ids"] = new JsonArray(
                    "p0001:region:0001",
                    "p0001:region:0002",
                    "p0001:region:0003");
                root["ignored_regions"] = new JsonArray();
                break;
            case "region_not_owned_by_segment":
                second["source_region_ids"] = new JsonArray("p0001:region:0003");
                break;
            case "box_outside_page":
                first["text_box"] = TextBox(-1, 84, 356, 305);
                break;
            case "box_outside_union":
                first["text_box"] = TextBox(80, 84, 356, 305);
                break;
            case "box_not_intersecting_region":
                first["text_box"] = TextBox(320, 84, 356, 300);
                break;
            case "overlapping_boxes":
                first["text_box"] = TextBox(92, 84, 356, 450);
                second["text_box"] = TextBox(92, 200, 356, 530);
                break;
            case "group_overflow":
                first["text_box"] = TextBox(100, 100, 120, 120);
                break;
            case "explicit_stroke_overflow":
                first["text_box"] = TextBox(100, 100, 132, 305);
                segment["layout"]!["stroke"] = new JsonObject
                {
                    ["width_px"] = 1.5,
                    ["color"] = "#000000",
                    ["round_corners"] = true
                };
                break;
            case "automatic_contrast_stroke_overflow":
                first["text_box"] = TextBox(100, 100, 132, 305);
                advanced["line_adjustments"]![0]!["color"] = "#FFFFFF80";
                break;
            case "offset_overflow":
                advanced["line_adjustments"]![0]!["offset_y_px"] = 200;
                break;
            case "ruby_crosses_groups":
                segment["translated_text"] = "甲\n乙";
                advanced["character_adjustments"] = new JsonArray();
                advanced["ruby_annotations"]![0]!["base_character_index"] = 0;
                advanced["ruby_annotations"]![0]!["base_character_length"] = 2;
                break;
            case "null_placement_groups":
                advanced["placement_groups"] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return root.ToJsonString(ModelJsonOptions);
    }

    private static JsonObject PlacementGroup(
        int[] lineIndices,
        string[] sourceRegionIds,
        TextLayoutBox textBox) =>
        new()
        {
            ["line_indices"] = new JsonArray(
                lineIndices.Select(index => (JsonNode?)index).ToArray()),
            ["source_region_ids"] = new JsonArray(
                sourceRegionIds.Select(regionId => (JsonNode?)regionId).ToArray()),
            ["text_box"] = TextBox(
                textBox.X0,
                textBox.Y0,
                textBox.X1,
                textBox.Y1)
        };

    private static JsonObject TextBox(int x0, int y0, int x1, int y1) =>
        new()
        {
            ["x0"] = x0,
            ["y0"] = y0,
            ["x1"] = x1,
            ["y1"] = y1
        };

    private static string CreateNoDetectionModelJson()
    {
        var root = JsonNode.Parse(CreateValidModelJson())!.AsObject();
        root["segments"] = new JsonArray();
        root["ignored_regions"] = new JsonArray();
        return root.ToJsonString(ModelJsonOptions);
    }

    private static string RequestText(ModelRequest request)
    {
        return string.Join(
            "\n",
            request.Messages
                .SelectMany(message => message.Content)
                .OfType<TextContent>()
                .Select(content => content.Text));
    }

    private static string RequestJsonText(ModelRequest request, string prefix)
    {
        var userMessage = Assert.Single(request.Messages, message =>
            message.Role == ModelRole.User);
        var manifest = Assert.Single(
            userMessage.Content.OfType<TextContent>(),
            content => content.Text.StartsWith(prefix, StringComparison.Ordinal));
        return manifest.Text[prefix.Length..];
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(
            ' ',
            value.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions ModelJsonOptions { get; } = CreateModelJsonOptions();

    private static JsonSerializerOptions CreateModelJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private sealed class RecordingImageProvider(
        string expectedPageId = "p0001",
        int pageWidth = 1200,
        int pageHeight = 1800) : IDialogueImageProvider
    {
        public int PageCallCount { get; private set; }

        public int FontReferenceCallCount { get; private set; }

        public ValueTask<DialogueImageAsset> GetOriginalPageAsync(
            string pageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageCallCount++;
            Assert.Equal(expectedPageId, pageId);
            return ValueTask.FromResult(new DialogueImageAsset(
                $"page:{pageId}",
                "image/png",
                PageBytes,
                pageWidth,
                pageHeight));
        }

        public ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(
            FontReferenceImageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FontReferenceCallCount++;
            Assert.Equal("book_unit", request.BookId);
            Assert.Equal(expectedPageId, request.PageId);
            Assert.Equal("zh-Hans", request.TargetLanguage);
            return ValueTask.FromResult(new FontReferenceSnapshot(
                "font-reference-r0007",
                7,
                new DialogueImageAsset(
                    "font-reference-r0007:image",
                    "image/png",
                    FontReferenceBytes,
                    900,
                    600),
                [
                    new FontReferenceFont(
                        "F0001",
                        "Source Han Sans",
                        "Source Han Sans CN",
                        "sans-serif",
                        ["dialogue"],
                        [
                            new FontReferenceStyle(
                                "regular",
                                "native",
                                "regular",
                                "F0001 regular",
                                0,
                                [])
                        ]),
                    new FontReferenceFont(
                        "F0002",
                        "Source Han Serif",
                        "Source Han Serif CN",
                        "serif",
                        ["narration"],
                        [
                            new FontReferenceStyle(
                                "regular",
                                "native",
                                "regular",
                                "F0002 regular",
                                0,
                                [])
                        ])
                ]));
        }
    }

    private sealed class SequenceModelProvider(params string[] responses) : IModelProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public string ProviderId => "fake-model-provider";

        public ModelProviderCapabilities Capabilities { get; init; } = new(
            SupportsVision: true,
            SupportsMultipleImages: true,
            SupportsStructuredOutputs: true,
            SupportsStreaming: true,
            SupportsOriginalImageDetail: true,
            SupportsReasoningConfiguration: true);

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake model response remains.");
            }

            await Task.Yield();
            var responseId = $"fake-response-{Requests.Count:0000}";
            yield return new ResponseCreated(responseId);
            yield return new OutputTextDelta(_responses.Dequeue());
            yield return new UsageReported(new ModelUsage(1_000, 500, 1_500, 100, 250));
            yield return new ResponseCompleted(new ModelResponseMetadata(
                responseId,
                TimeSpan.FromMilliseconds(10),
                "stop"));
        }
    }

    private sealed class RecordingProgressReporter : IDialogueProgressReporter
    {
        public List<DialogueProgress> Events { get; } = [];

        public void Report(DialogueProgress progress)
        {
            Events.Add(progress);
        }
    }
}
