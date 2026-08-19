using MangaTranslator.Api.Fixtures;

namespace MangaTranslator.Api.Pipeline;

public sealed record PipelineArtifactDescriptor(
    string RunId,
    string BookId,
    string RootPath,
    string CleaningPagesPath,
    PrecheckFixtureDescriptor PrecheckFixture,
    IReadOnlyList<PipelinePageArtifact> Pages,
    int SourcePageCount,
    DateTimeOffset CompletedAt);

public sealed record PipelinePageArtifact(
    string PipelinePageId,
    int PageIndex,
    string SourceFileName,
    string DialogueJsonPath,
    string MaskImagePath,
    string CleanImagePath,
    string? DetectionJsonPath,
    string? SourceContentHash,
    string? DetectionContentHash,
    string? MaskContentHash,
    string? CleanContentHash)
{
    public bool HasCompleteContentIdentity =>
        !string.IsNullOrWhiteSpace(SourceContentHash)
        && !string.IsNullOrWhiteSpace(DetectionContentHash)
        && !string.IsNullOrWhiteSpace(MaskContentHash)
        && !string.IsNullOrWhiteSpace(CleanContentHash);
}

public interface IPipelineArtifactCatalog
{
    IReadOnlyList<PipelineArtifactDescriptor> List();

    bool TryGet(string runIdOrBookId, out PipelineArtifactDescriptor? descriptor);

    PipelineArtifactDescriptor? MatchProject(ProjectSystem.ProjectSnapshot project);
}
