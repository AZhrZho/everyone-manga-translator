using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.ModelProvider.OpenAI;

namespace MangaTranslator.ModelProvider.OpenAI.Tests;

public sealed class OpenAIModelProviderTests
{
    [Fact]
    public async Task StreamAsyncMapsResponsesEventsAndSendsMultimodalStructuredRequest()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions
            {
                ApiKey = "test-key",
                Endpoint = new Uri("https://example.test/v1/responses")
            });

        using var schemaDocument = System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}");
        var request = new ModelRequest(
            "gpt-5.6",
            [new ModelMessage(
                ModelRole.User,
                [
                    new TextContent("inspect this page"),
                    new ImageContent("image-bytes"u8.ToArray(), "image/png", ImageDetailLevel.Original)
                ])],
            new JsonSchemaOutputFormat("test_result", schemaDocument.RootElement),
            new ModelGenerationOptions { MaxOutputTokens = 200 });

        var events = await CollectAsync(provider.StreamAsync(request));

        Assert.Collection(
            events,
            item => Assert.IsType<ResponseCreated>(item),
            item => Assert.Equal("{\"ok\":", Assert.IsType<OutputTextDelta>(item).Delta),
            item => Assert.Equal("true}", Assert.IsType<OutputTextDelta>(item).Delta),
            item => Assert.IsType<UsageReported>(item),
            item => Assert.IsType<ResponseCompleted>(item));

        Assert.NotNull(handler.RequestBody);
        Assert.Contains("\"stream\":true", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("input_image", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("test_result", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_key", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_retention", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal("Bearer test-key", handler.Authorization);
        TestArtifactWriter.WriteText(
            nameof(StreamAsyncMapsResponsesEventsAndSendsMultimodalStructuredRequest),
            "requests/responses_request.json",
            handler.RequestBody);
        TestArtifactWriter.WriteJson(
            nameof(StreamAsyncMapsResponsesEventsAndSendsMultimodalStructuredRequest),
            "events/stream_events.json",
            events.Select(item => new { type = item.GetType().Name }).ToArray());
    }

    [Fact]
    public async Task GenerateAsyncAggregatesStreamAndParsesJsonOutput()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });
        using var schemaDocument = System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

        var response = await provider.GenerateAsync(new ModelRequest(
            "gpt-5.6",
            [new ModelMessage(ModelRole.User, "return JSON")],
            new JsonSchemaOutputFormat("result", schemaDocument.RootElement)));

        Assert.Equal(ModelResponseStatus.Completed, response.Status);
        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal(true, response.Json?.GetProperty("ok").GetBoolean());
        TestArtifactWriter.WriteJson(
            nameof(GenerateAsyncAggregatesStreamAndParsesJsonOutput),
            "responses/aggregated_response.json",
            response);
    }

    [Theory]
    [InlineData(PromptCacheRetention.InMemory, "in_memory")]
    [InlineData(PromptCacheRetention.TwentyFourHours, "24h")]
    public async Task StreamAsyncSendsPromptCacheOptionsAndReadsCachedTokens(
        PromptCacheRetention retention,
        string expectedRetention)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });
        var request = new ModelRequest(
            "gpt-5.4",
            [new ModelMessage(ModelRole.User, "translate this page")],
            options: new ModelGenerationOptions
            {
                PromptCacheKey = "book-sample:zh-Hans:semantic-v3",
                PromptCacheRetention = retention
            });

        var events = await CollectAsync(provider.StreamAsync(request));

        Assert.NotNull(handler.RequestBody);
        using var requestDocument = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        var root = requestDocument.RootElement;
        Assert.Equal(
            "book-sample:zh-Hans:semantic-v3",
            root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(
            expectedRetention,
            root.GetProperty("prompt_cache_retention").GetString());
        var usage = Assert.Single(events.OfType<UsageReported>()).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(15, usage.TotalTokens);
        Assert.Equal(7, usage.CachedInputTokens);
        TestArtifactWriter.WriteText(
            $"{nameof(StreamAsyncSendsPromptCacheOptionsAndReadsCachedTokens)}_{expectedRetention}",
            "requests/responses_prompt_cache_request.json",
            handler.RequestBody);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamAsyncRejectsBlankPromptCacheKey(string promptCacheKey)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });
        var request = new ModelRequest(
            "gpt-5.4",
            [new ModelMessage(ModelRole.User, "translate this page")],
            options: new ModelGenerationOptions
            {
                PromptCacheKey = promptCacheKey
            });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => CollectAsync(provider.StreamAsync(request)));

        Assert.Contains("PromptCacheKey", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task StreamAsyncRetriesWithoutUnsupportedPromptCacheRetention()
    {
        var handler = new UnsupportedPromptCacheRetentionHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });
        var request = new ModelRequest(
            "gpt-5.4",
            [new ModelMessage(ModelRole.User, "translate this page")],
            options: new ModelGenerationOptions
            {
                PromptCacheKey = "book-sample:zh-Hans:semantic-v3",
                PromptCacheRetention = PromptCacheRetention.TwentyFourHours
            });

        var events = await CollectAsync(provider.StreamAsync(request));

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("prompt_cache_retention", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_retention", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("prompt_cache_key", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains(events, item => item is ResponseCompleted);
    }

    [Fact]
    public async Task StreamAsyncReportsInterruptedStreamWhenCompletionEventIsMissing()
    {
        using var httpClient = new HttpClient(new IncompleteStreamHandler());
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });

        var events = await CollectAsync(provider.StreamAsync(new ModelRequest(
            "gpt-5.6",
            [new ModelMessage(ModelRole.User, "return text")])));

        var failure = Assert.IsType<ResponseFailed>(events[^1]);
        Assert.Equal("stream_ended_without_completion", failure.Error.Code);
        Assert.True(failure.Error.IsRetryable);
        TestArtifactWriter.WriteJson(
            nameof(StreamAsyncReportsInterruptedStreamWhenCompletionEventIsMissing),
            "errors/stream_interruption.json",
            failure.Error);
    }

    [Fact]
    public async Task StreamAsyncReportsRequestTimeoutBeforeResponseHeaders()
    {
        using var httpClient = new HttpClient(new DelayingHandler());
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });

        var events = await CollectAsync(provider.StreamAsync(new ModelRequest(
            "gpt-5.6",
            [new ModelMessage(ModelRole.User, "return text")],
            options: new ModelGenerationOptions
            {
                TotalTimeout = TimeSpan.FromMilliseconds(30)
            })));

        var failure = Assert.IsType<ResponseFailed>(Assert.Single(events));
        Assert.Equal("request_timeout", failure.Error.Code);
        Assert.True(failure.Error.IsRetryable);
        TestArtifactWriter.WriteJson(
            nameof(StreamAsyncReportsRequestTimeoutBeforeResponseHeaders),
            "errors/request_timeout.json",
            failure.Error);
    }

    [Theory]
    [InlineData("request_timeout")]
    [InlineData("server_error")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("server_is_overloaded")]
    [InlineData("upstream_stream_aborted")]
    public async Task StreamAsyncMarksTransientProviderFailureAsRetryable(string code)
    {
        using var httpClient = new HttpClient(new FailedResponseHandler(code));
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions { ApiKey = "test-key" });

        var request = new ModelRequest(
            "gpt-5.6",
            [new ModelMessage(ModelRole.User, "return text")]);
        var events = await CollectAsync(provider.StreamAsync(request));

        var failure = Assert.IsType<ResponseFailed>(Assert.Single(events));
        Assert.Equal(code, failure.Error.Code);
        Assert.True(failure.Error.IsRetryable);
    }

    private static async Task<List<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> events)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var @event in events)
        {
            result.Add(@event);
        }

        return result;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? RequestBody { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();

            const string stream =
                "event: response.created\n" +
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_test\"}}\n\n" +
                "event: response.output_text.delta\n" +
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"ok\\\":\"}\n\n" +
                "event: response.output_text.delta\n" +
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"true}\"}\n\n" +
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15,\"input_tokens_details\":{\"cached_tokens\":7}}}}\n\n";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            response.Headers.TryAddWithoutValidation("x-request-id", "req_test");
            return response;
        }
    }

    private sealed class UnsupportedPromptCacheRetentionHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (RequestBodies.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"detail\":\"Unsupported parameter: prompt_cache_retention\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            const string stream =
                "event: response.created\n" +
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_test\"}}\n\n" +
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"status\":\"completed\"}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class IncompleteStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string stream =
                "event: response.created\n" +
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_incomplete\"}}\n\n" +
                "event: response.output_text.delta\n" +
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FailedResponseHandler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var stream =
                "event: response.failed\n"
                + $"data: {{\"type\":\"response.failed\",\"response_id\":\"resp_failed\",\"error\":{{\"code\":\"{code}\",\"message\":\"transient\"}}}}\n\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }
}
