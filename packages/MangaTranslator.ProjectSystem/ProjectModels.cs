namespace MangaTranslator.ProjectSystem;

public enum ProjectSourceKind
{
    Empty,
    Folder,
    Pdf
}

public enum ProjectWorkflowMode
{
    Expert,
    Quick
}

public enum ProjectLocationKind
{
    ProjectParent,
    ExistingProject,
    ImportFolder,
    ImportPdf,
    ImportFont,
    ExportFolder,
    ExportZip,
    ExportPdf
}

public enum PageVersionKind
{
    Imported,
    Ai,
    User
}

public enum PageVersionTrack
{
    Lettering,
    Background
}

public enum PageReviewTag
{
    Unreviewed,
    Priority,
    Accepted,
    Rejected
}

public static class ProjectPrecheckDefaults
{
    public const int BatchSize = 12;
    public const int BatchConcurrency = 4;
    public const int MinimumBatchSize = 1;
    public const int MaximumBatchSize = 24;
    public const int MinimumBatchConcurrency = 1;
    public const int MaximumBatchConcurrency = 8;
}

public sealed record ProjectListItem(
    string ProjectId,
    string Name,
    ProjectSourceKind SourceKind,
    int PageCount,
    string LocationLabel,
    bool IsAvailable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastOpenedAt);

public sealed record ProjectSnapshot(
    string ProjectId,
    string Name,
    ProjectSourceKind SourceKind,
    int Revision,
    bool AutoSaveEnabled,
    ProjectWorkflowMode WorkflowMode,
    string TargetLanguage,
    IReadOnlyList<string> EnabledFontIds,
    string? DefaultFontId,
    string? FallbackFontId,
    string LocationLabel,
    IReadOnlyList<ProjectPageSnapshot> Pages,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PrecheckBatchSize = ProjectPrecheckDefaults.BatchSize,
    int PrecheckBatchConcurrency = ProjectPrecheckDefaults.BatchConcurrency)
{
    public ProjectSnapshot(
        string projectId,
        string name,
        ProjectSourceKind sourceKind,
        int revision,
        bool autoSaveEnabled,
        string locationLabel,
        IReadOnlyList<ProjectPageSnapshot> pages,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        : this(
            projectId,
            name,
            sourceKind,
            revision,
            autoSaveEnabled,
            ProjectWorkflowMode.Expert,
            "zh-Hans",
            [],
            null,
            null,
            locationLabel,
            pages,
            createdAt,
            updatedAt,
            ProjectPrecheckDefaults.BatchSize,
            ProjectPrecheckDefaults.BatchConcurrency)
    {
    }
}

public sealed record ProjectPageSnapshot(
    string PageId,
    int Index,
    string FileName,
    int Width,
    int Height,
    string PageType,
    bool IncludedInWorkflow,
    PageReviewTag ReviewTag,
    string SourceAssetKey,
    bool HasAiResult,
    bool HasUnversionedUserChanges,
    int Revision,
    string? CurrentVersionId,
    string? CurrentLetteringVersionId,
    string? CurrentBackgroundVersionId,
    int VersionCount);

public sealed record PageVersionSnapshot(
    string VersionId,
    string PageId,
    int Sequence,
    string Label,
    PageVersionKind Kind,
    PageVersionTrack Track,
    string? ParentVersionId,
    bool IsCurrent,
    bool IsWorkingCopy,
    string SourceAssetKey,
    string? CleanAssetKey,
    string? MaskAssetKey,
    string? RenderedAssetKey,
    string? LayoutJson,
    DateTimeOffset CreatedAt);

public sealed record PageLayoutSnapshot(
    string ProjectId,
    string PageId,
    int ProjectRevision,
    int PageRevision,
    bool HasUnversionedUserChanges,
    string? CurrentVersionId,
    string? LayoutJson);

public sealed record PageImageEditSnapshot(
    string ProjectId,
    string PageId,
    int ProjectRevision,
    int PageRevision,
    bool HasUnversionedUserChanges,
    string? CurrentVersionId,
    string? OperationsJson,
    string DerivedCleanAssetKey,
    string RenderedAssetKey,
    string? EffectiveMaskAssetKey,
    string? CandidateMaskAssetKey);

public sealed record CreateProjectCommand(
    string Name,
    string DestinationToken,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record ImportFolderProjectCommand(
    string Name,
    string DestinationToken,
    string SourceToken,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record ImportPdfProjectCommand(
    string Name,
    string DestinationToken,
    string SourceToken,
    int RenderDpi = 200,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record OpenProjectCommand(string LocationToken);

public sealed record RenameProjectCommand(
    string Name,
    int ExpectedRevision);

public sealed record UpdateProjectSettingsCommand(
    ProjectWorkflowMode WorkflowMode,
    string TargetLanguage,
    IReadOnlyList<string> EnabledFontIds,
    string? DefaultFontId,
    int ExpectedRevision,
    string? FallbackFontId = null,
    int PrecheckBatchSize = ProjectPrecheckDefaults.BatchSize,
    int PrecheckBatchConcurrency = ProjectPrecheckDefaults.BatchConcurrency);

public sealed record UpdatePageReviewCommand(
    string PageType,
    bool IncludedInWorkflow,
    int ExpectedRevision);

public sealed record UpdatePageReviewTagCommand(
    PageReviewTag ReviewTag,
    int ExpectedRevision);

public sealed record CreatePageVersionCommand(
    string Label,
    int ExpectedRevision,
    PageVersionKind Kind = PageVersionKind.User,
    string? LayoutJson = null,
    string? CleanAssetKey = null,
    string? MaskAssetKey = null,
    string? RenderedAssetKey = null,
    PageVersionTrack Track = PageVersionTrack.Lettering,
    bool Activate = true,
    string? CandidateMaskAssetKey = null);

public sealed record UpdatePageLayoutCommand(
    string LayoutJson,
    int ExpectedRevision,
    string? CleanAssetKey = null,
    string? MaskAssetKey = null,
    string? CandidateMaskAssetKey = null);

public sealed record RebasePageBackgroundCommand(
    string CleanAssetKey,
    string MaskAssetKey,
    int ExpectedRevision,
    string? CandidateMaskAssetKey = null);

public sealed record UpdatePageImageEditCommand(
    string OperationsJson,
    int ExpectedPageRevision);

public sealed record ProjectAsset(
    Stream Content,
    string MediaType,
    string FileName,
    long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ProjectLocationGrant(
    string Token,
    ProjectLocationKind Kind,
    string DisplayName,
    DateTimeOffset ExpiresAt);
