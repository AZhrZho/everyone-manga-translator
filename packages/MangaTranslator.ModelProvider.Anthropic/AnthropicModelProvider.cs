using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.ModelProvider.Anthropic;

public sealed class AnthropicModelProvider : IModelProvider
{
    private static readonly ModelProviderCapabilities ProviderCapabilities = new(
        SupportsVision: true,
        SupportsMultipleImages: true,
        SupportsStructuredOutputs: true,
        SupportsStreaming: true,
        SupportsOriginalImageDetail: true,
        SupportsReasoningConfiguration: false);

    private readonly HttpClient httpClient;
    private readonly AnthropicModelProviderOptions options;

    public AnthropicModelProvider(HttpClient httpClient, AnthropicModelProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.httpClient = httpClient;
        this.options = options;
    }

    public string ProviderId => "anthropic";

    public ModelProviderCapabilities Capabilities => ProviderCapabilities;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(request.Options.TotalTimeout ?? options.Timeouts.DefaultTotalTimeout);
        using var httpRequest = CreateHttpRequest(request);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage? response = null;
        ModelProviderError? startupError = null;
        var cancelledAtStartup = false;
        try
        {
            response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, totalTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelledAtStartup = true;
        }
        catch (OperationCanceledException exception)
        {
            startupError = new ModelProviderError("request_timeout", exception.Message, true);
        }
        catch (HttpRequestException exception)
        {
            startupError = new ModelProviderError("http_request_failed", exception.Message, true);
        }
        if (cancelledAtStartup)
        {
            yield return new ResponseCancelled();
            yield break;
        }
        if (startupError is not null)
        {
            yield return new ResponseFailed(startupError);
            yield break;
        }

        using (response ?? throw new InvalidOperationException("The Anthropic request completed without a response."))
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(totalTimeout.Token).ConfigureAwait(false);
                yield return new ResponseFailed(CreateHttpError(response, body));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(totalTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var firstEvent = true;
            var eventName = string.Empty;
            var data = new StringBuilder();
            var completed = false;
            string? responseId = null;
            string? finishReason = null;
            JsonElement? finalMetadata = null;
            long inputTokens = 0;
            long outputTokens = 0;
            long cachedInputTokens = 0;

            while (true)
            {
                string? line = null;
                ModelProviderError? readError = null;
                var cancelled = false;
                try
                {
                    var timeout = firstEvent
                        ? request.Options.TimeToFirstEventTimeout ?? options.Timeouts.DefaultTimeToFirstEventTimeout
                        : request.Options.IdleEventTimeout ?? options.Timeouts.DefaultIdleEventTimeout;
                    line = await reader.ReadLineAsync(totalTimeout.Token).AsTask()
                        .WaitAsync(timeout, totalTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (OperationCanceledException exception)
                {
                    readError = new ModelProviderError("stream_total_timeout", exception.Message, true);
                }
                catch (TimeoutException exception)
                {
                    readError = new ModelProviderError(firstEvent ? "first_event_timeout" : "stream_idle_timeout", exception.Message, true);
                }
                catch (IOException exception)
                {
                    readError = new ModelProviderError("stream_interrupted", exception.Message, true);
                }
                if (cancelled)
                {
                    yield return new ResponseCancelled();
                    yield break;
                }
                if (readError is not null)
                {
                    yield return new ResponseFailed(readError);
                    yield break;
                }

                if (line is null) break;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line["event:".Length..].Trim();
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line["data:".Length..].TrimStart());
                    continue;
                }
                if (line.Length != 0 || data.Length == 0) continue;

                firstEvent = false;
                var payload = data.ToString();
                data.Clear();
                if (!TryParseJson(payload, out var document, out var parseError))
                {
                    yield return new ResponseFailed(parseError!);
                    yield break;
                }

                var parsedDocument = document!;
                using (parsedDocument)
                {
                    var root = parsedDocument.RootElement;
                    var type = string.IsNullOrWhiteSpace(eventName) ? GetString(root, "type") ?? string.Empty : eventName;
                    switch (type)
                    {
                        case "message_start":
                            if (root.TryGetProperty("message", out var message))
                            {
                                responseId = GetString(message, "id");
                                ReadUsage(message, ref inputTokens, ref outputTokens, ref cachedInputTokens);
                                finalMetadata = message.Clone();
                            }
                            yield return new ResponseCreated(responseId ?? string.Empty);
                            break;

                        case "content_block_start":
                            if (root.TryGetProperty("content_block", out var block))
                            {
                                var text = GetString(block, "text");
                                if (!string.IsNullOrEmpty(text)) yield return new OutputTextDelta(text);
                            }
                            break;

                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta))
                            {
                                var deltaType = GetString(delta, "type");
                                var text = deltaType == "text_delta" ? GetString(delta, "text") : null;
                                if (!string.IsNullOrEmpty(text)) yield return new OutputTextDelta(text);
                                var refusal = deltaType == "refusal_delta" ? GetString(delta, "refusal") : null;
                                if (!string.IsNullOrEmpty(refusal)) yield return new OutputRefusalDelta(refusal);
                            }
                            break;

                        case "content_block_stop":
                            yield return new OutputItemCompleted("text");
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("delta", out var messageDelta)) finishReason = GetString(messageDelta, "stop_reason");
                            ReadUsage(root, ref inputTokens, ref outputTokens, ref cachedInputTokens);
                            finalMetadata = root.Clone();
                            break;

                        case "message_stop":
                            completed = true;
                            break;

                        case "error":
                            yield return new ResponseFailed(ReadProviderError(root));
                            yield break;
                    }
                }

                eventName = string.Empty;
                if (completed) break;
            }

            if (!completed)
            {
                yield return new ResponseFailed(new ModelProviderError("stream_ended_without_completion", "The Anthropic stream ended before message_stop was received.", true));
                yield break;
            }

            if (inputTokens > 0 || outputTokens > 0)
            {
                yield return new UsageReported(new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens, CachedInputTokens: cachedInputTokens));
            }
            yield return new ResponseCompleted(new ModelResponseMetadata(responseId ?? TryGetHeader(response, "request-id"), stopwatch.Elapsed, finishReason, finalMetadata));
        }
    }

    private HttpRequestMessage CreateHttpRequest(ModelRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        message.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", options.ApiVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new StringContent(BuildRequestBody(request).ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private static JsonObject BuildRequestBody(ModelRequest request)
    {
        var system = new JsonArray();
        var messages = new JsonArray();
        foreach (var modelMessage in request.Messages)
        {
            var content = new JsonArray();
            foreach (var part in modelMessage.Content) content.Add(MapContent(part));
            if (modelMessage.Role is ModelRole.System or ModelRole.Developer)
            {
                foreach (var part in content) system.Add(part?.DeepClone());
            }
            else
            {
                messages.Add(new JsonObject
                {
                    ["role"] = modelMessage.Role == ModelRole.Assistant ? "assistant" : "user",
                    ["content"] = content
                });
            }
        }

        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.Options.MaxOutputTokens ?? 8192,
            ["stream"] = true
        };
        if (system.Count > 0) root["system"] = system;
        if (request.OutputFormat is JsonSchemaOutputFormat schema)
        {
            root["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = JsonNode.Parse(schema.Schema.GetRawText())
                }
            };
        }
        return root;
    }

    private static JsonObject MapContent(ModelContent content) => content switch
    {
        TextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ImageContent image => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = image.MediaType,
                ["data"] = Convert.ToBase64String(image.Data.Span)
            }
        },
        ImageUriContent image => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "url", ["url"] = image.Uri.AbsoluteUri }
        },
        _ => throw new NotSupportedException($"Unsupported model content type: {content.GetType().FullName}")
    };

    private static void ReadUsage(JsonElement root, ref long input, ref long output, ref long cached)
    {
        var usageRoot = root.TryGetProperty("usage", out var usage)
            ? usage
            : root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out usage) ? usage : default;
        if (usageRoot.ValueKind != JsonValueKind.Object) return;
        input = Math.Max(input, GetInt64(usageRoot, "input_tokens") ?? 0);
        output = Math.Max(output, GetInt64(usageRoot, "output_tokens") ?? 0);
        cached = Math.Max(cached, GetInt64(usageRoot, "cache_read_input_tokens") ?? 0);
    }

    private static ModelProviderError CreateHttpError(HttpResponseMessage response, string body)
    {
        var code = "anthropic_http_error";
        var message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "The Anthropic request failed." : body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                code = GetString(error, "type") ?? code;
                message = GetString(error, "message") ?? message;
            }
        }
        catch (JsonException) { }
        return new ModelProviderError(code, message, IsRetryable(response.StatusCode), (int)response.StatusCode, TryGetHeader(response, "request-id"));
    }

    private static ModelProviderError ReadProviderError(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var value) ? value : root;
        var code = GetString(error, "type") ?? "anthropic_error";
        return new ModelProviderError(code, GetString(error, "message") ?? "The Anthropic request failed.", IsRetryable(code), ProviderMetadata: root.Clone());
    }

    private static bool TryParseJson(string payload, out JsonDocument? document, out ModelProviderError? error)
    {
        try
        {
            document = JsonDocument.Parse(payload);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            document = null;
            error = new ModelProviderError("invalid_stream_json", exception.Message, true);
            return false;
        }
    }

    private static void ValidateRequest(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count == 0) throw new ArgumentException("At least one model message is required.", nameof(request));
        if (request.Messages.Any(message => message.Content.Count == 0)) throw new ArgumentException("Model messages cannot have empty content.", nameof(request));
        if (request.Messages.Where(message => message.Role is ModelRole.System or ModelRole.Developer).SelectMany(message => message.Content).Any(content => content is not TextContent))
            throw new NotSupportedException("Anthropic system and developer messages only support text content.");
        if (request.Options.PromptCacheKey is not null && string.IsNullOrWhiteSpace(request.Options.PromptCacheKey))
            throw new ArgumentException("PromptCacheKey cannot be empty or whitespace.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.Options.ReasoningEffort))
            throw new NotSupportedException("Anthropic reasoning effort is not supported by this provider.");
    }

    private static bool IsRetryable(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static bool IsRetryable(string code) => code is "rate_limit_error" or "api_error" or "overloaded_error";
    private static string? TryGetHeader(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetInt64(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
}
