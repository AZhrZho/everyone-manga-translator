using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.ModelProvider.Anthropic;
using MangaTranslator.ModelProvider.OpenAI;

namespace MangaTranslator.Api.Jobs;

internal static class ModelProviderFactory
{
    public static IModelProvider Create(HttpClient httpClient, WorkflowModelSettings settings)
    {
        var timeouts = new ModelProviderOptions
        {
            DefaultTotalTimeout = TimeSpan.FromMinutes(20),
            DefaultTimeToFirstEventTimeout = TimeSpan.FromMinutes(3),
            DefaultIdleEventTimeout = TimeSpan.FromMinutes(3)
        };

        return settings.EndpointType switch
        {
            ProviderEndpointType.OpenAIResponses => new OpenAIModelProvider(
                httpClient,
                new OpenAIModelProviderOptions
                {
                    ApiKey = settings.ApiKey,
                    Endpoint = settings.Endpoint,
                    SendRequestMetadata = false,
                    Timeouts = timeouts
                }),
            ProviderEndpointType.OpenAIChatCompletions => new OpenAIChatModelProvider(
                httpClient,
                new OpenAIChatModelProviderOptions
                {
                    ApiKey = settings.ApiKey,
                    Endpoint = settings.Endpoint,
                    SendRequestMetadata = false,
                    Timeouts = timeouts
                }),
            ProviderEndpointType.AnthropicMessages => new AnthropicModelProvider(
                httpClient,
                new AnthropicModelProviderOptions
                {
                    ApiKey = settings.ApiKey,
                    Endpoint = settings.Endpoint,
                    Timeouts = timeouts
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.EndpointType, null)
        };
    }
}
