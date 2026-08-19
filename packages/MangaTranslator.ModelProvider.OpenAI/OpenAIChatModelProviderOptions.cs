using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.ModelProvider.OpenAI;

public sealed record OpenAIChatModelProviderOptions
{
    public required string ApiKey { get; init; }

    public Uri Endpoint { get; init; } = new("https://api.openai.com/v1/chat/completions");

    public bool SendRequestMetadata { get; init; } = true;

    public ModelProviderOptions Timeouts { get; init; } = new();

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("An OpenAI API key is required.", nameof(ApiKey));
        }

        if (!Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The OpenAI endpoint must be an absolute URI.", nameof(Endpoint));
        }
    }
}
