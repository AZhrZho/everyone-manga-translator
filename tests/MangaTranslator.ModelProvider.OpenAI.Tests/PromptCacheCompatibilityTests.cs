using System.Net;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.ModelProvider.OpenAI;

namespace MangaTranslator.ModelProvider.OpenAI.Tests;

public sealed class PromptCacheCompatibilityTests
{
    [Theory]
    [InlineData(false, "Invalid model")]
    [InlineData(true, "Invalid model")]
    [InlineData(false, "Unsupported parameter: temperature")]
    [InlineData(true, "Unsupported parameter: temperature")]
    [InlineData(false, "Invalid value for prompt_cache_retention")]
    [InlineData(true, "Invalid value for prompt_cache_retention")]
    public async Task UnrelatedBadRequestIsPreserved(bool chat, string message)
    {
        var handler = new CompatibilityHandler(message);
        using var client = new HttpClient(handler);
        var events = await CollectAsync(CreateProvider(chat, client).StreamAsync(CreateRequest()));
        Assert.Equal(1, handler.CallCount);
        var failure = Assert.IsType<ResponseFailed>(Assert.Single(events));
        Assert.Contains(message, failure.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedFallbackStopsAfterOneRetry(bool chat)
    {
        var handler = new CompatibilityHandler("Unsupported parameter: prompt_cache_retention");
        using var client = new HttpClient(handler);
        var events = await CollectAsync(CreateProvider(chat, client).StreamAsync(CreateRequest()));
        Assert.Equal(2, handler.CallCount);
        Assert.IsType<ResponseFailed>(Assert.Single(events));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackHonorsTotalTimeout(bool chat)
    {
        var handler = new CompatibilityHandler("Unsupported parameter: prompt_cache_retention", delayFallback: true);
        using var client = new HttpClient(handler);
        var events = await CollectAsync(CreateProvider(chat, client).StreamAsync(CreateRequest(TimeSpan.FromMilliseconds(200))));
        Assert.Equal(2, handler.CallCount);
        var failure = Assert.IsType<ResponseFailed>(Assert.Single(events));
        Assert.Equal("request_timeout", failure.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackHonorsCallerCancellation(bool chat)
    {
        var handler = new CompatibilityHandler("Unsupported parameter: prompt_cache_retention", delayFallback: true);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = CollectAsync(CreateProvider(chat, client).StreamAsync(CreateRequest(), cancellation.Token));
        await handler.FallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.IsType<ResponseCancelled>(Assert.Single(await task));
        Assert.Equal(2, handler.CallCount);
    }

    private static IModelProvider CreateProvider(bool chat, HttpClient client) => chat
        ? new OpenAIChatModelProvider(client, new OpenAIChatModelProviderOptions { ApiKey = "test-key" })
        : new OpenAIModelProvider(client, new OpenAIModelProviderOptions { ApiKey = "test-key" });

    private static ModelRequest CreateRequest(TimeSpan? timeout = null) => new(
        "test-model", [new ModelMessage(ModelRole.User, "translate")],
        options: new ModelGenerationOptions
        {
            PromptCacheRetention = PromptCacheRetention.TwentyFourHours,
            TotalTimeout = timeout
        });

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> events)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in events) result.Add(item);
        return result;
    }

    private sealed class CompatibilityHandler(string message, bool delayFallback = false) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource FallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 2 && delayFallback)
            {
                FallbackStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(message)
            };
        }
    }
}
