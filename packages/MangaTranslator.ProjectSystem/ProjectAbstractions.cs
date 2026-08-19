namespace MangaTranslator.ProjectSystem;

public interface IProjectStore
{
    Task<IReadOnlyList<ProjectListItem>> ListRecentAsync(CancellationToken cancellationToken);

    Task RemoveRecentAsync(
        string projectId,
        bool deleteProjectFiles,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This project store does not support removing recent projects.");

    Task<ProjectSnapshot> GetAsync(string projectId, CancellationToken cancellationToken);

    Task<ProjectSnapshot> CreateAsync(
        CreateProjectCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> ImportFolderAsync(
        ImportFolderProjectCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> ImportPdfAsync(
        ImportPdfProjectCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> OpenAsync(
        OpenProjectCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> RenameAsync(
        string projectId,
        RenameProjectCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> UpdateSettingsAsync(
        string projectId,
        UpdateProjectSettingsCommand command,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This project store does not support project settings updates.");

    Task<ProjectSnapshot> UpdatePageReviewAsync(
        string projectId,
        string pageId,
        UpdatePageReviewCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> UpdatePageReviewTagAsync(
        string projectId,
        string pageId,
        UpdatePageReviewTagCommand command,
        CancellationToken cancellationToken);

    Task<PageLayoutSnapshot> GetPageLayoutAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> UpdatePageLayoutAsync(
        string projectId,
        string pageId,
        UpdatePageLayoutCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> RebasePageBackgroundAsync(
        string projectId,
        string pageId,
        RebasePageBackgroundCommand command,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This project store does not support layout-driven background rebasing.");

    Task<PageImageEditSnapshot> GetPageImageEditAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> UpdatePageImageEditAsync(
        string projectId,
        string pageId,
        UpdatePageImageEditCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PageVersionSnapshot>> ListPageVersionsAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> CreatePageVersionAsync(
        string projectId,
        string pageId,
        CreatePageVersionCommand command,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> SwitchPageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        CancellationToken cancellationToken);

    Task<ProjectSnapshot> SwitchPageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        string? cleanAssetKey,
        string? maskAssetKey,
        string? candidateMaskAssetKey,
        CancellationToken cancellationToken) => SwitchPageVersionAsync(
        projectId,
        pageId,
        versionId,
        expectedRevision,
        cancellationToken);

    Task<ProjectSnapshot> DeletePageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        CancellationToken cancellationToken);
}

public interface IProjectAssetStore
{
    Task<bool> ExistsAsync(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken);

    Task<ProjectAsset> OpenReadAsync(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken);
}

public interface IProjectAssetWriter
{
    Task SaveAsync(
        string projectId,
        string assetKey,
        Stream content,
        CancellationToken cancellationToken);
}

public interface IProjectResultService
{
    Task<ProjectAsset> RenderPageAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken);

    Task<ProjectAsset> RenderBackgroundAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken) => RenderPageAsync(projectId, pageId, cancellationToken);

    Task<ProjectAsset> RenderLetteringAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken) => RenderPageAsync(projectId, pageId, cancellationToken);

    Task<ProjectAsset> RenderPageAsync(
        string projectId,
        string pageId,
        string backgroundAssetKey,
        string? layoutJson,
        CancellationToken cancellationToken);
}

public interface IProjectLocationGrantRegistry
{
    ProjectLocationGrant Register(string path, ProjectLocationKind kind);
}
