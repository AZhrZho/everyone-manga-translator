using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.ModelProvider.Anthropic;
using MangaTranslator.ModelProvider.OpenAI;

namespace MangaTranslator.Api.Tests;

public sealed class ModelProviderFactoryTests
{
    [Fact]
    public void SelectsProviderFromConfiguredEndpointType()
    {
        using var client = new HttpClient();

        Assert.IsType<OpenAIModelProvider>(ModelProviderFactory.Create(
            client,
            Settings(ProviderEndpointType.OpenAIResponses, "responses")));
        Assert.IsType<OpenAIChatModelProvider>(ModelProviderFactory.Create(
            client,
            Settings(ProviderEndpointType.OpenAIChatCompletions, "chat/completions")));
        Assert.IsType<AnthropicModelProvider>(ModelProviderFactory.Create(
            client,
            Settings(ProviderEndpointType.AnthropicMessages, "messages")));
    }

    private static WorkflowModelSettings Settings(ProviderEndpointType endpointType, string suffix) =>
        new("translation", "provider", endpointType, new Uri($"https://example.test/v1/{suffix}"), "secret", "model");
}
