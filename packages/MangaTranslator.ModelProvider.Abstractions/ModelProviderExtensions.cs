using System.Text;
using System.Text.Json;

namespace MangaTranslator.ModelProvider.Abstractions;

public static class ModelProviderExtensions
{
    public static async Task<ModelResponse> GenerateAsync(
        this IModelProvider provider,
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var text = new StringBuilder();
        var refusal = new StringBuilder();
        ModelUsage? usage = null;
        ModelResponseMetadata? metadata = null;
        string? responseId = null;
        var status = ModelResponseStatus.Incomplete;
        ModelProviderError? failure = null;

        try
        {
            await foreach (var @event in provider.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                switch (@event)
                {
                    case ResponseCreated created:
                        responseId = created.ResponseId;
                        break;
                    case OutputTextDelta delta:
                        text.Append(delta.Delta);
                        break;
                    case OutputRefusalDelta delta:
                        refusal.Append(delta.Delta);
                        break;
                    case UsageReported reported:
                        usage = reported.Usage;
                        break;
                    case ResponseCompleted completed:
                        metadata = completed.Metadata with
                        {
                            ProviderRequestId = completed.Metadata.ProviderRequestId ?? responseId
                        };
                        status = refusal.Length > 0
                            ? ModelResponseStatus.Refused
                            : ModelResponseStatus.Completed;
                        break;
                    case ResponseFailed failed:
                        failure = failed.Error;
                        status = ModelResponseStatus.Incomplete;
                        break;
                    case ResponseCancelled:
                        status = ModelResponseStatus.Cancelled;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ModelResponseStatus.Cancelled;
        }

        if (failure is not null)
        {
            throw new ModelProviderException(failure);
        }

        metadata ??= new ModelResponseMetadata(responseId, TimeSpan.Zero);

        JsonElement? json = null;
        if (status == ModelResponseStatus.Completed && request.OutputFormat is JsonSchemaOutputFormat)
        {
            using var document = JsonDocument.Parse(text.ToString());
            json = document.RootElement.Clone();
        }

        return new ModelResponse(
            provider.ProviderId,
            request.Model,
            status,
            text.ToString(),
            json,
            usage,
            metadata,
            refusal.Length == 0 ? null : refusal.ToString());
    }
}
