using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.ModelProvider.OpenAI;

public sealed class OpenAIChatModelProvider : IModelProvider
{
    private static readonly ModelProviderCapabilities ProviderCapabilities = new(
        SupportsVision: true,
        SupportsMultipleImages: true,
        SupportsStructuredOutputs: true,
        SupportsStreaming: true,
        SupportsOriginalImageDetail: true,
        SupportsReasoningConfiguration: true);

    private readonly HttpClient httpClient;
    private readonly OpenAIChatModelProviderOptions options;

    public OpenAIChatModelProvider(HttpClient httpClient, OpenAIChatModelProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.httpClient = httpClient;
        this.options = options;
    }

    public string ProviderId => "openai-chat";

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
            response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                totalTimeout.Token).ConfigureAwait(false);
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

        using (response ?? throw new InvalidOperationException("The OpenAI request completed without a response."))
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
            var data = new StringBuilder();
            var created = false;
            var completed = false;
            string? responseId = null;
            string? finishReason = null;
            JsonElement? finalMetadata = null;

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
                    readError = new ModelProviderError(
                        firstEvent ? "first_event_timeout" : "stream_idle_timeout",
                        exception.Message,
                        true);
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

                if (line is null)
                {
                    break;
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

                if (payload == "[DONE]")
                {
                    completed = true;
                    break;
                }

                if (!TryParseJson(payload, out var document, out var parseError))
                {
                    yield return new ResponseFailed(parseError!);
                    yield break;
                }

                var parsedDocument = document!;
                using (parsedDocument)
                {
                    var root = parsedDocument.RootElement;
                    if (root.TryGetProperty("error", out var error))
                    {
                        yield return new ResponseFailed(ReadError(error, root));
                        yield break;
                    }

                    responseId ??= GetString(root, "id");
                    if (!created && responseId is not null)
                    {
                        created = true;
                        yield return new ResponseCreated(responseId);
                    }

                    if (root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var choice in choices.EnumerateArray())
                        {
                            if (choice.TryGetProperty("delta", out var delta))
                            {
                                var text = GetString(delta, "content");
                                if (!string.IsNullOrEmpty(text)) yield return new OutputTextDelta(text);
                                var refusal = GetString(delta, "refusal");
                                if (!string.IsNullOrEmpty(refusal)) yield return new OutputRefusalDelta(refusal);
                            }

                            finishReason ??= GetString(choice, "finish_reason");
                        }
                    }

                    var usage = ReadUsage(root);
                    if (usage is not null) yield return new UsageReported(usage);
                    finalMetadata = root.Clone();
                }
            }

            if (!completed)
            {
                yield return new ResponseFailed(new ModelProviderError(
                    "stream_ended_without_completion",
                    "The OpenAI Chat Completions stream ended before [DONE] was received.",
                    true));
                yield break;
            }

            yield return new ResponseCompleted(new ModelResponseMetadata(
                responseId ?? TryGetHeader(response, "x-request-id"),
                stopwatch.Elapsed,
                finishReason,
                finalMetadata));
        }
    }

    private HttpRequestMessage CreateHttpRequest(ModelRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new StringContent(BuildRequestBody(request).ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private JsonObject BuildRequestBody(ModelRequest request)
    {
        var messages = new JsonArray();
        foreach (var modelMessage in request.Messages)
        {
            var content = new JsonArray();
            foreach (var part in modelMessage.Content) content.Add(MapContent(part));
            messages.Add(new JsonObject { ["role"] = MapRole(modelMessage.Role), ["content"] = content });
        }

        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (request.OutputFormat is JsonSchemaOutputFormat schema)
        {
            root["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = schema.Name,
                    ["strict"] = schema.Strict,
                    ["schema"] = JsonNode.Parse(schema.Schema.GetRawText())
                }
            };
        }
        if (request.Options.MaxOutputTokens is { } maxTokens) root["max_completion_tokens"] = maxTokens;
        if (!string.IsNullOrWhiteSpace(request.Options.ReasoningEffort)) root["reasoning_effort"] = request.Options.ReasoningEffort;
        if (request.Options.PromptCacheKey is { } cacheKey) root["prompt_cache_key"] = cacheKey;
        if (request.Options.PromptCacheRetention is { } retention)
        {
            root["prompt_cache_retention"] = retention == PromptCacheRetention.TwentyFourHours ? "24h" : "in_memory";
        }
        if (options.SendRequestMetadata && request.Metadata is { Count: > 0 })
        {
            var metadata = new JsonObject();
            foreach (var pair in request.Metadata) metadata[pair.Key] = pair.Value;
            root["metadata"] = metadata;
        }
        return root;
    }

    private static JsonObject MapContent(ModelContent content) => content switch
    {
        TextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ImageContent image => ImagePart($"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}", image.Detail),
        ImageUriContent image => ImagePart(image.Uri.AbsoluteUri, image.Detail),
        _ => throw new NotSupportedException($"Unsupported model content type: {content.GetType().FullName}")
    };

    private static JsonObject ImagePart(string url, ImageDetailLevel detail) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = url, ["detail"] = MapDetail(detail) }
    };

    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return null;
        var input = GetInt64(usage, "prompt_tokens") ?? 0;
        var output = GetInt64(usage, "completion_tokens") ?? 0;
        return new ModelUsage(
            input,
            output,
            GetInt64(usage, "total_tokens") ?? input + output,
            GetNestedInt64(usage, "completion_tokens_details", "reasoning_tokens"),
            GetNestedInt64(usage, "prompt_tokens_details", "cached_tokens"));
    }

    private static ModelProviderError CreateHttpError(HttpResponseMessage response, string body)
    {
        var code = "openai_chat_http_error";
        var message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "The OpenAI request failed." : body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                code = GetString(error, "code") ?? code;
                message = GetString(error, "message") ?? message;
            }
        }
        catch (JsonException) { }
        return new ModelProviderError(code, message, IsRetryable(response.StatusCode), (int)response.StatusCode, TryGetHeader(response, "x-request-id"));
    }

    private static ModelProviderError ReadError(JsonElement error, JsonElement metadata)
    {
        var code = GetString(error, "code") ?? "openai_chat_error";
        return new ModelProviderError(code, GetString(error, "message") ?? "The OpenAI request failed.", IsRetryable(code), ProviderMetadata: metadata.Clone());
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
        if (request.Options.PromptCacheKey is not null && string.IsNullOrWhiteSpace(request.Options.PromptCacheKey))
            throw new ArgumentException("PromptCacheKey cannot be empty or whitespace.", nameof(request));
    }

    private static string MapRole(ModelRole role) => role switch
    {
        ModelRole.System => "system",
        ModelRole.Developer => "developer",
        ModelRole.User => "user",
        ModelRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    private static string MapDetail(ImageDetailLevel detail) => detail switch
    {
        ImageDetailLevel.Auto => "auto",
        ImageDetailLevel.Low => "low",
        ImageDetailLevel.High => "high",
        ImageDetailLevel.Original => "original",
        _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, null)
    };

    private static bool IsRetryable(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static bool IsRetryable(string code) => code is "request_timeout" or "rate_limit_exceeded" or "server_error" or "internal_server_error" or "service_unavailable" or "server_is_overloaded";
    private static string? TryGetHeader(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? GetInt64(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
    private static long? GetNestedInt64(JsonElement element, string objectName, string propertyName) => element.TryGetProperty(objectName, out var nested) ? GetInt64(nested, propertyName) : null;
}
