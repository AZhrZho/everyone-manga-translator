using System.Net;
using System.Text;
using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.ModelProvider.Anthropic;
using MangaTranslator.ModelProvider.OpenAI;

namespace MangaTranslator.ModelProvider.OpenAI.Tests;

public sealed class AdditionalModelProviderTests
{
    [Fact]
    public async Task OpenAIChatMapsStreamAndMultimodalStructuredRequest()
    {
        var handler = new OpenAIChatHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAIChatModelProvider(client, new OpenAIChatModelProviderOptions
        {
            ApiKey = "test-key",
            Endpoint = new Uri("https://example.test/v1/chat/completions")
        });
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""");

        var events = await CollectAsync(provider.StreamAsync(new ModelRequest(
            "gpt-4o",
            [new ModelMessage(ModelRole.User, [new TextContent("inspect"), new ImageContent("image"u8.ToArray(), "image/png")])],
            new JsonSchemaOutputFormat("result", schema.RootElement))));

        Assert.Collection(events,
            item => Assert.IsType<ResponseCreated>(item),
            item => Assert.Equal("{\"ok\":true}", Assert.IsType<OutputTextDelta>(item).Delta),
            item => Assert.IsType<UsageReported>(item),
            item => Assert.IsType<ResponseCompleted>(item));
        Assert.Equal("Bearer test-key", handler.Authorization);
        Assert.Contains("\"image_url\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"response_format\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicMapsStreamAndNativeMessagesRequest()
    {
        var handler = new AnthropicHandler();
        using var client = new HttpClient(handler);
        var provider = new AnthropicModelProvider(client, new AnthropicModelProviderOptions
        {
            ApiKey = "claude-key",
            Endpoint = new Uri("https://example.test/v1/messages")
        });
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""");

        var events = await CollectAsync(provider.StreamAsync(new ModelRequest(
            "claude-sonnet",
            [new ModelMessage(ModelRole.System, "return JSON"), new ModelMessage(ModelRole.User, [new ImageContent("image"u8.ToArray(), "image/png"), new TextContent("inspect")])],
            new JsonSchemaOutputFormat("result", schema.RootElement))));

        Assert.Collection(events,
            item => Assert.IsType<ResponseCreated>(item),
            item => Assert.Equal("{\"ok\":true}", Assert.IsType<OutputTextDelta>(item).Delta),
            item => Assert.IsType<OutputItemCompleted>(item),
            item => Assert.IsType<UsageReported>(item),
            item => Assert.IsType<ResponseCompleted>(item));
        Assert.Equal("claude-key", handler.ApiKey);
        Assert.Equal("Bearer claude-key", handler.Authorization);
        Assert.Equal("2023-06-01", handler.ApiVersion);
        Assert.Contains("\"type\":\"base64\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"output_config\"", handler.Body, StringComparison.Ordinal);
    }

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> events)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in events) result.Add(item);
        return result;
    }

    private sealed class OpenAIChatHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            const string stream =
                "data: {\"id\":\"chatcmpl_test\",\"choices\":[{\"delta\":{\"content\":\"{\\\"ok\\\":true}\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"chatcmpl_test\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4,\"total_tokens\":14}}\n\n" +
                "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class AnthropicHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string? ApiKey { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ApiKey = request.Headers.GetValues("x-api-key").Single();
            Authorization = request.Headers.Authorization?.ToString();
            ApiVersion = request.Headers.GetValues("anthropic-version").Single();
            const string stream =
                "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_test\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"{\\\"ok\\\":true}\"}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\"}\n\n" +
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":4}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
