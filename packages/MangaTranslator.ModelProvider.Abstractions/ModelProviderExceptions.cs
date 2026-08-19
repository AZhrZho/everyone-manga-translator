namespace MangaTranslator.ModelProvider.Abstractions;

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(ModelProviderError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    public ModelProviderError Error { get; }
}
