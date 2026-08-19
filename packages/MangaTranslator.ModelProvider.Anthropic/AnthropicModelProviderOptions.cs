using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.ModelProvider.Anthropic;

public sealed record AnthropicModelProviderOptions
{
    public required string ApiKey { get; init; }

    public Uri Endpoint { get; init; } = new("https://api.anthropic.com/v1/messages");

    public string ApiVersion { get; init; } = "2023-06-01";

    public ModelProviderOptions Timeouts { get; init; } = new();

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("An Anthropic API key is required.", nameof(ApiKey));
        }

        if (!Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The Anthropic endpoint must be an absolute URI.", nameof(Endpoint));
        }

        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            throw new ArgumentException("An Anthropic API version is required.", nameof(ApiVersion));
        }
    }
}
