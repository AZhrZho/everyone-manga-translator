using MangaTranslator.Contracts;
using ProjectModel = MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Projects;

internal static class ProjectContractMapper
{
    public static ProjectListItemResponse ToResponse(this ProjectModel.ProjectListItem project) => new(
        project.ProjectId,
        project.Name,
        Enum.Parse<ProjectSourceKind>(project.SourceKind.ToString()),
        project.PageCount,
        project.LocationLabel,
        project.IsAvailable,
        project.CreatedAt,
        project.UpdatedAt,
        project.LastOpenedAt);

    public static ProjectDetailResponse ToResponse(this ProjectModel.ProjectSnapshot project) => new(
        project.ProjectId,
        project.Name,
        Enum.Parse<ProjectSourceKind>(project.SourceKind.ToString()),
        project.Revision,
        project.AutoSaveEnabled,
        Enum.Parse<ProjectWorkflowMode>(project.WorkflowMode.ToString()),
        project.TargetLanguage,
        project.EnabledFontIds,
        project.DefaultFontId,
        project.FallbackFontId,
        project.LocationLabel,
        project.Pages.Select(page => new ProjectPageResponse(
            page.PageId,
            page.Index,
            page.FileName,
            page.Width,
            page.Height,
            page.PageType,
            page.IncludedInWorkflow,
            Enum.Parse<PageReviewTag>(page.ReviewTag.ToString()),
            $"/api/projects/{Uri.EscapeDataString(project.ProjectId)}/pages/{Uri.EscapeDataString(page.PageId)}/original",
            $"/api/projects/{Uri.EscapeDataString(project.ProjectId)}/pages/{Uri.EscapeDataString(page.PageId)}/clean",
            $"/api/projects/{Uri.EscapeDataString(project.ProjectId)}/pages/{Uri.EscapeDataString(page.PageId)}/result",
            page.HasAiResult,
            page.HasUnversionedUserChanges,
            page.Revision,
            page.CurrentVersionId,
            page.CurrentLetteringVersionId,
            null,
            page.VersionCount)).ToArray(),
        project.CreatedAt,
        project.UpdatedAt,
        project.PrecheckBatchSize,
        project.PrecheckBatchConcurrency);

    public static PageVersionResponse ToResponse(this ProjectModel.PageVersionSnapshot version) => new(
        version.VersionId,
        version.PageId,
        version.Sequence,
        version.Label,
        Enum.Parse<PageVersionKind>(version.Kind.ToString()),
        Enum.Parse<PageVersionTrack>(version.Track.ToString()),
        version.ParentVersionId,
        version.IsCurrent,
        version.IsWorkingCopy,
        version.SourceAssetKey,
        version.CleanAssetKey,
        version.MaskAssetKey,
        version.RenderedAssetKey,
        version.LayoutJson,
        version.CreatedAt);

    public static PageLayoutResponse ToResponse(this ProjectModel.PageLayoutSnapshot layout) => new(
        layout.ProjectId,
        layout.PageId,
        layout.ProjectRevision,
        layout.PageRevision,
        layout.HasUnversionedUserChanges,
        layout.CurrentVersionId,
        layout.LayoutJson);

    public static PageImageEditResponse ToResponse(this ProjectModel.PageImageEditSnapshot imageEdit) => new(
        imageEdit.ProjectId,
        imageEdit.PageId,
        imageEdit.ProjectRevision,
        imageEdit.PageRevision,
        imageEdit.HasUnversionedUserChanges,
        imageEdit.CurrentVersionId,
        imageEdit.OperationsJson,
        $"/api/projects/{Uri.EscapeDataString(imageEdit.ProjectId)}/pages/{Uri.EscapeDataString(imageEdit.PageId)}/clean");
}
