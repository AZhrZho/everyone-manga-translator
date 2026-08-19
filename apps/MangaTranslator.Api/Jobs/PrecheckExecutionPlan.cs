using MangaTranslator.Contracts;

namespace MangaTranslator.Api.Jobs;

public sealed record PrecheckExecutionPlan(
    ExecutionMode ExecutionMode,
    string? FixtureRunId,
    IReadOnlyList<PrecheckExecutionEvent> Events,
    bool AlreadyCompleted = false);

public sealed record PrecheckExecutionEvent(
    string EventId,
    string Stage,
    string Title,
    string RequestSummary,
    string CompletionResponse,
    string CompletionDetail,
    string? ParallelGroup,
    IReadOnlyList<int> PageIndices,
    ProcessEventStatus InitialStatus = ProcessEventStatus.Pending);

public sealed record PrecheckExecutionContext(
    string JobId,
    string ProjectId,
    string RunId,
    Func<PrecheckExecutionEvent, CancellationToken, Task> ReportAsync);

public interface IPrecheckExecutionSource
{
    ExecutionMode Mode { get; }

    bool DrivesExecution { get; }

    Task<PrecheckExecutionPlan> CreateAsync(
        string projectId,
        string? fixtureRunId,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        PrecheckExecutionContext context,
        CancellationToken cancellationToken);
}
