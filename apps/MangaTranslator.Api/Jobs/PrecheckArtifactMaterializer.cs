using MangaTranslator.Api.Fixtures;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public interface IPrecheckArtifactMaterializer
{
    Task MaterializeAsync(
        string projectId,
        string? runId,
        CancellationToken cancellationToken);
}

public sealed class PrecheckArtifactMaterializer(
    IProjectArtifactStore artifacts,
    IPrecheckFixtureCatalog fixtures,
    IPipelineArtifactCatalog pipelineArtifacts,
    PipelinePrecheckArtifactImporter pipelineImporter) : IPrecheckArtifactMaterializer
{
    public async Task MaterializeAsync(
        string projectId,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runId)
            && pipelineArtifacts.TryGet(runId, out var pipeline))
        {
            await pipelineImporter.ImportAsync(projectId, pipeline!, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(runId) || !fixtures.TryGet(runId, out var fixture))
        {
            return;
        }

        foreach (var path in PlatformIoPath.EnumerateFiles(
                     fixture!.RootPath,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await PlatformIoPath.ReadAllTextAsync(path, cancellationToken);
            await artifacts.SaveAsync(projectId, Path.GetFileName(path), json, cancellationToken);
        }
    }
}
