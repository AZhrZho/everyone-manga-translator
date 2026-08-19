using System.Text.Json;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;

namespace MangaTranslator.Dialogue.Tests;

public sealed class ModelBackedMultimodalDialogueBatchTranslatorTests
{
    [Fact]
    public async Task BatchRequestContainsOnlySubmittedPagesAndOneSharedFontReference()
    {
        var provider = new RecordingProvider(request => BatchJson("page-1", "page-3"));
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            QuickDialoguePromptProvider.Instance);

        var results = await translator.TranslateAsync(new DialogueTranslationBatchRequest(
            "batch-run",
            "book-1",
            [CreateRequest("page-1", 1), CreateRequest("page-3", 3)]));

        Assert.Equal(["page-1", "page-3"], results.Select(item => item.PageId));
        var request = Assert.Single(provider.Requests);
        var content = request.Messages.Single(item => item.Role == ModelRole.User).Content;
        Assert.Equal(3, content.Count(item => item is ImageContent));
        var prompt = string.Join('\n', content.OfType<TextContent>().Select(item => item.Text));
        Assert.Contains("PAGE_ID=page-1", prompt, StringComparison.Ordinal);
        Assert.Contains("PAGE_ID=page-3", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("page-2", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpertBatchKeepsBookIndexContextWithoutAttachingUnsubmittedPageImages()
    {
        var provider = new RecordingProvider(request => BatchJson("page-1", "page-3"));
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            ExpertDialoguePromptProvider.Instance);
        var bookPageIndex = new[]
        {
            new DialogueBookPageIndexEntry("page-1", 1),
            new DialogueBookPageIndexEntry("page-2", 2),
            new DialogueBookPageIndexEntry("page-3", 3)
        };

        await translator.TranslateAsync(new DialogueTranslationBatchRequest(
            "batch-run",
            "book-1",
            [
                CreateRequest("page-1", 1) with { BookPageIndex = bookPageIndex },
                CreateRequest("page-3", 3) with { BookPageIndex = bookPageIndex }
            ]));

        var request = Assert.Single(provider.Requests);
        var content = request.Messages.Single(item => item.Role == ModelRole.User).Content;
        Assert.Equal(3, content.Count(item => item is ImageContent));
        var prompt = string.Join('\n', content.OfType<TextContent>().Select(item => item.Text));
        Assert.Contains("\"page_id\":\"page-2\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ORIGINAL_PAGE_IMAGE PAGE_ID=page-2", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task RejectsInvalidBatchPageIdentity(string mode)
    {
        var json = mode switch
        {
            "missing" => BatchJson("page-1"),
            "duplicate" => BatchJson("page-1", "page-1"),
            _ => BatchJson("page-1", "page-9")
        };
        var provider = new RecordingProvider(_ => json);
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            QuickDialoguePromptProvider.Instance);

        await Assert.ThrowsAsync<DialogueTranslationException>(() => translator.TranslateAsync(
            new DialogueTranslationBatchRequest(
                "batch-run",
                "book-1",
                [CreateRequest("page-1", 1), CreateRequest("page-2", 2)])));
    }

    [Fact]
    public async Task RetryableProviderFailureRetriesThreeTimesBeforeSucceeding()
    {
        var provider = new FailingProvider(3, true, BatchJson("page-1"));
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            QuickDialoguePromptProvider.Instance);

        var result = await translator.TranslateAsync(new DialogueTranslationBatchRequest(
            "batch-run",
            "book-1",
            [CreateRequest("page-1", 1)]));

        Assert.Single(result);
        Assert.Equal(4, provider.Attempts);
    }

    [Fact]
    public async Task RetryableProviderFailureStopsAfterThreeRetries()
    {
        var provider = new FailingProvider(4, true, BatchJson("page-1"));
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            QuickDialoguePromptProvider.Instance);

        var exception = await Assert.ThrowsAsync<DialogueTranslationException>(() =>
            translator.TranslateAsync(new DialogueTranslationBatchRequest(
                "batch-run",
                "book-1",
                [CreateRequest("page-1", 1)])));

        Assert.Equal(4, provider.Attempts);
        Assert.Contains("stream_disconnected", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ModelProviderException>(exception.InnerException);
    }

    [Fact]
    public async Task NonRetryableProviderFailureIsNotRetried()
    {
        var provider = new FailingProvider(1, false, BatchJson("page-1"));
        var translator = new ModelBackedMultimodalDialogueBatchTranslator(
            provider,
            QuickDialoguePromptProvider.Instance);

        await Assert.ThrowsAsync<DialogueTranslationException>(() =>
            translator.TranslateAsync(new DialogueTranslationBatchRequest(
                "batch-run",
                "book-1",
                [CreateRequest("page-1", 1)])));

        Assert.Equal(1, provider.Attempts);
    }

    private static DialogueTranslationRequest CreateRequest(string pageId, int order)
    {
        var knowledge = EmptyKnowledge();
        return new DialogueTranslationRequest(
            "run-" + pageId,
            "book-1",
            new DialoguePageInput(
                pageId,
                order,
                null,
                pageId,
                "story",
                true,
                32,
                48,
                "image/png",
                [new DialogueDetectedRegion(
                    pageId + ":region:0001",
                    0,
                    new DetectedTextRegion(
                        2, 3, 20, 28,
                        DetectedTextLanguage.Japanese,
                        DetectedTextOrientation.Vertical,
                        12,
                        .9f,
                        []))]),
            knowledge,
            new FixedImages(),
            new FixedImages(),
            new DialogueTranslationOptions
            {
                Model = "vision-model",
                TargetLanguage = "zh-Hans",
                MaxModelAttempts = 1,
                RetryBaseDelay = TimeSpan.Zero
            })
        {
            BookPageIndex = [new DialogueBookPageIndexEntry(pageId, order)]
        };
    }

    private static GlobalSemanticMergeResult EmptyKnowledge()
    {
        using var arrays = JsonDocument.Parse("[]");
        using var objects = JsonDocument.Parse("{}");
        return new GlobalSemanticMergeResult(
            "zh-Hans",
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            objects.RootElement.Clone());
    }

    private static string BatchJson(params string[] pageIds) => JsonSerializer.Serialize(new
    {
        pages = pageIds.Select(pageId => new
        {
            page_id = pageId,
            segments = Array.Empty<object>(),
            ignored_regions = new[]
            {
                new
                {
                    region_ids = new[] { pageId + ":region:0001" },
                    reason = "not translatable",
                    evidence = "fixture"
                }
            },
            warnings = Array.Empty<object>()
        })
    });

    private sealed class FixedImages : IDialogueImageProvider
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII=");

        public ValueTask<DialogueImageAsset> GetOriginalPageAsync(
            string requestedPageId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DialogueImageAsset("page:" + requestedPageId, "image/png", Png, 32, 48));

        public ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(
            FontReferenceImageRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new FontReferenceSnapshot(
            "fonts-1",
            1,
            new DialogueImageAsset("fonts", "image/png", Png, 1, 1),
            [new FontReferenceFont(
                "font-1",
                "Font",
                "Font",
                null,
                [],
                [new FontReferenceStyle("regular", "native", "regular", "Regular", 0, [])])]));
    }

    private sealed class RecordingProvider(Func<ModelRequest, string> response) : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public string ProviderId => "recording";
        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ResponseCreated("response-1");
            yield return new OutputTextDelta(response(request));
            yield return new ResponseCompleted(new ModelResponseMetadata("response-1", TimeSpan.Zero));
            await Task.CompletedTask;
        }
    }

    private sealed class FailingProvider(
        int failuresBeforeSuccess,
        bool retryable,
        string response) : IModelProvider
    {
        public int Attempts { get; private set; }
        public string ProviderId => "failing";
        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            yield return new ResponseCreated("response-" + Attempts);
            if (Attempts <= failuresBeforeSuccess)
            {
                yield return new ResponseFailed(new ModelProviderError(
                    "stream_disconnected",
                    "stream disconnected before completion",
                    retryable));
                yield break;
            }

            yield return new OutputTextDelta(response);
            yield return new ResponseCompleted(new ModelResponseMetadata(
                "response-" + Attempts,
                TimeSpan.Zero));
            await Task.CompletedTask;
        }
    }
}
