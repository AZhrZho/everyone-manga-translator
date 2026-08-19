using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Dialogue;

public sealed partial class ModelBackedMultimodalDialogueTranslator
    : IMultimodalDialogueTranslator
{
    private const float RendererClipSafetyInsetPx = 1.5f;
    private const float SpatialRegionLinkDistanceInFontSizes = 2f;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IModelProvider _modelProvider;
    private readonly IDialogueProgressReporter _progress;
    private readonly IDialoguePromptProvider _prompts;

    public ModelBackedMultimodalDialogueTranslator(
        IModelProvider modelProvider,
        IDialogueProgressReporter? progress = null)
        : this(modelProvider, ExpertDialoguePromptProvider.Instance, progress)
    {
    }

    public ModelBackedMultimodalDialogueTranslator(
        IModelProvider modelProvider,
        IDialoguePromptProvider prompts,
        IDialogueProgressReporter? progress = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _progress = progress ?? NullDialogueProgressReporter.Instance;
    }

    public async Task<DialogueTranslationResult> TranslateAsync(
        DialogueTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ValidateCapabilities(request.Options);

        var stopwatch = Stopwatch.StartNew();
        _progress.Report(new DialogueProgress(request.Page.PageId, "page_started"));
        var pageImage = await request.PageImages
            .GetOriginalPageAsync(request.Page.PageId, cancellationToken)
            .ConfigureAwait(false);
        var fontReference = await request.FontReferenceImages
            .GetFontReferenceImageAsync(
                new FontReferenceImageRequest(
                    request.BookId,
                    request.Page.PageId,
                    request.Options.TargetLanguage),
                cancellationToken)
            .ConfigureAwait(false);
        ValidateAssets(request.Page, pageImage, fontReference);

        var fontIds = fontReference.Fonts
            .Select(font => font.FontId)
            .ToArray();
        var maximumAttempts = Math.Max(1, request.Options.MaxModelAttempts);
        Exception? lastException = null;
        string? retryFeedback = null;
        var hadModelOutputRepair = false;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelRequest = BuildModelRequest(
                request,
                pageImage,
                fontReference,
                fontIds,
                retryFeedback);
            _progress.Report(new DialogueProgress(
                request.Page.PageId,
                "model_call_started",
                attempt,
                maximumAttempts,
                "2 images: original page + font reference"));
            try
            {
                var response = await _modelProvider
                    .GenerateAsync(modelRequest, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCompleted(response, request.Page.PageId);
                var envelope = ParseResponse(response);
                var mapped = ValidateAndMap(
                    request,
                    fontReference,
                    envelope,
                    response,
                    _prompts.ProfileId.StartsWith("quick-", StringComparison.Ordinal));
                ValidatePromptProfileResult(mapped);
                mapped = ApplyPriorityReviewSignals(mapped, hadModelOutputRepair);
                _progress.Report(new DialogueProgress(
                    request.Page.PageId,
                    "page_completed",
                    attempt,
                    maximumAttempts,
                    $"{mapped.Segments.Count} segments; "
                    + $"{mapped.IgnoredRegions.Count} ignored groups",
                    stopwatch.Elapsed));
                return mapped;
            }
            catch (ModelProviderException exception) when (
                exception.Error.IsRetryable && attempt < maximumAttempts)
            {
                lastException = exception;
                retryFeedback = null;
                await ScheduleRetryAsync(
                    request,
                    attempt,
                    maximumAttempts,
                    exception.Error.Code,
                    exception.Error.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception) when (attempt < maximumAttempts)
            {
                lastException = exception;
                hadModelOutputRepair = true;
                retryFeedback = "The previous attempt did not produce valid JSON. "
                    + "Return one complete replacement object that conforms to the schema.";
                await ScheduleRetryAsync(
                    request,
                    attempt,
                    maximumAttempts,
                    "invalid_json",
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DialogueModelOutputException exception) when (attempt < maximumAttempts)
            {
                lastException = exception;
                hadModelOutputRepair = true;
                retryFeedback = BuildRetryFeedback(exception);
                await ScheduleRetryAsync(
                    request,
                    attempt,
                    maximumAttempts,
                    "invalid_dialogue_output",
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ModelProviderException exception)
            {
                throw new DialogueTranslationException(
                    $"The model provider failed dialogue translation for {request.Page.PageId} "
                    + $"[{exception.Error.Code}]: {exception.Error.Message}",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new DialogueTranslationException(
                    $"The model returned invalid dialogue JSON for {request.Page.PageId}.",
                    exception);
            }
            catch (DialogueModelOutputException exception)
            {
                throw new DialogueTranslationException(
                    $"The model returned an invalid dialogue layout for "
                    + $"{request.Page.PageId}: {exception.Message}",
                    exception);
            }
        }

        throw new DialogueTranslationException(
            $"Dialogue translation for {request.Page.PageId} exhausted "
            + $"{maximumAttempts} attempts.",
            lastException ?? new InvalidOperationException("No model attempt was recorded."));
    }

    private void ValidatePromptProfileResult(DialogueTranslationResult result)
    {
        if (!_prompts.ProfileId.StartsWith("quick-", StringComparison.Ordinal)) return;

        var segment = result.Segments.FirstOrDefault(item =>
            item.Layout.Advanced?.PlacementGroups.Count > 0);
        if (segment is not null)
        {
            throw new DialogueModelOutputException(
                $"[grouping] Quick Mode segment '{segment.SegmentId}' must use one render cavity; "
                + "advanced.placement_groups must be empty and separated cavities must be split into segments.");
        }
    }

    private static DialogueTranslationResult ApplyPriorityReviewSignals(
        DialogueTranslationResult result,
        bool hadModelOutputRepair)
    {
        var warningSegmentIds = result.Warnings
            .Where(warning => warning.SegmentId is not null)
            .Select(warning => warning.SegmentId!)
            .ToHashSet(StringComparer.Ordinal);
        var segments = result.Segments
            .Select(segment => segment with
            {
                NeedsPriorityReview = segment.NeedsPriorityReview
                    || segment.Status == DialogueSegmentStatus.Unreadable
                    || segment.Confidence < 0.9
                    || warningSegmentIds.Contains(segment.SegmentId)
                    || segment.Layout.Advanced?.PlacementGroups.Count > 0
            })
            .ToArray();
        return result with
        {
            Segments = segments,
            NeedsPriorityReview = hadModelOutputRepair
                || result.Warnings.Count > 0
                || segments.Any(segment => segment.NeedsPriorityReview)
        };
    }

    private string BuildRetryFeedback(DialogueModelOutputException exception)
    {
        var message = exception.Message;
        var lower = message.ToLowerInvariant();
        var category = lower.Contains("speaker_id", StringComparison.Ordinal)
            || lower.Contains("speaker", StringComparison.Ordinal)
            ? "speaker"
            : lower.Contains("fit", StringComparison.Ordinal)
            || lower.Contains("overflow", StringComparison.Ordinal)
            ? "fit"
            : lower.Contains("anchor", StringComparison.Ordinal)
                || lower.Contains("intersect", StringComparison.Ordinal)
                ? "anchor"
                : lower.Contains("grouping", StringComparison.Ordinal)
                    || lower.Contains("placement group", StringComparison.Ordinal)
                    || lower.Contains("region_id", StringComparison.Ordinal)
                    || lower.Contains("container", StringComparison.Ordinal)
                    ? "grouping"
                    : lower.Contains("overlap", StringComparison.Ordinal)
                        || lower.Contains("duplicate", StringComparison.Ordinal)
                        ? "overlap"
                        : "schema";
        var categoryGuidance = category switch
        {
            "speaker" => "For speaker repair, re-evaluate each bubble independently and set speaker_id=null "
                + "unless the page manifest's eligible_speaker_ids and the page geometry both support it; "
                + "never carry a future or merely plausible character ID into this page.",
            "grouping" when _prompts.ProfileId.StartsWith("quick-", StringComparison.Ordinal) =>
                "For Quick Mode grouping repair, every segment must use exactly one effective layout.text_box. "
                + "Set advanced.placement_groups=[] in every advanced object. Never return a non-empty placement "
                + "group list. Split separated cavities or connected lobes into independent segments with disjoint "
                + "source_region_ids, one complete translated string and one tight box per segment. Keep every "
                + "line_adjustments.line_index inside that segment's explicit translated_text line count.",
            "grouping" => "For grouping repair, keep one logical lettering unit per segment and cite at least "
                + "one LocalDetection region for every segment. Merge a visually connected multi-lobed balloon "
                + "only when it contains one continuous semantic paragraph from one speaker turn; use explicit "
                + "line or column breaks and advanced.placement_groups to partition every translated line and "
                + "source region exactly once into tight, non-overlapping cavity boxes. "
                + "A segment spanning geometrically separate region clusters MUST NOT return advanced=null or "
                + "placement_groups=[]. Either split it into separate semantic segments or return an advanced "
                + "object shaped as {line_adjustments:[], character_adjustments:[], ruby_annotations:[], "
                + "placement_groups:[{line_indices:[...], source_region_ids:[...], "
                + "text_box:{x0:...,y0:...,x1:...,y1:...}}, ...]}. Use one tight group per physical cavity and "
                + "do not repeat the rejected union-box candidate. "
                + "Split different semantic paragraphs, speaker turns, and all disconnected bubbles, panels, "
                + "captions, or SFX into separate segments. Never recover OCR outside supplied regions.",
            "overlap" => "For overlap repair, remove only accidental duplicate ownership; retain repeated text "
                + "when it occurs in different physical positions.",
            "anchor" => "For anchor repair, re-locate each bbox independently on the original image, verify "
                + "source_text against the glyph pixels at that exact position, and make text_box intersect "
                + "every cited region in the logical lettering unit. For a connected multi-lobed unit, keep "
                + "each placement_group.text_box inside the segment union and intersecting all regions assigned "
                + "to that cavity.",
            "fit" => "For fit repair, preserve meaning and logical-unit ownership while rebreaking or resizing "
                + "inside the same logical lettering unit until every line or column fits its intended container "
                + "or connected lobe. Treat the reported required width/height as a hard lower bound: the effective "
                + "text box must be at least that large after stroke and spacing. For narrow vertical regions, use "
                + "one explicit newline-delimited column per cavity and set font_size_px no larger than the cavity "
                + "width minus stroke/margin; never leave a long translated string to implicit wrapping or clipping. "
                + "If a candidate needs more width than the region, shorten/rebreak it or expand only into the "
                + "same visible bubble, never into a neighboring region.",
            _ => "Repair the reported schema or layout constraint without weakening the logical-lettering-unit contract."
        };

        return "The previous candidate failed the mechanical host audit. "
            + $"CATEGORY={category}; ERROR={message} "
            + "Return one complete replacement JSON object. Re-inspect the original page, keep one logical "
            + "lettering unit per segment, stay inside LocalDetection scope, re-anchor every cited region "
            + "and text_box, preserve legitimate "
            + "same-text occurrences at different positions, and repair the listed category before returning. "
            + categoryGuidance;
    }

    private ModelRequest BuildModelRequest(
        DialogueTranslationRequest request,
        DialogueImageAsset pageImage,
        FontReferenceSnapshot fontReference,
        IReadOnlyCollection<string> fontIds,
        string? retryFeedback)
    {
        var systemPrompt = _prompts.System(request.Options.TargetLanguage);
        var bookLevelContext = _prompts.Context(
            request,
            fontReference);
        var outputSchema = DialogueSchemas.Result(fontIds);
        var userContent = new List<ModelContent>
        {
            new TextContent(bookLevelContext),
            new TextContent(_prompts.PageManifest(
                request,
                pageImage)),
            new TextContent(
                $"ORIGINAL_PAGE_IMAGE PAGE_ID={request.Page.PageId}; "
                + $"SIZE={request.Page.Width}x{request.Page.Height}; "
                + "authoritative for layout and for glyph verification only inside supplied "
                + "LocalDetection regions/mask; do not expand OCR scope."),
            new ImageContent(
                pageImage.Data,
                pageImage.MediaType,
                request.Options.PageImageDetail),
            new TextContent(
                $"FONT_REFERENCE_IMAGE REFERENCE_ID={fontReference.ReferenceId}; "
                + $"LIBRARY_REVISION={fontReference.LibraryRevision}; "
                + "use only during the lettering pass for font_id and supported style selection."),
            new ImageContent(
                fontReference.Image.Data,
                fontReference.Image.MediaType,
                request.Options.FontReferenceImageDetail)
        };
        if (!string.IsNullOrWhiteSpace(retryFeedback))
        {
            userContent.Add(new TextContent(
                "RETRY_VALIDATION_FEEDBACK: " + retryFeedback));
        }

        var promptCacheKey = ResolvePromptCacheKey(
            request,
            systemPrompt,
            bookLevelContext,
            outputSchema,
            fontReference);

        return new ModelRequest(
            request.Options.Model,
            [
                new ModelMessage(
                    ModelRole.System,
                    systemPrompt),
                new ModelMessage(ModelRole.User, userContent)
            ],
            new JsonSchemaOutputFormat(
                "manga_dialogue_translation_layout",
                outputSchema),
            new ModelGenerationOptions
            {
                MaxOutputTokens = request.Options.MaxOutputTokens,
                ReasoningEffort = request.Options.ReasoningEffort,
                PromptCacheKey = promptCacheKey,
                PromptCacheRetention = request.Options.EnablePromptCaching
                    ? request.Options.PromptCacheRetention
                    : null,
                TotalTimeout = request.Options.TotalTimeout
            },
            new Dictionary<string, string>
            {
                ["run_id"] = request.RunId,
                ["book_id"] = request.BookId,
                ["page_id"] = request.Page.PageId,
                ["stage"] = "dialogue_translation_layout",
                ["prompt_profile"] = _prompts.ProfileId,
                ["font_reference_id"] = fontReference.ReferenceId
            });
    }

    private static string? ResolvePromptCacheKey(
        DialogueTranslationRequest request,
        string systemPrompt,
        string bookLevelContext,
        JsonElement outputSchema,
        FontReferenceSnapshot fontReference)
    {
        if (!request.Options.EnablePromptCaching)
        {
            return null;
        }

        if (request.Options.PromptCacheKey is not null)
        {
            return request.Options.PromptCacheKey;
        }

        var material = string.Join(
            '\n',
            "model=" + request.Options.Model,
            "font_reference_detail=" + request.Options.FontReferenceImageDetail,
            "font_image_sha256=" + Convert.ToHexString(
                SHA256.HashData(fontReference.Image.Data.Span)),
            "system_prompt=" + systemPrompt,
            "book_level_context=" + bookLevelContext,
            "output_schema=" + outputSchema.GetRawText());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "dlg_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static DialogueTranslationResult ValidateAndMap(
        DialogueTranslationRequest request,
        FontReferenceSnapshot fontReference,
        ModelEnvelope envelope,
        ModelResponse response,
        bool autoFitSingleCavity)
    {
        var page = request.Page;
        if (envelope.Segments is null
            || envelope.IgnoredRegions is null
            || envelope.Warnings is null
            || envelope.Segments.Any(segment => segment is null)
            || envelope.IgnoredRegions.Any(ignored => ignored is null)
            || envelope.Warnings.Any(warning => warning is null))
        {
            throw new DialogueModelOutputException(
                "Dialogue result collections cannot be null.");
        }

        var knownRegionIds = page.DetectedTextRegions
            .Select(region => region.RegionId)
            .ToHashSet(StringComparer.Ordinal);
        var fontIds = fontReference.Fonts
            .ToDictionary(font => font.FontId, StringComparer.OrdinalIgnoreCase);
        var speakerValidity = ExtractCharacterValidity(request.BookKnowledge.Characters);
        var segmentRegionIds = new HashSet<string>(StringComparer.Ordinal);
        var ignoredRegionIds = new HashSet<string>(StringComparer.Ordinal);
        var modelSegmentIds = new HashSet<string>(StringComparer.Ordinal);
        var oldToNewSegmentIds = new Dictionary<string, string>(StringComparer.Ordinal);

        var duplicateReadingOrders = envelope.Segments
            .GroupBy(segment => segment.ReadingOrder)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateReadingOrders.Length > 0)
        {
            throw new DialogueModelOutputException(
                "Segment reading_order values must be unique: "
                + string.Join(", ", duplicateReadingOrders));
        }

        var orderedSegments = envelope.Segments
            .OrderBy(segment => segment.ReadingOrder)
            .ThenBy(segment => segment.SegmentId, StringComparer.Ordinal)
            .ToArray();
        var mappedSegments = new DialogueSegment[orderedSegments.Length];
        for (var index = 0; index < orderedSegments.Length; index++)
        {
            var segment = orderedSegments[index];
            ValidateSegmentText(segment);
            if (string.IsNullOrWhiteSpace(segment.SegmentId)
                || !modelSegmentIds.Add(segment.SegmentId))
            {
                throw new DialogueModelOutputException(
                    "Model segment_id values must be non-empty and unique.");
            }

            ValidateSegmentRegionIds(
                segment.SourceRegionIds,
                knownRegionIds,
                segmentRegionIds,
                $"segment '{segment.SegmentId}'");
            ValidateSpeakerId(
                segment.SpeakerId,
                speakerValidity,
                page.Order,
                segment.SegmentId);
            var selectedFont = ResolveFont(segment.Layout.FontId, fontIds);
            var normalizedText = NormalizeNewlines(segment.TranslatedText);
            var normalizedSource = NormalizeNewlines(segment.SourceText);
            var normalizedAdvanced = ValidateAdvanced(
                segment.Layout.Advanced,
                normalizedText,
                page,
                segment.SourceRegionIds,
                segment.Layout.TextBox,
                segment.SegmentId);
            var needsSpatialCavityReview = HasUngroupedSpatialCavities(
                page,
                segment.SourceRegionIds,
                normalizedAdvanced?.PlacementGroups ?? []);
            var normalizedRotation = DialogueBoxRotationCoordinates
                .NormalizeLegacyPageRelativeCenter(
                    segment.Layout.Rotation,
                    segment.Layout.TextBox);
            var normalizedLayout = segment.Layout with { Rotation = normalizedRotation };
            ValidateLayout(normalizedLayout, selectedFont, page);
            var newSegmentId = $"{page.PageId}:segment:{index + 1:0000}";
            oldToNewSegmentIds[segment.SegmentId] = newSegmentId;

            mappedSegments[index] = segment with
            {
                SegmentId = newSegmentId,
                ReadingOrder = index,
                SourceText = normalizedSource,
                TranslatedText = normalizedText,
                SpeakerId = string.IsNullOrWhiteSpace(segment.SpeakerId)
                    ? null
                    : segment.SpeakerId,
                NeedsPriorityReview = segment.NeedsPriorityReview || needsSpatialCavityReview,
                Layout = normalizedLayout with
                {
                    FontId = selectedFont.FontId,
                    Advanced = normalizedAdvanced
                }
            };
        }

        foreach (var ignored in envelope.IgnoredRegions)
        {
            if (ignored.RegionIds is null
                || ignored.RegionIds.Count == 0
                || string.IsNullOrWhiteSpace(ignored.Reason)
                || string.IsNullOrWhiteSpace(ignored.Evidence))
            {
                throw new DialogueModelOutputException(
                    "Ignored-region groups require IDs, reason, and evidence.");
            }

            ValidateIgnoredRegionIds(
                ignored.RegionIds,
                knownRegionIds,
                segmentRegionIds,
                ignoredRegionIds,
                "ignored_regions");
        }

        var missing = knownRegionIds
            .Where(regionId => !segmentRegionIds.Contains(regionId)
                && !ignoredRegionIds.Contains(regionId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new DialogueModelOutputException(
                "The model did not cover LocalDetection regions: "
                + string.Join(", ", missing));
        }

        if (autoFitSingleCavity)
        {
            for (var index = 0; index < mappedSegments.Length; index++)
            {
                mappedSegments[index] = AutoFitQuickSingleCavity(mappedSegments[index], page);
            }
        }

        ValidateSpatialGrounding(page, mappedSegments);

        var warnings = envelope.Warnings.Select(warning =>
        {
            string? mappedSegmentId = null;
            if (warning.SegmentId is not null
                && !oldToNewSegmentIds.TryGetValue(
                    warning.SegmentId,
                    out mappedSegmentId))
            {
                throw new DialogueModelOutputException(
                    $"Warning references unknown segment_id '{warning.SegmentId}'.");
            }

            return warning with { SegmentId = mappedSegmentId };
        }).ToArray();
        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning.Code)
                || string.IsNullOrWhiteSpace(warning.Message))
            {
                throw new DialogueModelOutputException(
                    "Warnings require non-empty code and message values.");
            }

            if (warning.RegionId is not null && !knownRegionIds.Contains(warning.RegionId))
            {
                throw new DialogueModelOutputException(
                    $"Warning references unknown region_id '{warning.RegionId}'.");
            }
        }

        return new DialogueTranslationResult(
            DialoguePromptCatalog.SchemaContractVersion,
            request.RunId,
            request.BookId,
            request.Options.TargetLanguage,
            page.PageId,
            response.ProviderId,
            response.Model,
            mappedSegments,
            envelope.IgnoredRegions,
            warnings,
            response.Usage,
            response.Metadata);
    }

    private static void ValidateRequest(DialogueTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Page);
        ArgumentNullException.ThrowIfNull(request.BookKnowledge);
        ArgumentNullException.ThrowIfNull(request.PageImages);
        ArgumentNullException.ThrowIfNull(request.FontReferenceImages);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.BookPageIndex);
        ArgumentNullException.ThrowIfNull(request.Page.DetectedTextRegions);

        var page = request.Page;
        if (string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.BookId)
            || string.IsNullOrWhiteSpace(request.Options.Model)
            || string.IsNullOrWhiteSpace(request.Options.TargetLanguage)
            || string.IsNullOrWhiteSpace(request.BookKnowledge.TargetLanguage)
            || !IsDefinedJson(request.BookKnowledge.Chapters)
            || !IsDefinedJson(request.BookKnowledge.Characters)
            || !IsDefinedJson(request.BookKnowledge.CulturalLocalizations)
            || !IsDefinedJson(request.BookKnowledge.Glossary)
            || !IsDefinedJson(request.BookKnowledge.StoryBible)
            || string.IsNullOrWhiteSpace(page.PageId)
            || string.IsNullOrWhiteSpace(page.SceneId)
            || string.IsNullOrWhiteSpace(page.PageType)
            || !IsImageMediaType(page.MediaType))
        {
            throw new ArgumentException(
                "Run, book, model, language, and page identity fields are required.",
                nameof(request));
        }

        if (!page.IncludeInMask)
        {
            throw new ArgumentException(
                $"Page {page.PageId} is excluded by include_in_mask and must not be submitted.",
                nameof(request));
        }

        if (page.Order <= 0 || page.Width <= 0 || page.Height <= 0)
        {
            throw new ArgumentException(
                $"Page {page.PageId} has invalid order or dimensions.",
                nameof(request));
        }

        ValidateBookPageIndex(request.BookPageIndex, page);

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                request.BookKnowledge.TargetLanguage,
                request.Options.TargetLanguage))
        {
            throw new ArgumentException(
                "Book knowledge and dialogue target languages must match.",
                nameof(request));
        }

        if (page.DetectedTextRegions.Any(item => item is null || item.Region is null))
        {
            throw new ArgumentException(
                "Detected region entries and their region payloads cannot be null.",
                nameof(request));
        }

        var duplicateRegionIds = page.DetectedTextRegions
            .GroupBy(region => region.RegionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateRegionIds.Length > 0)
        {
            throw new ArgumentException(
                "Detected region IDs must be unique: "
                + string.Join(", ", duplicateRegionIds),
                nameof(request));
        }

        var duplicateIndices = page.DetectedTextRegions
            .GroupBy(region => region.DetectionIndex)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIndices.Length > 0)
        {
            throw new ArgumentException(
                "Detection indices must be unique: "
                + string.Join(", ", duplicateIndices),
                nameof(request));
        }

        foreach (var item in page.DetectedTextRegions)
        {
            if (item is null
                || item.Region is null
                || string.IsNullOrWhiteSpace(item.RegionId)
                || item.DetectionIndex < 0
                || item.Region.X0 < 0
                || item.Region.Y0 < 0
                || item.Region.X1 <= item.Region.X0
                || item.Region.Y1 <= item.Region.Y0
                || item.Region.X1 > page.Width
                || item.Region.Y1 > page.Height
                || item.Region.FontSize < 0
                || !float.IsFinite(item.Region.Confidence)
                || item.Region.Confidence < 0
                || item.Region.Confidence > 1
                || item.Region.Lines is null
                || item.Region.Lines.Any(line => line is null
                    || line.Polygon is null
                    || line.Polygon.Count < 3
                    || !float.IsFinite(line.Confidence)
                    || line.Confidence < 0
                    || line.Confidence > 1))
            {
                throw new ArgumentException(
                    $"Detected region '{item?.RegionId ?? "<null>"}' is invalid for page {page.PageId}.",
                    nameof(request));
            }
        }

        if (request.Options.MaxOutputTokens <= 0
            || request.Options.MaxModelAttempts <= 0
            || request.Options.RetryBaseDelay < TimeSpan.Zero
            || request.Options.TotalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Dialogue generation limits must be positive.",
                nameof(request));
        }

        if (request.Options.EnablePromptCaching
            && request.Options.PromptCacheKey is not null
            && string.IsNullOrWhiteSpace(request.Options.PromptCacheKey))
        {
            throw new ArgumentException(
                "Dialogue prompt cache key cannot be empty or whitespace.",
                nameof(request));
        }
    }

    private static void ValidateBookPageIndex(
        IReadOnlyList<DialogueBookPageIndexEntry> bookPageIndex,
        DialoguePageInput currentPage)
    {
        if (bookPageIndex.Count == 0)
        {
            return;
        }

        if (bookPageIndex.Any(item => item is null
                || string.IsNullOrWhiteSpace(item.PageId)
                || item.Order <= 0))
        {
            throw new ArgumentException(
                "BookPageIndex entries require a non-empty page_id and positive order.",
                nameof(bookPageIndex));
        }

        var duplicatePageIds = bookPageIndex
            .GroupBy(item => item.PageId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var duplicateOrders = bookPageIndex
            .GroupBy(item => item.Order)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePageIds.Length > 0 || duplicateOrders.Length > 0)
        {
            throw new ArgumentException(
                "BookPageIndex page_id and order values must each be unique.",
                nameof(bookPageIndex));
        }

        var currentEntry = bookPageIndex.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.PageId, currentPage.PageId));
        if (currentEntry is null || currentEntry.Order != currentPage.Order)
        {
            throw new ArgumentException(
                $"BookPageIndex must map current page {currentPage.PageId} to "
                + $"order {currentPage.Order}.",
                nameof(bookPageIndex));
        }
    }

    private void ValidateCapabilities(DialogueTranslationOptions options)
    {
        var capabilities = _modelProvider.Capabilities;
        if (!capabilities.SupportsVision
            || !capabilities.SupportsMultipleImages
            || !capabilities.SupportsStructuredOutputs
            || !capabilities.SupportsStreaming)
        {
            throw new DialogueTranslationException(
                $"Model provider '{_modelProvider.ProviderId}' must support vision, "
                + "multiple images, structured outputs, and streaming.");
        }

        if ((options.PageImageDetail == ImageDetailLevel.Original
                || options.FontReferenceImageDetail == ImageDetailLevel.Original)
            && !capabilities.SupportsOriginalImageDetail)
        {
            throw new DialogueTranslationException(
                $"Model provider '{_modelProvider.ProviderId}' does not support "
                + "original image detail.");
        }

        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort)
            && !capabilities.SupportsReasoningConfiguration)
        {
            throw new DialogueTranslationException(
                $"Model provider '{_modelProvider.ProviderId}' does not support "
                + "reasoning configuration.");
        }
    }

    private static void ValidateAssets(
        DialoguePageInput page,
        DialogueImageAsset pageImage,
        FontReferenceSnapshot fontReference)
    {
        ArgumentNullException.ThrowIfNull(pageImage);
        ArgumentNullException.ThrowIfNull(fontReference);
        ArgumentNullException.ThrowIfNull(fontReference.Image);
        ArgumentNullException.ThrowIfNull(fontReference.Fonts);

        if (pageImage.Data.IsEmpty
            || string.IsNullOrWhiteSpace(pageImage.AssetId)
            || !IsImageMediaType(pageImage.MediaType)
            || !StringComparer.OrdinalIgnoreCase.Equals(pageImage.MediaType, page.MediaType)
            || pageImage.Width != page.Width
            || pageImage.Height != page.Height)
        {
            throw new DialogueTranslationException(
                $"Original image for {page.PageId} does not match page dimensions.");
        }

        if (fontReference.Image.Data.IsEmpty
            || string.IsNullOrWhiteSpace(fontReference.ReferenceId)
            || string.IsNullOrWhiteSpace(fontReference.Image.AssetId)
            || !IsImageMediaType(fontReference.Image.MediaType)
            || fontReference.Image.Width <= 0
            || fontReference.Image.Height <= 0
            || fontReference.LibraryRevision <= 0
            || fontReference.Fonts.Count == 0)
        {
            throw new DialogueTranslationException("The font reference snapshot is invalid.");
        }

        var hasInvalidFontMetadata = fontReference.Fonts.Any(font => font is null
            || string.IsNullOrWhiteSpace(font.FontId)
            || string.IsNullOrWhiteSpace(font.DisplayName)
            || font.Tags is null
            || font.Styles is null
            || font.Styles.Count == 0
            || font.Styles.Any(style => style is null
                || string.IsNullOrWhiteSpace(style.Style)
                || string.IsNullOrWhiteSpace(style.RenderMode)
                || string.IsNullOrWhiteSpace(style.SourceStyle)
                || style.MissingGlyphCount < 0
                || style.MissingGlyphs is null));
        if (hasInvalidFontMetadata)
        {
            throw new DialogueTranslationException(
                "Font reference metadata contains an invalid font or style.");
        }

        var duplicateFontIds = fontReference.Fonts
            .GroupBy(font => font.FontId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateFontIds.Length > 0)
        {
            throw new DialogueTranslationException(
                "Font reference IDs must be non-empty and unique: "
                + string.Join(", ", duplicateFontIds));
        }
    }

    private static void ValidateSegmentText(DialogueSegment segment)
    {
        if (segment.SourceRegionIds is null
            || segment.Layout is null
            || segment.Layout.TextBox is null
            || segment.Notes is null
            || segment.SourceText is null
            || segment.TranslatedText is null
            || !double.IsFinite(segment.Confidence)
            || segment.Confidence < 0
            || segment.Confidence > 1
            || segment.Notes.Any(note => note is null))
        {
            throw new DialogueModelOutputException(
                $"Segment '{segment.SegmentId}' has missing or invalid fields.");
        }

        if (segment.SourceRegionIds.Count == 0)
        {
            throw new DialogueModelOutputException(
                $"[grouping] Segment '{segment.SegmentId}' must cite at least one "
                + "LocalDetection region_id. OCR outside supplied regions is not renderable.");
        }

        if (segment.Status == DialogueSegmentStatus.Translated
            && (string.IsNullOrWhiteSpace(segment.SourceText)
                || string.IsNullOrWhiteSpace(segment.TranslatedText)))
        {
            throw new DialogueModelOutputException(
                $"Translated segment '{segment.SegmentId}' has empty text.");
        }

        if (segment.Status == DialogueSegmentStatus.Unreadable
            && (!string.IsNullOrEmpty(segment.SourceText)
                || !string.IsNullOrEmpty(segment.TranslatedText)))
        {
            throw new DialogueModelOutputException(
                $"Unreadable segment '{segment.SegmentId}' must have empty text.");
        }
    }

    private static Dictionary<string, IReadOnlyList<CharacterValidityRange>>
        ExtractCharacterValidity(JsonElement characters)
    {
        var rangesById = new Dictionary<string, List<CharacterValidityRange>>(
            StringComparer.Ordinal);
        if (characters.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, IReadOnlyList<CharacterValidityRange>>(
                StringComparer.Ordinal);
        }

        foreach (var character in characters.EnumerateArray())
        {
            if (character.ValueKind != JsonValueKind.Object
                || !character.TryGetProperty("character_id", out var characterId)
                || characterId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(characterId.GetString()))
            {
                continue;
            }

            var isWellFormed = true;
            var validFromOrder = 1;
            if (character.TryGetProperty("valid_from_order", out var validFrom))
            {
                isWellFormed = validFrom.ValueKind == JsonValueKind.Number
                    && validFrom.TryGetInt32(out validFromOrder);
            }

            int? validToOrder = null;
            if (character.TryGetProperty("valid_to_order", out var validTo)
                && validTo.ValueKind != JsonValueKind.Null)
            {
                if (validTo.ValueKind == JsonValueKind.Number
                    && validTo.TryGetInt32(out var parsedValidTo))
                {
                    validToOrder = parsedValidTo;
                }
                else
                {
                    isWellFormed = false;
                }
            }

            if (validFromOrder <= 0
                || validToOrder is not null && validToOrder < validFromOrder)
            {
                isWellFormed = false;
            }

            var id = characterId.GetString()!;
            if (!rangesById.TryGetValue(id, out var ranges))
            {
                ranges = [];
                rangesById.Add(id, ranges);
            }

            ranges.Add(new CharacterValidityRange(
                validFromOrder,
                validToOrder,
                isWellFormed));
        }

        return rangesById.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<CharacterValidityRange>)item.Value,
            StringComparer.Ordinal);
    }

    private static void ValidateSpeakerId(
        string? speakerId,
        IReadOnlyDictionary<string, IReadOnlyList<CharacterValidityRange>> speakerValidity,
        int pageOrder,
        string segmentId)
    {
        if (speakerId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(speakerId)
            || !speakerValidity.TryGetValue(speakerId, out var validityRanges))
        {
            throw new DialogueModelOutputException(
                $"Segment '{segmentId}' references unknown speaker_id '{speakerId}'.");
        }

        if (!validityRanges.Any(range => range.IsActiveAt(pageOrder)))
        {
            throw new DialogueModelOutputException(
                $"Segment '{segmentId}' references speaker_id '{speakerId}', which is not "
                + $"valid at page.order {pageOrder} according to valid_from_order/valid_to_order.");
        }
    }

    private sealed record CharacterValidityRange(
        int ValidFromOrder,
        int? ValidToOrder,
        bool IsWellFormed)
    {
        public bool IsActiveAt(int pageOrder) => IsWellFormed
            && pageOrder >= ValidFromOrder
            && (ValidToOrder is null || pageOrder <= ValidToOrder);
    }

    private static void ValidateLayout(
        DialogueTextLayout layout,
        FontReferenceFont selectedFont,
        DialoguePageInput page)
    {
        if (layout.TextBox.X0 < 0
            || layout.TextBox.Y0 < 0
            || layout.TextBox.X1 <= layout.TextBox.X0
            || layout.TextBox.Y1 <= layout.TextBox.Y0
            || layout.TextBox.X1 > page.Width
            || layout.TextBox.Y1 > page.Height)
        {
            throw new DialogueModelOutputException(
                $"Layout box is outside page {page.PageId}.");
        }

        if (string.IsNullOrWhiteSpace(selectedFont.FontId)
            || layout.FontSizePx <= 0
            || !float.IsFinite(layout.FontSizePx)
            || layout.EffectiveFontWeight is < 1 or > 1000
            || !float.IsFinite(layout.LetterSpacingPx)
            || Math.Abs(layout.LetterSpacingPx) > Math.Max(page.Width, page.Height)
            || string.IsNullOrWhiteSpace(layout.Color)
            || !IsColor(layout.Color))
        {
            throw new DialogueModelOutputException("Base text layout values are invalid.");
        }

        if (layout.Rotation is not null
            && (!float.IsFinite(layout.Rotation.CenterXPx)
                || !float.IsFinite(layout.Rotation.CenterYPx)
                || !float.IsFinite(layout.Rotation.AngleDeg)))
        {
            throw new DialogueModelOutputException(
                "Rotation values must be finite.");
        }

        if (layout.Stroke is not null
            && (layout.Stroke.WidthPx < 0
                || !float.IsFinite(layout.Stroke.WidthPx)
                || string.IsNullOrWhiteSpace(layout.Stroke.Color)
                || !IsColor(layout.Stroke.Color)))
        {
            throw new DialogueModelOutputException(
                "Stroke values are invalid.");
        }
    }

    private static void ValidateSpatialGrounding(
        DialoguePageInput page,
        IReadOnlyList<DialogueSegment> segments)
    {
        var regions = page.DetectedTextRegions.ToDictionary(
            item => item.RegionId,
            item => item.Region,
            StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            if (segment.SourceRegionIds.Count > 0)
            {
                var cited = segment.SourceRegionIds
                    .Select(regionId => regions[regionId])
                    .ToArray();
                var unanchored = cited
                    .Where(region => !Intersects(
                        segment.Layout.TextBox,
                        new TextLayoutBox(region.X0, region.Y0, region.X1, region.Y1)))
                    .ToArray();
                if (unanchored.Length > 0)
                {
                    throw new DialogueModelOutputException(
                        $"[anchor] Segment '{segment.SegmentId}' text_box does not overlap "
                        + "every cited source region. Keep the block in the same logical lettering unit; a "
                        + "connected multi-lobed unit must intersect every supported lobe and cite only its "
                        + "own regions.");
                }
            }

            ValidateApproximateFit(segment);
        }

        var translated = segments
            .Where(segment => segment.Status == DialogueSegmentStatus.Translated)
            .ToArray();
        for (var leftIndex = 0; leftIndex < translated.Length; leftIndex++)
        {
            var left = translated[leftIndex];
            var leftArea = Area(left.Layout.TextBox);
            for (var rightIndex = leftIndex + 1; rightIndex < translated.Length; rightIndex++)
            {
                var right = translated[rightIndex];
                var overlap = IntersectionArea(left.Layout.TextBox, right.Layout.TextBox);
                var smaller = Math.Min(leftArea, Area(right.Layout.TextBox));
                if (smaller <= 0 || overlap / smaller < 0.92)
                {
                    continue;
                }

                var explained = left.Notes.Concat(right.Notes).Any(note =>
                    note.Contains("overlap", StringComparison.OrdinalIgnoreCase)
                    || note.Contains("重叠", StringComparison.Ordinal)
                    || note.Contains("重疊", StringComparison.Ordinal));
                if (!explained)
                {
                    throw new DialogueModelOutputException(
                        $"[overlap] Segment '{left.SegmentId}' and '{right.SegmentId}' have "
                        + "near-identical/containing text boxes without an explicit visual-overlap explanation.");
                }
            }
        }
    }

    private static void ValidateApproximateFit(DialogueSegment segment)
    {
        if (segment.Status != DialogueSegmentStatus.Translated)
        {
            return;
        }

        var text = NormalizeNewlines(segment.TranslatedText);
        var lines = text.Split('\n');
        if (lines.Any(line => line.Length == 0))
        {
            throw new DialogueModelOutputException(
                $"[fit] Segment '{segment.SegmentId}' contains an empty rendered line or column.");
        }

        var baseSize = segment.Layout.FontSizePx;
        var lineAdjustments = (segment.Layout.Advanced?.LineAdjustments ?? [])
            .ToDictionary(item => item.LineIndex);
        var characterAdjustments = (segment.Layout.Advanced?.CharacterAdjustments ?? [])
            .ToDictionary(item => item.CharacterIndex);
        var globalCharacter = 0;
        var sizesByLine = new float[lines.Length];
        var countsByLine = new int[lines.Length];
        var strokeInsetsByLine = new float[lines.Length];
        var offsetXByLine = new float[lines.Length];
        var offsetYByLine = new float[lines.Length];
        foreach (var (line, lineIndex) in lines.Select((value, index) => (value, index)))
        {
            var elements = StringInfo.ParseCombiningCharacters(line).Length;
            var lineAdjustment = lineAdjustments.GetValueOrDefault(lineIndex);
            var lineSize = lineAdjustment?.FontSizePx ?? baseSize;
            var lineColor = lineAdjustment?.Color ?? segment.Layout.Color;
            var lineOffsetX = lineAdjustment?.OffsetXPx ?? 0;
            var lineOffsetY = lineAdjustment?.OffsetYPx ?? 0;
            var sizes = new List<float> { lineSize };
            var strokeInset = 0f;
            var maximumOffsetX = Math.Abs(lineOffsetX);
            var maximumOffsetY = Math.Abs(lineOffsetY);
            for (var index = 0; index < elements; index++)
            {
                var characterAdjustment = characterAdjustments.GetValueOrDefault(globalCharacter);
                var characterSize = characterAdjustment?.FontSizePx ?? lineSize;
                var characterColor = characterAdjustment?.Color ?? lineColor;
                sizes.Add(characterSize);
                strokeInset = Math.Max(
                    strokeInset,
                    ResolveStrokeInset(
                        characterSize,
                        characterColor,
                        segment.Layout.Stroke));
                maximumOffsetX = Math.Max(
                    maximumOffsetX,
                    Math.Abs(lineOffsetX + (characterAdjustment?.OffsetXPx ?? 0)));
                maximumOffsetY = Math.Max(
                    maximumOffsetY,
                    Math.Abs(lineOffsetY + (characterAdjustment?.OffsetYPx ?? 0)));
                globalCharacter++;
            }

            sizesByLine[lineIndex] = sizes.Max();
            countsByLine[lineIndex] = elements;
            strokeInsetsByLine[lineIndex] = strokeInset;
            offsetXByLine[lineIndex] = maximumOffsetX;
            offsetYByLine[lineIndex] = maximumOffsetY;
        }

        var placementGroups = segment.Layout.Advanced?.PlacementGroups ?? [];
        if (placementGroups.Count == 0)
        {
            ValidateApproximateFitInBox(
                segment,
                lines,
                Enumerable.Range(0, lines.Length).ToArray(),
                segment.Layout.TextBox,
                sizesByLine,
                countsByLine,
                strokeInsetsByLine,
                offsetXByLine,
                offsetYByLine,
                "text_box");
            return;
        }

        for (var groupIndex = 0; groupIndex < placementGroups.Count; groupIndex++)
        {
            var group = placementGroups[groupIndex];
            ValidateApproximateFitInBox(
                segment,
                lines,
                group.LineIndices.ToArray(),
                group.TextBox,
                sizesByLine,
                countsByLine,
                strokeInsetsByLine,
                offsetXByLine,
                offsetYByLine,
                $"placement_groups[{groupIndex}].text_box");
        }
    }

    private static DialogueSegment AutoFitQuickSingleCavity(
        DialogueSegment segment,
        DialoguePageInput page)
    {
        if (segment.Status != DialogueSegmentStatus.Translated
            || segment.Layout.Advanced?.PlacementGroups.Count > 0)
        {
            return segment;
        }

        var candidate = segment;
        const float minimumFontSize = 10f;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                ValidateApproximateFit(candidate);
                return candidate;
            }
            catch (DialogueModelOutputException exception) when (
                exception.Message.StartsWith("[fit]", StringComparison.Ordinal))
            {
                if (candidate.Layout.FontSizePx <= minimumFontSize) break;
                var nextSize = Math.Max(minimumFontSize, candidate.Layout.FontSizePx * 0.82f);
                var scale = nextSize / candidate.Layout.FontSizePx;
                candidate = candidate with
                {
                    NeedsPriorityReview = true,
                    Layout = ScaleQuickLayout(candidate.Layout, scale)
                };
            }
        }

        var originalBox = candidate.Layout.TextBox;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            candidate = candidate with
            {
                NeedsPriorityReview = true,
                Layout = candidate.Layout with
                {
                    TextBox = ExpandQuickBox(
                        candidate.Layout.TextBox,
                        originalBox,
                        page.Width,
                        page.Height)
                }
            };
            try
            {
                ValidateApproximateFit(candidate);
                return candidate;
            }
            catch (DialogueModelOutputException exception) when (
                exception.Message.StartsWith("[fit]", StringComparison.Ordinal))
            {
                // Continue within the bounded expansion budget, then run the strict validator once more.
            }
        }

        ValidateApproximateFit(candidate);
        return candidate;
    }

    private static TextLayoutBox ExpandQuickBox(
        TextLayoutBox current,
        TextLayoutBox original,
        int pageWidth,
        int pageHeight)
    {
        var currentWidth = current.X1 - current.X0;
        var currentHeight = current.Y1 - current.Y0;
        var maximumWidth = Math.Min(pageWidth, original.X1 - original.X0 + 48);
        var maximumHeight = Math.Min(pageHeight, original.Y1 - original.Y0 + 48);
        var targetWidth = Math.Min(
            maximumWidth,
            Math.Max(currentWidth + 4, (int)Math.Ceiling(currentWidth * 1.12)));
        var targetHeight = Math.Min(
            maximumHeight,
            Math.Max(currentHeight + 4, (int)Math.Ceiling(currentHeight * 1.08)));
        var centerX = (current.X0 + current.X1) / 2.0;
        var centerY = (current.Y0 + current.Y1) / 2.0;
        var x0 = Math.Clamp((int)Math.Floor(centerX - targetWidth / 2.0), 0, pageWidth - targetWidth);
        var y0 = Math.Clamp((int)Math.Floor(centerY - targetHeight / 2.0), 0, pageHeight - targetHeight);
        return new TextLayoutBox(x0, y0, x0 + targetWidth, y0 + targetHeight);
    }

    private static DialogueTextLayout ScaleQuickLayout(DialogueTextLayout layout, float scale)
    {
        var advanced = layout.Advanced;
        if (advanced is not null)
        {
            advanced = advanced with
            {
                LineAdjustments = advanced.LineAdjustments.Select(item => item with
                {
                    OffsetXPx = item.OffsetXPx * scale,
                    OffsetYPx = item.OffsetYPx * scale,
                    FontSizePx = item.FontSizePx is null
                        ? null
                        : Math.Max(1, item.FontSizePx.Value * scale)
                }).ToArray(),
                CharacterAdjustments = advanced.CharacterAdjustments.Select(item => item with
                {
                    OffsetXPx = item.OffsetXPx * scale,
                    OffsetYPx = item.OffsetYPx * scale,
                    FontSizePx = item.FontSizePx is null
                        ? null
                        : Math.Max(1, item.FontSizePx.Value * scale)
                }).ToArray(),
                RubyAnnotations = advanced.RubyAnnotations.Select(item => item with
                {
                    OffsetXPx = item.OffsetXPx * scale,
                    OffsetYPx = item.OffsetYPx * scale,
                    FontSizePx = item.FontSizePx is null
                        ? null
                        : Math.Max(1, item.FontSizePx.Value * scale)
                }).ToArray()
            };
        }

        return layout with
        {
            FontSizePx = Math.Max(1, layout.FontSizePx * scale),
            LetterSpacingPx = layout.LetterSpacingPx * scale,
            Advanced = advanced,
            Stroke = layout.Stroke is null
                ? null
                : layout.Stroke with { WidthPx = layout.Stroke.WidthPx * scale }
        };
    }

    private static void ValidateApproximateFitInBox(
        DialogueSegment segment,
        string[] lines,
        int[] lineIndices,
        TextLayoutBox textBox,
        float[] sizesByLine,
        int[] countsByLine,
        float[] strokeInsetsByLine,
        float[] offsetXByLine,
        float[] offsetYByLine,
        string boxLabel)
    {
        var selectedLineIndices = lineIndices;
        var strokeInset = selectedLineIndices
            .Select(index => strokeInsetsByLine[index])
            .DefaultIfEmpty(0)
            .Max();
        var boxWidth = textBox.X1 - textBox.X0 - strokeInset * 2;
        var boxHeight = textBox.Y1 - textBox.Y0 - strokeInset * 2;
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            throw new DialogueModelOutputException(
                $"[fit] Segment '{segment.SegmentId}' has no usable space inside {boxLabel} "
                + "after accounting for its stroke.");
        }

        var selectedSizes = selectedLineIndices
            .Select(index => sizesByLine[index])
            .ToArray();
        var selectedCounts = selectedLineIndices
            .Select(index => countsByLine[index])
            .ToArray();
        var maximumOffsetX = selectedLineIndices
            .Select(index => offsetXByLine[index])
            .DefaultIfEmpty(0)
            .Max();
        var maximumOffsetY = selectedLineIndices
            .Select(index => offsetYByLine[index])
            .DefaultIfEmpty(0)
            .Max();
        var baseSize = segment.Layout.FontSizePx;
        if (segment.Layout.Orientation == DialogueTextOrientation.Vertical)
        {
            var columnGap = Math.Max(1, baseSize * 0.2f);
            var requiredWidth = selectedSizes.Sum(size => Math.Max(1, size))
                + columnGap * Math.Max(0, selectedSizes.Length - 1)
                + maximumOffsetX * 2;
            var requiredHeight = selectedCounts.Zip(selectedSizes)
                .Select(item => item.First * Math.Max(item.Second, item.Second * 1.18f)
                    + Math.Max(0, item.First - 1) * segment.Layout.LetterSpacingPx)
                .DefaultIfEmpty(0)
                .Max()
                + maximumOffsetY * 2;
            if (requiredWidth > boxWidth * 1.08f || requiredHeight > boxHeight * 1.08f)
            {
                throw new DialogueModelOutputException(
                    $"[fit] Segment '{segment.SegmentId}' requires approximately "
                    + $"{requiredWidth:0.#}x{requiredHeight:0.#}px but {boxLabel} has "
                    + $"{boxWidth:0.#}x{boxHeight:0.#}px of usable space. Rebreak, resize, or adjust the box within the same logical "
                    + "lettering unit.");
            }
        }
        else
        {
            var requiredHeight = selectedSizes.Sum(size => size * 1.18f)
                + maximumOffsetY * 2;
            var requiredWidth = selectedLineIndices
                .Select(index => EstimateHorizontalWidth(
                    lines[index],
                    sizesByLine[index],
                    segment.Layout.LetterSpacingPx))
                .DefaultIfEmpty(0)
                .Max()
                + maximumOffsetX * 2;
            if (requiredWidth > boxWidth * 1.30f || requiredHeight > boxHeight * 1.08f)
            {
                throw new DialogueModelOutputException(
                    $"[fit] Segment '{segment.SegmentId}' is unlikely to fit {boxLabel} "
                    + $"({boxWidth:0.#}x{boxHeight:0.#}px usable; estimated "
                    + $"{requiredWidth:0.#}x{requiredHeight:0.#}px).");
            }
        }
    }

    private static float EstimateHorizontalWidth(
        string line,
        float fontSize,
        float letterSpacing)
    {
        var width = 0f;
        var starts = StringInfo.ParseCombiningCharacters(line);
        for (var elementIndex = 0; elementIndex < starts.Length; elementIndex++)
        {
            var start = starts[elementIndex];
            var end = elementIndex + 1 < starts.Length
                ? starts[elementIndex + 1]
                : line.Length;
            var value = line[start..end];
            width += value.Any(IsWideIdeograph) ? fontSize : fontSize * 0.68f;
        }

        return width + Math.Max(0, starts.Length - 1) * letterSpacing;
    }

    private static bool IsWideIdeograph(char value) =>
        value >= '\u2E80' || value is >= '\u3000' and <= '\u30FF';

    private static float ResolveStrokeInset(
        float fontSize,
        string fillColor,
        DialogueTextStroke? stroke)
    {
        var outlineInset = IsNearWhite(fillColor) && !HasEffectiveContrastStroke(stroke, fillColor)
            ? Math.Clamp(fontSize * 0.04f, 1, 2)
            : stroke?.WidthPx ?? 0;
        return outlineInset > 0
            ? outlineInset + RendererClipSafetyInsetPx
            : 0;
    }

    private static bool IsNearWhite(string color)
    {
        var channels = ParseColorChannels(color);
        return channels.Alpha > 0
            && channels.Red >= 224
            && channels.Green >= 224
            && channels.Blue >= 224;
    }

    private static bool HasEffectiveContrastStroke(
        DialogueTextStroke? stroke,
        string fillColor)
    {
        if (stroke is null || stroke.WidthPx < 0.5f)
        {
            return false;
        }

        var fill = ParseColorChannels(fillColor);
        var outline = ParseColorChannels(stroke.Color);
        return outline.Alpha > 0
            && ContrastRatio(
                CompositeOverWhite(fill),
                CompositeOverWhite(outline)) >= 2;
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) CompositeOverWhite(
        (byte Red, byte Green, byte Blue, byte Alpha) color)
    {
        var alpha = color.Alpha / 255d;
        return (
            (byte)Math.Round(color.Red * alpha + byte.MaxValue * (1 - alpha)),
            (byte)Math.Round(color.Green * alpha + byte.MaxValue * (1 - alpha)),
            (byte)Math.Round(color.Blue * alpha + byte.MaxValue * (1 - alpha)),
            byte.MaxValue);
    }

    private static double ContrastRatio(
        (byte Red, byte Green, byte Blue, byte Alpha) left,
        (byte Red, byte Green, byte Blue, byte Alpha) right)
    {
        var leftLuminance = RelativeLuminance(left);
        var rightLuminance = RelativeLuminance(right);
        return (Math.Max(leftLuminance, rightLuminance) + 0.05)
            / (Math.Min(leftLuminance, rightLuminance) + 0.05);
    }

    private static double RelativeLuminance(
        (byte Red, byte Green, byte Blue, byte Alpha) color) =>
        0.2126 * LinearizeColorChannel(color.Red)
        + 0.7152 * LinearizeColorChannel(color.Green)
        + 0.0722 * LinearizeColorChannel(color.Blue);

    private static double LinearizeColorChannel(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) ParseColorChannels(
        string color) =>
        (
            ParseHexByte(color, 1),
            ParseHexByte(color, 3),
            ParseHexByte(color, 5),
            color.Length == 9 ? ParseHexByte(color, 7) : byte.MaxValue);

    private static byte ParseHexByte(string value, int start) =>
        byte.Parse(
            value.AsSpan(start, 2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

    private static bool Intersects(TextLayoutBox left, TextLayoutBox right) =>
        left.X0 < right.X1 && right.X0 < left.X1
        && left.Y0 < right.Y1 && right.Y0 < left.Y1;

    private static float Area(TextLayoutBox box) =>
        Math.Max(0, box.X1 - box.X0) * Math.Max(0, box.Y1 - box.Y0);

    private static float IntersectionArea(TextLayoutBox left, TextLayoutBox right)
    {
        var width = Math.Max(0, Math.Min(left.X1, right.X1) - Math.Max(left.X0, right.X0));
        var height = Math.Max(0, Math.Min(left.Y1, right.Y1) - Math.Max(left.Y0, right.Y0));
        return width * height;
    }

    private static DialogueAdvancedLayout? ValidateAdvanced(
        DialogueAdvancedLayout? advanced,
        string translatedText,
        DialoguePageInput page,
        IReadOnlyList<string> sourceRegionIds,
        TextLayoutBox segmentTextBox,
        string segmentId)
    {
        var spatialClusterByRegion = BuildSpatialRegionClusters(page, sourceRegionIds);
        if (advanced is null)
        {
            return null;
        }

        if (advanced.LineAdjustments is null
            || advanced.CharacterAdjustments is null
            || advanced.RubyAnnotations is null
            || advanced.PlacementGroups is null)
        {
            throw new DialogueModelOutputException(
                "Advanced layout collections cannot be null.");
        }

        if (advanced.LineAdjustments.Count == 0
            && advanced.CharacterAdjustments.Count == 0
            && advanced.RubyAnnotations.Count == 0
            && advanced.PlacementGroups.Count == 0)
        {
            return null;
        }

        var lineCount = string.IsNullOrEmpty(translatedText)
            ? 0
            : translatedText.Split('\n').Length;
        var characterText = translatedText.Replace("\n", string.Empty, StringComparison.Ordinal);
        var characterCount = StringInfo.ParseCombiningCharacters(characterText).Length;
        var maximumOffset = Math.Max(page.Width, page.Height);
        var adjustedLines = new HashSet<int>();
        var adjustedCharacters = new HashSet<int>();
        var rubyRanges = new HashSet<(int Start, int Length)>();

        foreach (var line in advanced.LineAdjustments)
        {
            if (line.LineIndex < 0
                || line.LineIndex >= lineCount
                || !adjustedLines.Add(line.LineIndex)
                || !ValidOverride(line.FontSizePx, line.Color)
                || !float.IsFinite(line.OffsetXPx)
                || !float.IsFinite(line.OffsetYPx)
                || Math.Abs(line.OffsetXPx) > maximumOffset
                || Math.Abs(line.OffsetYPx) > maximumOffset)
            {
                throw new DialogueModelOutputException("A line adjustment is out of range.");
            }
        }

        foreach (var character in advanced.CharacterAdjustments)
        {
            if (character.CharacterIndex < 0
                || character.CharacterIndex >= characterCount
                || !adjustedCharacters.Add(character.CharacterIndex)
                || !ValidOverride(character.FontSizePx, character.Color)
                || !float.IsFinite(character.OffsetXPx)
                || !float.IsFinite(character.OffsetYPx)
                || Math.Abs(character.OffsetXPx) > maximumOffset
                || Math.Abs(character.OffsetYPx) > maximumOffset)
            {
                throw new DialogueModelOutputException(
                    "A character adjustment is out of range.");
            }
        }

        foreach (var ruby in advanced.RubyAnnotations)
        {
            if (ruby.BaseCharacterIndex < 0
                || ruby.BaseCharacterLength <= 0
                || ruby.BaseCharacterIndex + ruby.BaseCharacterLength > characterCount
                || !rubyRanges.Add((ruby.BaseCharacterIndex, ruby.BaseCharacterLength))
                || string.IsNullOrWhiteSpace(ruby.Text)
                || ruby.Text.Contains('\n', StringComparison.Ordinal)
                || !ValidOverride(ruby.FontSizePx, ruby.Color)
                || !float.IsFinite(ruby.OffsetXPx)
                || !float.IsFinite(ruby.OffsetYPx)
                || Math.Abs(ruby.OffsetXPx) > maximumOffset
                || Math.Abs(ruby.OffsetYPx) > maximumOffset)
            {
                throw new DialogueModelOutputException("A ruby annotation is out of range.");
            }
        }

        ValidatePlacementGroups(
            advanced,
            translatedText,
            page,
            sourceRegionIds,
            segmentTextBox,
            segmentId,
            spatialClusterByRegion);

        return advanced;
    }

    private static void ValidatePlacementGroups(
        DialogueAdvancedLayout advanced,
        string translatedText,
        DialoguePageInput page,
        IReadOnlyList<string> sourceRegionIds,
        TextLayoutBox segmentTextBox,
        string segmentId,
        Dictionary<string, int> spatialClusterByRegion)
    {
        if (advanced.PlacementGroups.Count == 0)
        {
            return;
        }

        if (advanced.PlacementGroups.Count < 2)
        {
            throw new DialogueModelOutputException(
                $"[grouping] Segment '{segmentId}' must use either placement_groups=[] for one "
                + "ordinary render cavity or at least two placement groups for a multi-cavity unit.");
        }

        var lines = translatedText.Split('\n');
        var segmentRegionIds = sourceRegionIds.ToHashSet(StringComparer.Ordinal);
        var regions = page.DetectedTextRegions.ToDictionary(
            item => item.RegionId,
            item => new TextLayoutBox(
                item.Region.X0,
                item.Region.Y0,
                item.Region.X1,
                item.Region.Y1),
            StringComparer.Ordinal);
        var coveredLines = new HashSet<int>();
        var coveredRegions = new HashSet<string>(StringComparer.Ordinal);
        var lineOwners = Enumerable.Repeat(-1, lines.Length).ToArray();
        int? previousGroupMinimumLine = null;

        for (var groupIndex = 0; groupIndex < advanced.PlacementGroups.Count; groupIndex++)
        {
            var group = advanced.PlacementGroups[groupIndex];
            if (group is null
                || group.LineIndices is null
                || group.SourceRegionIds is null
                || group.TextBox is null)
            {
                throw new DialogueModelOutputException(
                    $"[grouping] Segment '{segmentId}' placement group {groupIndex} has missing fields.");
            }

            if (group.LineIndices.Count == 0 || group.SourceRegionIds.Count == 0)
            {
                throw new DialogueModelOutputException(
                    $"[grouping] Segment '{segmentId}' placement group {groupIndex} must contain "
                    + "at least one line_index and one source_region_id.");
            }

            if (previousGroupMinimumLine is not null
                && group.LineIndices[0] <= previousGroupMinimumLine.Value)
            {
                throw new DialogueModelOutputException(
                    $"[grouping] Segment '{segmentId}' placement groups must be ordered by "
                    + "increasing minimum line_index.");
            }

            previousGroupMinimumLine = group.LineIndices[0];

            var groupLines = new HashSet<int>();
            var previousLineIndex = -1;
            for (var localLineIndex = 0; localLineIndex < group.LineIndices.Count; localLineIndex++)
            {
                var lineIndex = group.LineIndices[localLineIndex];
                if (lineIndex < 0 || lineIndex >= lines.Length)
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' placement group {groupIndex} references "
                        + $"out-of-range translated line {lineIndex}.");
                }

                if (!groupLines.Add(lineIndex))
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' repeats translated line {lineIndex} "
                        + $"inside placement group {groupIndex}.");
                }

                if (localLineIndex > 0 && lineIndex != previousLineIndex + 1)
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' placement group {groupIndex} line_indices "
                        + "must be ascending and contiguous.");
                }

                if (!coveredLines.Add(lineIndex))
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' assigns translated line {lineIndex} "
                        + "to more than one placement group.");
                }

                lineOwners[lineIndex] = groupIndex;
                previousLineIndex = lineIndex;
            }

            var groupRegions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var regionId in group.SourceRegionIds)
            {
                if (string.IsNullOrWhiteSpace(regionId)
                    || !groupRegions.Add(regionId))
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' placement group {groupIndex} contains "
                        + "an empty or duplicate source_region_id.");
                }

                if (!segmentRegionIds.Contains(regionId))
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' placement group {groupIndex} references "
                        + $"region_id '{regionId}' that is not owned by the segment.");
                }

                if (!coveredRegions.Add(regionId))
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' assigns region_id '{regionId}' "
                    + "to more than one placement group.");
                }
            }

            var groupSpatialClusters = group.SourceRegionIds
                .Select(regionId => spatialClusterByRegion[regionId])
                .Distinct()
                .ToArray();
            if (groupSpatialClusters.Length > 1)
            {
                throw new DialogueModelOutputException(
                    $"[grouping] Segment '{segmentId}' placement group {groupIndex} spans "
                    + "geometrically separate LocalDetection clusters. Use one tight group per "
                    + "physical cavity, or split different semantic units into separate segments.");
            }

            if (!IsValidPageBox(group.TextBox, page)
                || !Contains(segmentTextBox, group.TextBox))
            {
                throw new DialogueModelOutputException(
                    $"[anchor] Segment '{segmentId}' placement group {groupIndex} text_box must be "
                    + "inside both the page and the segment union text_box.");
            }

            foreach (var regionId in group.SourceRegionIds)
            {
                if (!Intersects(group.TextBox, regions[regionId]))
                {
                    throw new DialogueModelOutputException(
                        $"[anchor] Segment '{segmentId}' placement group {groupIndex} text_box does "
                        + $"not intersect its referenced region_id '{regionId}'.");
                }
            }
        }

        var missingLines = Enumerable.Range(0, lines.Length)
            .Where(lineIndex => !coveredLines.Contains(lineIndex))
            .ToArray();
        if (missingLines.Length > 0)
        {
            throw new DialogueModelOutputException(
                $"[grouping] Segment '{segmentId}' placement groups do not cover translated line(s): "
                + string.Join(", ", missingLines));
        }

        var missingRegions = segmentRegionIds
            .Where(regionId => !coveredRegions.Contains(regionId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingRegions.Length > 0)
        {
            throw new DialogueModelOutputException(
                $"[grouping] Segment '{segmentId}' placement groups do not assign source region(s): "
                + string.Join(", ", missingRegions));
        }

        for (var leftIndex = 0; leftIndex < advanced.PlacementGroups.Count; leftIndex++)
        {
            var left = advanced.PlacementGroups[leftIndex].TextBox;
            var leftArea = Area(left);
            for (var rightIndex = leftIndex + 1;
                 rightIndex < advanced.PlacementGroups.Count;
                 rightIndex++)
            {
                var right = advanced.PlacementGroups[rightIndex].TextBox;
                var smallerArea = Math.Min(leftArea, Area(right));
                var overlap = IntersectionArea(left, right);
                if (smallerArea > 0 && overlap / smallerArea > 0.20f)
                {
                    throw new DialogueModelOutputException(
                        $"[overlap] Segment '{segmentId}' placement groups {leftIndex} and "
                        + $"{rightIndex} have materially overlapping text boxes.");
                }
            }
        }

        var characterOwners = new List<int>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var characterCount = StringInfo.ParseCombiningCharacters(lines[lineIndex]).Length;
            for (var characterIndex = 0; characterIndex < characterCount; characterIndex++)
            {
                characterOwners.Add(lineOwners[lineIndex]);
            }
        }

        foreach (var ruby in advanced.RubyAnnotations)
        {
            var owner = characterOwners[ruby.BaseCharacterIndex];
            for (var characterIndex = ruby.BaseCharacterIndex + 1;
                 characterIndex < ruby.BaseCharacterIndex + ruby.BaseCharacterLength;
                 characterIndex++)
            {
                if (characterOwners[characterIndex] != owner)
                {
                    throw new DialogueModelOutputException(
                        $"[grouping] Segment '{segmentId}' ruby range crosses placement groups.");
                }
            }
        }
    }

    private static Dictionary<string, int> BuildSpatialRegionClusters(
        DialoguePageInput page,
        IReadOnlyList<string> sourceRegionIds)
    {
        var pageRegions = page.DetectedTextRegions.ToDictionary(
            item => item.RegionId,
            StringComparer.Ordinal);
        var regions = sourceRegionIds
            .Select(regionId => pageRegions[regionId])
            .ToArray();
        var clusterByRegion = new Dictionary<string, int>(StringComparer.Ordinal);
        var clusterIndex = 0;
        for (var startIndex = 0; startIndex < regions.Length; startIndex++)
        {
            if (clusterByRegion.ContainsKey(regions[startIndex].RegionId))
            {
                continue;
            }

            var pending = new Stack<int>();
            pending.Push(startIndex);
            clusterByRegion.Add(regions[startIndex].RegionId, clusterIndex);
            while (pending.Count > 0)
            {
                var currentIndex = pending.Pop();
                for (var candidateIndex = 0; candidateIndex < regions.Length; candidateIndex++)
                {
                    var candidate = regions[candidateIndex];
                    if (clusterByRegion.ContainsKey(candidate.RegionId)
                        || !AreSpatiallyContinuous(
                            regions[currentIndex].Region,
                            candidate.Region))
                    {
                        continue;
                    }

                    clusterByRegion.Add(candidate.RegionId, clusterIndex);
                    pending.Push(candidateIndex);
                }
            }

            clusterIndex++;
        }

        return clusterByRegion;
    }

    private static bool AreSpatiallyContinuous(
        MangaTranslator.LocalDetection.DetectedTextRegion left,
        MangaTranslator.LocalDetection.DetectedTextRegion right)
    {
        var horizontalGap = Math.Max(
            0,
            Math.Max(left.X0, right.X0) - Math.Min(left.X1, right.X1));
        var verticalGap = Math.Max(
            0,
            Math.Max(left.Y0, right.Y0) - Math.Min(left.Y1, right.Y1));
        var linkDistance = Math.Max(
            1,
            Math.Max(left.FontSize, right.FontSize)
                * SpatialRegionLinkDistanceInFontSizes);
        return Math.Sqrt(
            (double)horizontalGap * horizontalGap
            + (double)verticalGap * verticalGap) <= linkDistance;
    }

    private static bool HasUngroupedSpatialCavities(
        DialoguePageInput page,
        IReadOnlyList<string> sourceRegionIds,
        IReadOnlyList<DialoguePlacementGroup> placementGroups) =>
        HasUngroupedSpatialCavities(
            BuildSpatialRegionClusters(page, sourceRegionIds),
            placementGroups);

    private static bool HasUngroupedSpatialCavities(
        Dictionary<string, int> spatialClusterByRegion,
        IReadOnlyList<DialoguePlacementGroup> placementGroups) =>
        placementGroups.Count == 0
        && spatialClusterByRegion.Values.Distinct().Skip(1).Any();

    private static bool IsValidPageBox(TextLayoutBox box, DialoguePageInput page) =>
        box.X0 >= 0
        && box.Y0 >= 0
        && box.X1 > box.X0
        && box.Y1 > box.Y0
        && box.X1 <= page.Width
        && box.Y1 <= page.Height;

    private static bool Contains(TextLayoutBox outer, TextLayoutBox inner) =>
        inner.X0 >= outer.X0
        && inner.Y0 >= outer.Y0
        && inner.X1 <= outer.X1
        && inner.Y1 <= outer.Y1;

    private static bool ValidOverride(float? fontSize, string? color)
    {
        return (fontSize is null || fontSize > 0 && float.IsFinite(fontSize.Value))
            && (color is null || IsColor(color));
    }

    private static FontReferenceFont ResolveFont(
        string modelFontId,
        Dictionary<string, FontReferenceFont> fontIds)
    {
        if (string.IsNullOrWhiteSpace(modelFontId)
            || !fontIds.TryGetValue(modelFontId, out var font))
        {
            throw new DialogueModelOutputException(
                $"Unknown font_id '{modelFontId}'.");
        }

        return font;
    }

    private static void ValidateSegmentRegionIds(
        IReadOnlyList<string> candidateIds,
        HashSet<string> knownIds,
        HashSet<string> coveredIds,
        string owner)
    {
        var idsInSegment = new HashSet<string>(StringComparer.Ordinal);
        foreach (var regionId in candidateIds)
        {
            if (!knownIds.Contains(regionId))
            {
                throw new DialogueModelOutputException(
                    $"{owner} references unknown region_id '{regionId}'.");
            }

            if (!idsInSegment.Add(regionId))
            {
                throw new DialogueModelOutputException(
                    $"region_id '{regionId}' is repeated inside {owner}.");
            }

            if (!coveredIds.Add(regionId))
            {
                throw new DialogueModelOutputException(
                    $"[grouping] region_id '{regionId}' is owned by more than one segment. "
                    + "Each detected region must have exactly one logical-lettering-unit owner.");
            }
        }
    }

    private static void ValidateIgnoredRegionIds(
        IReadOnlyList<string> candidateIds,
        HashSet<string> knownIds,
        HashSet<string> segmentIds,
        HashSet<string> ignoredIds,
        string owner)
    {
        foreach (var regionId in candidateIds)
        {
            if (!knownIds.Contains(regionId))
            {
                throw new DialogueModelOutputException(
                    $"{owner} references unknown region_id '{regionId}'.");
            }

            if (segmentIds.Contains(regionId))
            {
                throw new DialogueModelOutputException(
                    $"region_id '{regionId}' cannot be both rendered and ignored.");
            }

            if (!ignoredIds.Add(regionId))
            {
                throw new DialogueModelOutputException(
                    $"region_id '{regionId}' is repeated in ignored_regions.");
            }
        }
    }

    private static ModelEnvelope ParseResponse(ModelResponse response)
    {
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DialogueModelOutputException("The model returned no dialogue JSON.");
        }

        return JsonSerializer.Deserialize<ModelEnvelope>(json, JsonOptions)
            ?? throw new DialogueModelOutputException("The dialogue JSON root was null.");
    }

    private static void EnsureCompleted(ModelResponse response, string pageId)
    {
        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new DialogueTranslationException(
                $"The model refused dialogue translation for {pageId}: {response.Refusal}");
        }

        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new DialogueTranslationException(
                $"The model did not complete dialogue translation for {pageId}: "
                + response.Status);
        }
    }

    private async Task ScheduleRetryAsync(
        DialogueTranslationRequest request,
        int attempt,
        int maximumAttempts,
        string errorCode,
        string detail,
        CancellationToken cancellationToken)
    {
        _progress.Report(new DialogueProgress(
            request.Page.PageId,
            "retry_scheduled",
            attempt,
            maximumAttempts,
            detail,
            ErrorCode: errorCode));
        var baseDelayMilliseconds = request.Options.RetryBaseDelay.TotalMilliseconds;
        if (baseDelayMilliseconds <= 0)
        {
            return;
        }

        var exponential = Math.Pow(2, attempt - 1) * baseDelayMilliseconds;
        var jitterMaximum = (int)Math.Min(250, Math.Ceiling(baseDelayMilliseconds));
        var jitter = jitterMaximum <= 0 ? 0 : Random.Shared.Next(0, jitterMaximum);
        var delay = TimeSpan.FromMilliseconds(exponential + jitter);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeNewlines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static bool IsColor(string value) => ColorPattern().IsMatch(value);

    private static bool IsImageMediaType(string? value) => value is not null
        && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && value.Length > "image/".Length;

    private static bool IsDefinedJson(JsonElement value) =>
        value.ValueKind != JsonValueKind.Undefined;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    private sealed record ModelEnvelope(
        [property: JsonPropertyName("segments")]
        IReadOnlyList<DialogueSegment> Segments,
        [property: JsonPropertyName("ignored_regions")]
        IReadOnlyList<DialogueIgnoredRegion> IgnoredRegions,
        [property: JsonPropertyName("warnings")]
        IReadOnlyList<DialogueWarning> Warnings);

    private sealed class DialogueModelOutputException : Exception
    {
        public DialogueModelOutputException(string message)
            : base(message)
        {
        }
    }
}
