using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.ModelProvider.OpenAI;

public sealed class OpenAIModelProvider : IModelProvider
{
    private static readonly ModelProviderCapabilities ProviderCapabilities = new(
        SupportsVision: true,
        SupportsMultipleImages: true,
        SupportsStructuredOutputs: true,
        SupportsStreaming: true,
        SupportsOriginalImageDetail: true,
        SupportsReasoningConfiguration: true);

    private readonly HttpClient _httpClient;
    private readonly OpenAIModelProviderOptions _options;

    public OpenAIModelProvider(HttpClient httpClient, OpenAIModelProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        _httpClient = httpClient;
        _options = options;
    }

    public string ProviderId => "openai";

    public ModelProviderCapabilities Capabilities => ProviderCapabilities;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        using var totalTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeoutSource.CancelAfter(
            request.Options.TotalTimeout ?? _options.Timeouts.DefaultTotalTimeout);

        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage? httpResponse = null;
        ModelProviderError? startupError = null;

        try
        {
            using var httpRequest = CreateHttpRequest(request);
            httpResponse = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                totalTimeoutSource.Token).ConfigureAwait(false);

            if (httpResponse.StatusCode == HttpStatusCode.BadRequest
                && request.Options.PromptCacheRetention is not null)
            {
                var errorBody = await httpResponse.Content
                    .ReadAsStringAsync(totalTimeoutSource.Token)
                    .ConfigureAwait(false);
                if (IsUnsupportedPromptCacheRetention(errorBody))
                {
                    httpResponse.Dispose();
                    using var compatibilityRequest = CreateHttpRequest(
                        request,
                        includePromptCacheRetention: false);
                    httpResponse = await _httpClient.SendAsync(
                        compatibilityRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        totalTimeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startupError = new ModelProviderError(
                "cancelled",
                "The OpenAI request was cancelled.",
                IsRetryable: false);
        }
        catch (OperationCanceledException exception)
        {
            startupError = CreateTransportError("request_timeout", exception.Message, true);
        }
        catch (HttpRequestException exception)
        {
            startupError = CreateTransportError("http_request_failed", exception.Message, true);
        }

        if (startupError is not null)
        {
            httpResponse?.Dispose();
            if (startupError.Code == "cancelled")
            {
                yield return new ResponseCancelled();
            }
            else
            {
                yield return new ResponseFailed(startupError);
            }

            yield break;
        }

        var response = httpResponse ?? throw new InvalidOperationException(
            "The OpenAI request completed without an HTTP response.");

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content
                    .ReadAsStringAsync(totalTimeoutSource.Token)
                    .ConfigureAwait(false);

                yield return new ResponseFailed(CreateHttpError(response, errorBody));
                yield break;
            }

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(totalTimeoutSource.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);

            var firstEvent = true;
            var completed = false;
            var eventName = string.Empty;
            var data = new StringBuilder();
            ModelProviderError? streamError = null;
            var streamErrorEmitted = false;

            while (true)
            {
                string? line = null;
                ModelProviderError? readError = null;
                var cancelled = false;
                try
                {
                    var timeout = firstEvent
                        ? request.Options.TimeToFirstEventTimeout
                            ?? _options.Timeouts.DefaultTimeToFirstEventTimeout
                        : request.Options.IdleEventTimeout
                            ?? _options.Timeouts.DefaultIdleEventTimeout;

                    line = await reader.ReadLineAsync(totalTimeoutSource.Token)
                        .AsTask()
                        .WaitAsync(timeout, totalTimeoutSource.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (OperationCanceledException exception)
                {
                    readError = CreateTransportError(
                        "stream_total_timeout",
                        exception.Message,
                        true);
                }
                catch (TimeoutException exception)
                {
                    readError = CreateTransportError(
                        firstEvent ? "first_event_timeout" : "stream_idle_timeout",
                        exception.Message,
                        true);
                }
                catch (IOException exception)
                {
                    readError = CreateTransportError(
                        "stream_interrupted",
                        exception.Message,
                        true);
                }

                if (cancelled)
                {
                    yield return new ResponseCancelled();
                    yield break;
                }

                if (readError is not null)
                {
                    streamError = readError;
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line["event:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data:".Length..].TrimStart());
                    continue;
                }

                if (line.Length != 0)
                {
                    continue;
                }

                if (data.Length == 0)
                {
                    eventName = string.Empty;
                    continue;
                }

                firstEvent = false;
                var payload = data.ToString();
                data.Clear();

                foreach (var mappedEvent in MapEvent(eventName, payload, stopwatch.Elapsed))
                {
                    if (mappedEvent is ResponseCompleted)
                    {
                        completed = true;
                    }

                    if (mappedEvent is ResponseFailed failed)
                    {
                        streamError = failed.Error;
                        streamErrorEmitted = true;
                    }

                    yield return mappedEvent;
                }

                eventName = string.Empty;

                if (completed || streamError is not null)
                {
                    break;
                }
            }

            if (streamError is not null)
            {
                if (!completed && !streamErrorEmitted)
                {
                    yield return new ResponseFailed(streamError);
                }

                yield break;
            }

            if (!completed)
            {
                yield return new ResponseFailed(CreateTransportError(
                    "stream_ended_without_completion",
                    "The OpenAI stream ended before response.completed was received.",
                    true));
            }
        }
    }

    private HttpRequestMessage CreateHttpRequest(
        ModelRequest request,
        bool includePromptCacheRetention = true)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_options.OrganizationId))
        {
            message.Headers.TryAddWithoutValidation("OpenAI-Organization", _options.OrganizationId);
        }

        if (!string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            message.Headers.TryAddWithoutValidation("OpenAI-Project", _options.ProjectId);
        }

        message.Content = new StringContent(
            BuildRequestBody(request, includePromptCacheRetention).ToJsonString(),
            Encoding.UTF8,
            "application/json");

        return message;
    }

    private JsonObject BuildRequestBody(
        ModelRequest request,
        bool includePromptCacheRetention = true)
    {
        var input = new JsonArray();
        foreach (var modelMessage in request.Messages)
        {
            var content = new JsonArray();
            foreach (var part in modelMessage.Content)
            {
                content.Add(MapContent(modelMessage.Role, part));
            }

            input.Add(new JsonObject
            {
                ["role"] = MapRole(modelMessage.Role),
                ["content"] = content
            });
        }

        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["stream"] = true
        };

        if (request.OutputFormat is JsonSchemaOutputFormat jsonSchema)
        {
            root["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = jsonSchema.Name,
                    ["strict"] = jsonSchema.Strict,
                    ["schema"] = JsonNode.Parse(jsonSchema.Schema.GetRawText())
                }
            };
        }

        if (request.Options.MaxOutputTokens is { } maxOutputTokens)
        {
            root["max_output_tokens"] = maxOutputTokens;
        }

        if (!string.IsNullOrWhiteSpace(request.Options.ReasoningEffort))
        {
            root["reasoning"] = new JsonObject
            {
                ["effort"] = request.Options.ReasoningEffort
            };
        }

        if (request.Options.PromptCacheKey is { } promptCacheKey)
        {
            root["prompt_cache_key"] = promptCacheKey;
        }

        if (includePromptCacheRetention
            && request.Options.PromptCacheRetention is { } promptCacheRetention)
        {
            root["prompt_cache_retention"] = MapPromptCacheRetention(
                promptCacheRetention);
        }

        if (_options.SendRequestMetadata && request.Metadata is { Count: > 0 })
        {
            var metadata = new JsonObject();
            foreach (var item in request.Metadata)
            {
                metadata[item.Key] = item.Value;
            }

            root["metadata"] = metadata;
        }

        return root;
    }

    private static bool IsUnsupportedPromptCacheRetention(string errorBody) =>
        errorBody.Contains("prompt_cache_retention", StringComparison.OrdinalIgnoreCase)
        && errorBody.Contains("unsupported", StringComparison.OrdinalIgnoreCase);

    private static JsonObject MapContent(ModelRole role, ModelContent content)
    {
        return content switch
        {
            TextContent text => new JsonObject
            {
                ["type"] = role == ModelRole.Assistant ? "output_text" : "input_text",
                ["text"] = text.Text
            },
            ImageContent image => new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}",
                ["detail"] = MapDetail(image.Detail)
            },
            ImageUriContent image => new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = image.Uri.AbsoluteUri,
                ["detail"] = MapDetail(image.Detail)
            },
            _ => throw new NotSupportedException(
                $"Unsupported model content type: {content.GetType().FullName}")
        };
    }

    private static List<ModelStreamEvent> MapEvent(
        string eventName,
        string payload,
        TimeSpan duration)
    {
        if (payload == "[DONE]")
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            return [new ResponseFailed(new ModelProviderError(
                "invalid_stream_json",
                exception.Message,
                IsRetryable: true))];
        }

        var events = new List<ModelStreamEvent>();
        using (document)
        {
            var root = document.RootElement;
            var resolvedEventName = string.IsNullOrWhiteSpace(eventName)
                ? GetString(root, "type") ?? string.Empty
                : eventName;

            switch (resolvedEventName)
            {
                case "response.created":
                    events.Add(new ResponseCreated(
                        GetNestedString(root, "response", "id")
                        ?? GetString(root, "response_id")
                        ?? string.Empty));
                    break;

                case "response.output_text.delta":
                    events.Add(new OutputTextDelta(GetString(root, "delta") ?? string.Empty));
                    break;

                case "response.refusal.delta":
                    events.Add(new OutputRefusalDelta(GetString(root, "delta") ?? string.Empty));
                    break;

                case "response.output_item.done":
                    events.Add(new OutputItemCompleted(
                        GetNestedString(root, "item", "type") ?? "unknown"));
                    break;

                case "response.completed":
                    var responseElement = TryGetProperty(root, "response", out var response)
                        ? response
                        : root;
                    var usage = ReadUsage(responseElement);
                    if (usage is not null)
                    {
                        events.Add(new UsageReported(usage));
                    }

                    events.Add(new ResponseCompleted(new ModelResponseMetadata(
                        GetString(responseElement, "id"),
                        duration,
                        GetString(responseElement, "status"),
                        responseElement.Clone())));
                    break;

                case "response.failed":
                case "error":
                    events.Add(new ResponseFailed(ReadProviderError(root)));
                    break;
            }
        }

        return events;
    }

    private static ModelUsage? ReadUsage(JsonElement response)
    {
        if (!TryGetProperty(response, "usage", out var usage))
        {
            return null;
        }

        var inputTokens = GetInt64(usage, "input_tokens") ?? 0;
        var outputTokens = GetInt64(usage, "output_tokens") ?? 0;
        var totalTokens = GetInt64(usage, "total_tokens") ?? inputTokens + outputTokens;
        var reasoningTokens = GetNestedInt64(usage, "output_tokens_details", "reasoning_tokens");
        var cachedTokens = GetNestedInt64(usage, "input_tokens_details", "cached_tokens");

        return new ModelUsage(
            inputTokens,
            outputTokens,
            totalTokens,
            reasoningTokens,
            cachedTokens);
    }

    private static ModelProviderError ReadProviderError(JsonElement root)
    {
        var error = TryGetProperty(root, "error", out var rootError)
            ? rootError
            : TryGetProperty(root, "response", out var response)
                && TryGetProperty(response, "error", out var responseError)
                    ? responseError
                    : root;

        return new ModelProviderError(
            GetString(error, "code") ?? "openai_error",
            GetString(error, "message") ?? "The OpenAI response failed.",
            IsRetryable: IsRetryableProviderErrorCode(
                GetString(error, "code")),
            ProviderRequestId: GetString(root, "response_id"),
            ProviderMetadata: root.Clone());
    }

    private static bool IsRetryableProviderErrorCode(string? code) =>
        code is "request_timeout"
            or "rate_limit_exceeded"
            or "server_error"
            or "internal_server_error"
            or "service_unavailable"
            or "temporarily_unavailable"
            or "overloaded"
            or "server_is_overloaded"
            or "upstream_stream_aborted"
            or "stream_aborted"
            or "upstream_timeout"
            or "connection_reset";

    private static ModelProviderError CreateHttpError(
        HttpResponseMessage response,
        string body)
    {
        string code = "openai_http_error";
        string message = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? "The OpenAI request failed."
            : body;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryGetProperty(document.RootElement, "error", out var error))
            {
                code = GetString(error, "code") ?? code;
                message = GetString(error, "message") ?? message;
            }
        }
        catch (JsonException)
        {
            // Preserve the response body when the server did not return JSON.
        }

        var statusCode = (int)response.StatusCode;
        return new ModelProviderError(
            code,
            message,
            IsRetryableStatusCode(response.StatusCode),
            statusCode,
            TryGetHeader(response, "x-request-id"));
    }

    private static ModelProviderError CreateTransportError(
        string code,
        string message,
        bool retryable)
    {
        return new ModelProviderError(code, message, retryable);
    }

    private static void ValidateRequest(ModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("A model name is required.", nameof(request));
        }

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one model message is required.", nameof(request));
        }

        if (request.Messages.Any(message => message.Content.Count == 0))
        {
            throw new ArgumentException("Model messages cannot have empty content.", nameof(request));
        }

        if (request.Options.PromptCacheKey is not null
            && string.IsNullOrWhiteSpace(request.Options.PromptCacheKey))
        {
            throw new ArgumentException(
                "PromptCacheKey cannot be empty or whitespace.",
                nameof(request));
        }
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

    private static string MapPromptCacheRetention(PromptCacheRetention retention) =>
        retention switch
        {
            PromptCacheRetention.InMemory => "in_memory",
            PromptCacheRetention.TwentyFourHours => "24h",
            _ => throw new ArgumentOutOfRangeException(nameof(retention), retention, null)
        };

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return TryGetProperty(element, objectName, out var nested)
            ? GetString(nested, propertyName)
            : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value)
            && value.TryGetInt64(out var result)
                ? result
                : null;
    }

    private static long? GetNestedInt64(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return TryGetProperty(element, objectName, out var nested)
            ? GetInt64(nested, propertyName)
            : null;
    }
}
