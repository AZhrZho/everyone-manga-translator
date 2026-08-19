namespace MangaTranslator.ProjectSystem;

public sealed record ProjectArtifact(
    string Name,
    string Json,
    DateTimeOffset UpdatedAt);

public interface IProjectArtifactStore
{
    Task SaveAsync(
        string projectId,
        string name,
        string json,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);

    Task<int> UpdateAsync(
        string projectId,
        string name,
        string json,
        int expectedProjectRevision,
        CancellationToken cancellationToken);
}
