using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Dialogue;

public sealed class ModelBackedMultimodalDialogueBatchTranslator(
    IModelProvider modelProvider,
    IDialoguePromptProvider prompts) : IMultimodalDialogueBatchTranslator
{
    private const int MaximumApiRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DialogueTranslationResult>> TranslateAsync(
        DialogueTranslationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ValidateCapabilities(modelProvider);
        var first = request.Pages[0];
        var fontReference = await first.FontReferenceImages.GetFontReferenceImageAsync(
            new FontReferenceImageRequest(
                first.BookId,
                first.Page.PageId,
                first.Options.TargetLanguage),
            cancellationToken);
        var pageAssets = new List<(DialogueTranslationRequest Request, DialogueImageAsset Image)>(request.Pages.Count);
        foreach (var page in request.Pages)
        {
            var image = await page.PageImages.GetOriginalPageAsync(page.Page.PageId, cancellationToken);
            if (image.Width != page.Page.Width || image.Height != page.Page.Height
                || !string.Equals(image.MediaType, page.Page.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new DialogueTranslationException(
                    $"Original image for {page.Page.PageId} does not match its batch manifest.");
            }
            pageAssets.Add((page, image));
        }

        var fontIds = fontReference.Fonts.Select(item => item.FontId).ToArray();
        var schema = DialogueSchemas.BatchResult(fontIds);
        var maximumAttempts = Math.Max(1, first.Options.MaxModelAttempts);
        Exception? lastException = null;
        string? retryFeedback = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await GenerateWithRetryAsync(
                    BuildModelRequest(request, pageAssets, fontReference, schema, retryFeedback),
                    first.Options.RetryBaseDelay,
                    cancellationToken);
                EnsureCompleted(response, request.Pages.Select(item => item.Page.PageId));
                var pages = ParsePages(response);
                ValidatePageIdentity(request.Pages, pages);
                var results = new List<DialogueTranslationResult>(request.Pages.Count);
                foreach (var page in request.Pages)
                {
                    var payload = pages.Single(item => string.Equals(
                        item.PageId,
                        page.Page.PageId,
                        StringComparison.Ordinal));
                    var validatingProvider = new FixedResponseProvider(
                        response.ProviderId,
                        payload.Result,
                        response.Usage,
                        response.Metadata);
                    var validator = new ModelBackedMultimodalDialogueTranslator(validatingProvider, prompts);
                    results.Add(await validator.TranslateAsync(
                        page with
                        {
                            Options = page.Options with
                            {
                                MaxModelAttempts = 1,
                                RetryBaseDelay = TimeSpan.Zero
                            }
                        },
                        cancellationToken));
                }
                return results;
            }
            catch (Exception exception) when (
                exception is JsonException or DialogueTranslationException
                && attempt < maximumAttempts)
            {
                lastException = exception;
                retryFeedback = "The previous batch output failed host validation: "
                    + exception.Message
                    + " Return one complete replacement object. Include every requested page_id exactly once, "
                    + "and repair the invalid page without changing the other pages' ownership.";
                var delay = first.Options.RetryBaseDelay.TotalMilliseconds <= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(
                        Math.Pow(2, attempt - 1) * first.Options.RetryBaseDelay.TotalMilliseconds);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
            catch (ModelProviderException exception)
            {
                throw new DialogueTranslationException(
                    $"The model provider failed dialogue batch {request.RunId} "
                    + $"[{exception.Error.Code}]: {exception.Error.Message}",
                    exception);
            }
        }

        throw new DialogueTranslationException(
            $"Dialogue batch {request.RunId} exhausted {maximumAttempts} attempts.",
            lastException ?? new InvalidOperationException("No model attempt was recorded."));
    }

    private async Task<ModelResponse> GenerateWithRetryAsync(
        ModelRequest request,
        TimeSpan retryBaseDelay,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await modelProvider.GenerateAsync(request, cancellationToken);
            }
            catch (ModelProviderException exception) when (
                exception.Error.IsRetryable && retry < MaximumApiRetries)
            {
                var delay = retryBaseDelay.TotalMilliseconds <= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(
                        Math.Pow(2, retry) * retryBaseDelay.TotalMilliseconds);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
    }

    private ModelRequest BuildModelRequest(
        DialogueTranslationBatchRequest batch,
        List<(DialogueTranslationRequest Request, DialogueImageAsset Image)> pages,
        FontReferenceSnapshot fontReference,
        JsonElement schema,
        string? retryFeedback)
    {
        var first = batch.Pages[0];
        var systemPrompt = prompts.System(first.Options.TargetLanguage)
            + "\n\nBATCH CONTRACT: The user message contains only the pages selected for this task. "
            + "Do not request, infer, or emit results for any other page. Treat each supplied image as an independent "
            + "page identified by PAGE_ID. Return a pages array with every supplied page_id exactly once.";
        var context = prompts.Context(first, fontReference);
        var userContent = new List<ModelContent> { new TextContent(context) };
        foreach (var (request, image) in pages)
        {
            userContent.Add(new TextContent(prompts.PageManifest(request, image)));
            userContent.Add(new TextContent(
                $"ORIGINAL_PAGE_IMAGE PAGE_ID={request.Page.PageId}; SIZE={image.Width}x{image.Height}; "
                + "this image belongs only to the immediately preceding manifest."));
            userContent.Add(new ImageContent(
                image.Data,
                image.MediaType,
                request.Options.PageImageDetail));
        }
        userContent.Add(new TextContent(
            $"FONT_REFERENCE_IMAGE REFERENCE_ID={fontReference.ReferenceId}; shared by all pages in this batch."));
        userContent.Add(new ImageContent(
            fontReference.Image.Data,
            fontReference.Image.MediaType,
            first.Options.FontReferenceImageDetail));
        if (!string.IsNullOrWhiteSpace(retryFeedback))
        {
            userContent.Add(new TextContent("RETRY_VALIDATION_FEEDBACK: " + retryFeedback));
        }

        return new ModelRequest(
            first.Options.Model,
            [
                new ModelMessage(ModelRole.System, systemPrompt),
                new ModelMessage(ModelRole.User, userContent)
            ],
            new JsonSchemaOutputFormat("manga_dialogue_translation_batch", schema),
            new ModelGenerationOptions
            {
                MaxOutputTokens = Math.Min(
                    65_536,
                    checked(first.Options.MaxOutputTokens * pages.Count)),
                ReasoningEffort = first.Options.ReasoningEffort,
                PromptCacheKey = ResolvePromptCacheKey(first, systemPrompt, context, schema, fontReference),
                PromptCacheRetention = first.Options.EnablePromptCaching
                    ? first.Options.PromptCacheRetention
                    : null,
                TotalTimeout = first.Options.TotalTimeout
            },
            new Dictionary<string, string>
            {
                ["run_id"] = batch.RunId,
                ["book_id"] = batch.BookId,
                ["page_ids"] = string.Join(",", pages.Select(item => item.Request.Page.PageId)),
                ["stage"] = "dialogue_translation_layout_batch",
                ["prompt_profile"] = prompts.ProfileId,
                ["font_reference_id"] = fontReference.ReferenceId
            });
    }

    private static string? ResolvePromptCacheKey(
        DialogueTranslationRequest request,
        string systemPrompt,
        string context,
        JsonElement schema,
        FontReferenceSnapshot fontReference)
    {
        if (!request.Options.EnablePromptCaching) return null;
        if (request.Options.PromptCacheKey is not null) return request.Options.PromptCacheKey;
        var material = string.Join('\n',
            "model=" + request.Options.Model,
            "font_image_sha256=" + Convert.ToHexString(SHA256.HashData(fontReference.Image.Data.Span)),
            "system_prompt=" + systemPrompt,
            "context=" + context,
            "schema=" + schema.GetRawText());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "dlg_batch_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static BatchPageEnvelope[] ParsePages(ModelResponse response)
    {
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DialogueTranslationException("The model returned no batch dialogue JSON.");
        }
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new DialogueTranslationException("The batch dialogue JSON root was not an object.");
        var pages = root["pages"] as JsonArray
            ?? throw new DialogueTranslationException("The batch dialogue JSON has no pages array.");
        return pages.Select(node =>
        {
            var page = node as JsonObject
                ?? throw new DialogueTranslationException("A batch page result was not an object.");
            var pageId = page["page_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new DialogueTranslationException("A batch page result has no page_id.");
            }
            var result = page.DeepClone().AsObject();
            result.Remove("page_id");
            using var document = JsonDocument.Parse(result.ToJsonString(JsonOptions));
            return new BatchPageEnvelope(pageId, document.RootElement.Clone());
        }).ToArray();
    }

    private static void ValidatePageIdentity(
        IReadOnlyList<DialogueTranslationRequest> requests,
        IReadOnlyList<BatchPageEnvelope> results)
    {
        var expected = requests.Select(item => item.Page.PageId).ToHashSet(StringComparer.Ordinal);
        var duplicates = results.GroupBy(item => item.PageId, StringComparer.Ordinal)
            .Where(item => item.Count() > 1)
            .Select(item => item.Key)
            .ToArray();
        var actual = results.Select(item => item.PageId).ToHashSet(StringComparer.Ordinal);
        if (duplicates.Length > 0 || !expected.SetEquals(actual) || results.Count != requests.Count)
        {
            throw new DialogueTranslationException(
                "Batch page_id values must match the requested pages exactly. "
                + $"Expected: {string.Join(",", expected.Order())}; actual: {string.Join(",", results.Select(item => item.PageId))}.");
        }
    }

    private static void ValidateRequest(DialogueTranslationBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Pages);
        if (string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.BookId)
            || request.Pages.Count == 0)
        {
            throw new ArgumentException("A batch run, book, and at least one page are required.", nameof(request));
        }
        var first = request.Pages[0];
        if (request.Pages.Any(item => item is null
            || !string.Equals(item.BookId, first.BookId, StringComparison.Ordinal)
            || !string.Equals(item.Options.Model, first.Options.Model, StringComparison.Ordinal)
            || !string.Equals(item.Options.TargetLanguage, first.Options.TargetLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("All pages in a dialogue batch must share book, model, and target language.", nameof(request));
        }
        var duplicateIds = request.Pages.GroupBy(item => item.Page.PageId, StringComparer.Ordinal)
            .Where(item => item.Count() > 1)
            .Select(item => item.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new ArgumentException("Dialogue batch page IDs must be unique.", nameof(request));
        }
    }

    private static void ValidateCapabilities(IModelProvider provider)
    {
        var capabilities = provider.Capabilities;
        if (!capabilities.SupportsVision || !capabilities.SupportsMultipleImages
            || !capabilities.SupportsStructuredOutputs || !capabilities.SupportsStreaming)
        {
            throw new DialogueTranslationException(
                $"Model provider '{provider.ProviderId}' must support multiple vision inputs, structured outputs, and streaming.");
        }
    }

    private static void EnsureCompleted(ModelResponse response, IEnumerable<string> pageIds)
    {
        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new DialogueTranslationException(
                $"The model did not complete dialogue translation for pages {string.Join(",", pageIds)}: {response.Status}.");
        }
    }

    private sealed record BatchPageEnvelope(string PageId, JsonElement Result);

    private sealed class FixedResponseProvider(
        string providerId,
        JsonElement result,
        ModelUsage? usage,
        ModelResponseMetadata metadata) : IModelProvider
    {
        public string ProviderId => providerId;
        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCreated(metadata.ProviderRequestId ?? Guid.NewGuid().ToString("N"));
            yield return new OutputTextDelta(result.GetRawText());
            if (usage is not null) yield return new UsageReported(usage);
            yield return new ResponseCompleted(metadata);
            await Task.CompletedTask;
        }
    }
}
