using System.Text.Json;

namespace MangaTranslator.ModelProvider.Abstractions;

public sealed record ModelRequest
{
    public ModelRequest(
        string model,
        IReadOnlyList<ModelMessage> messages,
        ModelOutputFormat? outputFormat = null,
        ModelGenerationOptions? options = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model name is required.", nameof(model));
        }

        ArgumentNullException.ThrowIfNull(messages);
        Model = model;
        Messages = messages;
        OutputFormat = outputFormat ?? ModelOutputFormat.Text;
        Options = options ?? new ModelGenerationOptions();
        Metadata = metadata;
    }

    public string Model { get; }

    public IReadOnlyList<ModelMessage> Messages { get; }

    public ModelOutputFormat OutputFormat { get; }

    public ModelGenerationOptions Options { get; }

    public IReadOnlyDictionary<string, string>? Metadata { get; }
}

public sealed record ModelMessage(
    ModelRole Role,
    IReadOnlyList<ModelContent> Content)
{
    public ModelMessage(ModelRole role, string text)
        : this(role, [new TextContent(text)])
    {
    }
}

public enum ModelRole
{
    System,
    Developer,
    User,
    Assistant
}

public abstract record ModelContent;

public sealed record TextContent(string Text) : ModelContent;

public sealed record ImageContent(
    ReadOnlyMemory<byte> Data,
    string MediaType,
    ImageDetailLevel Detail = ImageDetailLevel.Auto) : ModelContent;

public sealed record ImageUriContent(
    Uri Uri,
    ImageDetailLevel Detail = ImageDetailLevel.Auto) : ModelContent;

public abstract record ModelOutputFormat
{
    public static ModelOutputFormat Text { get; } = new TextOutputFormat();
}

public sealed record TextOutputFormat : ModelOutputFormat;

public sealed record JsonSchemaOutputFormat : ModelOutputFormat
{
    public JsonSchemaOutputFormat(string name, JsonElement schema, bool strict = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A schema name is required.", nameof(name));
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The JSON schema must be an object.", nameof(schema));
        }

        Name = name;
        Schema = schema.Clone();
        Strict = strict;
    }

    public string Name { get; }

    public JsonElement Schema { get; }

    public bool Strict { get; }
}

public sealed record ModelGenerationOptions
{
    public int? MaxOutputTokens { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Stable caller-defined prefix identity used by providers that support prompt caching.
    /// Null leaves provider-side cache routing unspecified.
    /// </summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>
    /// Optional retention policy for a provider-managed prompt-cache entry.
    /// </summary>
    public PromptCacheRetention? PromptCacheRetention { get; init; }

    public TimeSpan? TotalTimeout { get; init; }

    public TimeSpan? TimeToFirstEventTimeout { get; init; }

    public TimeSpan? IdleEventTimeout { get; init; }
}

public enum PromptCacheRetention
{
    InMemory,
    TwentyFourHours
}

public enum ImageDetailLevel
{
    Auto,
    Low,
    High,
    Original
}

public sealed record ModelProviderCapabilities(
    bool SupportsVision,
    bool SupportsMultipleImages,
    bool SupportsStructuredOutputs,
    bool SupportsStreaming,
    bool SupportsOriginalImageDetail,
    bool SupportsReasoningConfiguration);

public sealed record ModelUsage(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long? ReasoningTokens = null,
    long? CachedInputTokens = null);

public sealed record ModelResponseMetadata(
    string? ProviderRequestId,
    TimeSpan Duration,
    string? FinishReason = null,
    JsonElement? ProviderMetadata = null);

public enum ModelResponseStatus
{
    Completed,
    Refused,
    Incomplete,
    Cancelled
}

public sealed record ModelResponse(
    string ProviderId,
    string Model,
    ModelResponseStatus Status,
    string? Text,
    JsonElement? Json,
    ModelUsage? Usage,
    ModelResponseMetadata Metadata,
    string? Refusal = null);

public abstract record ModelStreamEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ResponseCreated(string ResponseId) : ModelStreamEvent;

public sealed record OutputTextDelta(string Delta) : ModelStreamEvent;

public sealed record OutputRefusalDelta(string Delta) : ModelStreamEvent;

public sealed record OutputItemCompleted(string ItemType) : ModelStreamEvent;

public sealed record UsageReported(ModelUsage Usage) : ModelStreamEvent;

public sealed record ResponseCompleted(ModelResponseMetadata Metadata) : ModelStreamEvent;

public sealed record ResponseFailed(ModelProviderError Error) : ModelStreamEvent;

public sealed record ResponseCancelled : ModelStreamEvent;

public sealed record ModelProviderError(
    string Code,
    string Message,
    bool IsRetryable,
    int? HttpStatusCode = null,
    string? ProviderRequestId = null,
    JsonElement? ProviderMetadata = null);

public sealed record ModelProviderOptions
{
    public TimeSpan DefaultTotalTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan DefaultTimeToFirstEventTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan DefaultIdleEventTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
