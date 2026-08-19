namespace MangaTranslator.ProjectSystem;

public enum PersistentJobStatus
{
    Pending,
    Running,
    StopRequested,
    Stopped,
    Completed,
    Failed
}

public enum PersistentProcessEventStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed record PersistentProcessEvent(
    string EventId,
    string Stage,
    string Title,
    string RequestSummary,
    string? ResponseSummary,
    string? Detail,
    PersistentProcessEventStatus Status,
    string? ParallelGroup,
    IReadOnlyList<int> PageIndices,
    DateTimeOffset UpdatedAt);

public sealed record PersistentJobSnapshot(
    string JobId,
    string ProjectId,
    string Kind,
    PersistentJobStatus Status,
    string ExecutionMode,
    string? FixtureRunId,
    int ProgressPercent,
    IReadOnlyList<PersistentProcessEvent> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SettingsJson = null);

public interface IJobStore
{
    Task SaveAsync(PersistentJobSnapshot snapshot, CancellationToken cancellationToken);

    Task<PersistentJobSnapshot?> FindAsync(
        string jobId,
        CancellationToken cancellationToken) =>
        Task.FromResult<PersistentJobSnapshot?>(null);

    Task<PersistentJobSnapshot?> GetAsync(
        string projectId,
        string jobId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistentJobSnapshot>> ListForProjectAsync(
        string projectId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistentJobSnapshot>> ListActiveAsync(CancellationToken cancellationToken);

    Task<int> DeleteFinishedForProjectAsync(
        string projectId,
        CancellationToken cancellationToken) => Task.FromResult(0);
}
