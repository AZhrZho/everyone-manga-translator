using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public sealed record TranslationExecutionPlan(
    ExecutionMode ExecutionMode,
    string RunId,
    IReadOnlyList<ProjectPageSnapshot> Pages,
    int AnnotationCount,
    TranslationTaskSettingsResponse? Settings = null);

public sealed record TranslationPageExecutionContext(
    string JobId,
    string ProjectId,
    string RunId,
    int PageIndex,
    Func<string, string, CancellationToken, Task> ReportAsync);

public sealed record TranslationPageExecutionResult(
    string LayoutJson,
    string CleanAssetKey,
    string MaskAssetKey,
    string RenderedAssetKey,
    string ResponseSummary,
    string Detail,
    bool NeedsPriorityReview = false,
    string? CandidateMaskAssetKey = null);

public sealed record TranslationPagePipelineOutcome(
    string ResponseSummary,
    string Detail);

public sealed record TranslationBatchExecutionResult(
    int PageIndex,
    TranslationPageExecutionResult Result);

public interface ITranslationExecutionSource
{
    ExecutionMode Mode { get; }

    Task<TranslationExecutionPlan> CreateAsync(
        StartTranslationRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse settings,
        CancellationToken cancellationToken) => ExecuteIndividuallyAsync(contexts, cancellationToken);

    Task<TranslationPageExecutionResult> ExecutePageAsync(
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException(
        "The translation source must implement batch or page translation execution.");

    private async Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteIndividuallyAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        CancellationToken cancellationToken)
    {
        var results = new List<TranslationBatchExecutionResult>(contexts.Count);
        foreach (var context in contexts)
        {
            results.Add(new TranslationBatchExecutionResult(
                context.PageIndex,
                await ExecutePageAsync(context, cancellationToken)));
        }
        return results;
    }
}

public interface ITranslationPageRunner
{
    Task<TranslationPagePipelineOutcome> RunAsync(
        TranslationPageExecutionContext context,
        TranslationPageExecutionResult result,
        CancellationToken cancellationToken) => Task.FromResult(
        new TranslationPagePipelineOutcome(result.ResponseSummary, result.Detail));

    Task<TranslationPagePipelineOutcome> RunAsync(
        ITranslationExecutionSource source,
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken) => RunLegacyAsync(source, context, cancellationToken);

    private async Task<TranslationPagePipelineOutcome> RunLegacyAsync(
        ITranslationExecutionSource source,
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken) => await RunAsync(
            context,
            await source.ExecutePageAsync(context, cancellationToken),
            cancellationToken);
}
