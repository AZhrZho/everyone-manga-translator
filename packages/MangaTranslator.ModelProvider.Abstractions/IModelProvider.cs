namespace MangaTranslator.ModelProvider.Abstractions;

public interface IModelProvider
{
    string ProviderId { get; }

    ModelProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
