namespace MangaTranslator.Contracts;

public enum HostKind
{
    Desktop,
    Web
}

public enum ExecutionMode
{
    Cached,
    Replay,
    Live
}

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

public enum ProviderEndpointType
{
    OpenAIResponses,
    OpenAIChatCompletions,
    AnthropicMessages
}

public enum LocalTextDetectionEngine
{
    Ctd,
    Craft,
    BrowserCraft
}

public enum TranslationInputResolution
{
    Original,
    Half,
    Quarter
}

public enum JobStatus
{
    Pending,
    Running,
    Stopped,
    Completed,
    Failed
}

public enum ProcessEventStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed record SystemInfoResponse(
    string ProductName,
    string ProductVersion,
    HostKind HostKind,
    string RuntimeVersion,
    string DataRoot);

public sealed record AppSettingsResponse(
    string Theme,
    string Locale,
    IReadOnlyList<ProviderSettingsResponse> Providers,
    IReadOnlyList<StageModelSelectionResponse> Stages,
    string? DefaultFontId,
    string? FallbackFontId,
    IReadOnlyList<FontLanguageProfileResponse> FontLanguageProfiles,
    IReadOnlyList<FontInfoResponse> Fonts,
    int TranslationBatchConcurrency,
    int TranslationBatchSize,
    LocalTextDetectionEngine LocalTextDetectionEngine = LocalTextDetectionEngine.Ctd,
    int PrecheckBatchConcurrency = 4,
    int PrecheckBatchSize = 12);

public sealed record ProviderSettingsResponse(
    string ProviderId,
    string Name,
    ProviderEndpointType EndpointType,
    string BaseUrl,
    bool ApiKeyConfigured,
    IReadOnlyList<ModelSettingsResponse> Models);

public sealed record ModelSettingsResponse(
    string ModelId,
    string DisplayName,
    bool SupportsImageInput);

public sealed record StageModelSelectionResponse(
    string StageId,
    string DisplayName,
    string RequiredModelType,
    string? ProviderId,
    string? ModelId);

public sealed record FontInfoResponse(
    string FontId,
    string DisplayName,
    string FileName,
    string Source,
    bool CanRemove);

public sealed record FontLanguageProfileResponse(
    string TargetLanguage,
    IReadOnlyList<string> AllowedFontIds,
    string? DefaultFontId);

public sealed record UpdateAppSettingsRequest(
    string Theme,
    IReadOnlyList<ProviderSettingsRequest> Providers,
    IReadOnlyList<StageModelSelectionRequest> Stages,
    string? DefaultFontId,
    string? FallbackFontId = null,
    string? Locale = null,
    IReadOnlyList<FontLanguageProfileRequest>? FontLanguageProfiles = null,
    int TranslationBatchConcurrency = 2,
    int TranslationBatchSize = 3,
    LocalTextDetectionEngine LocalTextDetectionEngine = LocalTextDetectionEngine.Ctd,
    int PrecheckBatchConcurrency = 4,
    int PrecheckBatchSize = 12);

public sealed record FontLanguageProfileRequest(
    string TargetLanguage,
    IReadOnlyList<string> AllowedFontIds,
    string? DefaultFontId);

public sealed record ProviderSettingsRequest(
    string ProviderId,
    string Name,
    ProviderEndpointType EndpointType,
    string BaseUrl,
    string? ApiKey,
    bool ClearApiKey,
    IReadOnlyList<ModelSettingsRequest> Models);

public sealed record ModelSettingsRequest(
    string ModelId,
    string DisplayName,
    bool SupportsImageInput);

public sealed record StageModelSelectionRequest(
    string StageId,
    string? ProviderId,
    string? ModelId);

public sealed record ImportFontsRequest(
    IReadOnlyList<string> SourceTokens,
    string? TargetLanguage = null);

public sealed record PageSummaryResponse(
    string PageId,
    int Index,
    string FileName,
    string PageType,
    bool IncludedInWorkflow);

public sealed record ProjectSummaryResponse(
    string ProjectId,
    string Name,
    int PageCount,
    IReadOnlyList<PageSummaryResponse> Pages);

public sealed record ProjectListItemResponse(
    string ProjectId,
    string Name,
    ProjectSourceKind SourceKind,
    int PageCount,
    string LocationLabel,
    bool IsAvailable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastOpenedAt);

public sealed record ProjectPageResponse(
    string PageId,
    int Index,
    string FileName,
    int Width,
    int Height,
    string PageType,
    bool IncludedInWorkflow,
    PageReviewTag ReviewTag,
    string OriginalAssetUrl,
    string CleanAssetUrl,
    string ResultAssetUrl,
    bool HasAiResult,
    bool HasUnversionedUserChanges,
    int Revision,
    string? CurrentVersionId,
    string? CurrentLetteringVersionId,
    string? CurrentBackgroundVersionId,
    int VersionCount);

public sealed record ProjectDetailResponse(
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
    IReadOnlyList<ProjectPageResponse> Pages,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PrecheckBatchSize = 12,
    int PrecheckBatchConcurrency = 4);

public sealed record CreateProjectRequest(
    string Name,
    string DestinationToken,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record ImportFolderProjectRequest(
    string Name,
    string DestinationToken,
    string SourceToken,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record ImportPdfProjectRequest(
    string Name,
    string DestinationToken,
    string SourceToken,
    int RenderDpi = 200,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null);

public sealed record OpenProjectRequest(string LocationToken);

public sealed record ExportProjectRequest(
    string? DestinationToken,
    ProjectExportFormat Format,
    bool IncludeBackground = true,
    bool IncludeLettering = true,
    ProjectExportResolution Resolution = ProjectExportResolution.Original);

public sealed record ExportProjectResponse(
    ProjectExportFormat Format,
    string DestinationPath,
    int PageCount);

public sealed record RegisterLocalPathRequest(
    string Path,
    ProjectLocationKind Kind);

public sealed record ProjectLocationGrantResponse(
    string Token,
    ProjectLocationKind Kind,
    string DisplayName,
    DateTimeOffset ExpiresAt);

public sealed record RenameProjectRequest(
    string Name,
    int ExpectedRevision);

public sealed record UpdateProjectSettingsRequest(
    ProjectWorkflowMode WorkflowMode,
    string TargetLanguage,
    IReadOnlyList<string> EnabledFontIds,
    string? DefaultFontId,
    int ExpectedRevision,
    string? FallbackFontId = null,
    int PrecheckBatchSize = 12,
    int PrecheckBatchConcurrency = 4);

public sealed record UpdatePageReviewRequest(
    string PageType,
    bool IncludedInWorkflow,
    int ExpectedRevision);

public sealed record UpdatePageReviewTagRequest(
    PageReviewTag ReviewTag,
    int ExpectedRevision);

public sealed record UpdatePageLayoutRequest(
    string LayoutJson,
    int ExpectedRevision);

public sealed record RenderTextClipRequest(
    string LayoutJson,
    string SegmentId);

public sealed record PageLayoutResponse(
    string ProjectId,
    string PageId,
    int ProjectRevision,
    int PageRevision,
    bool HasUnversionedUserChanges,
    string? CurrentVersionId,
    string? LayoutJson);

public sealed record UpdatePageImageEditRequest(
    string OperationsJson,
    int ExpectedPageRevision);

public sealed record PageImageEditResponse(
    string ProjectId,
    string PageId,
    int ProjectRevision,
    int PageRevision,
    bool HasUnversionedUserChanges,
    string? CurrentVersionId,
    string? OperationsJson,
    string RenderedAssetUrl);

public sealed record UpdatePageImageEditResponse(
    ProjectDetailResponse Project,
    PageImageEditResponse ImageEdit);

public sealed record TranslationAnnotationsResponse(
    string ProjectId,
    int ProjectRevision,
    IReadOnlyDictionary<string, string> Pages);

public sealed record UpdateTranslationAnnotationsRequest(
    IReadOnlyDictionary<string, string> Pages,
    int ExpectedRevision);

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

public enum ProjectExportFormat
{
    Folder,
    Zip,
    Pdf
}

public enum ProjectExportResolution
{
    Original,
    Half,
    Quarter
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

public sealed record PageVersionResponse(
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

public sealed record CreatePageVersionRequest(
    string Label,
    int ExpectedRevision,
    PageVersionKind Kind = PageVersionKind.User,
    PageVersionTrack Track = PageVersionTrack.Lettering);

public sealed record PrecheckPageReviewResponse(
    string PageId,
    int PageIndex,
    string PageType,
    double Confidence,
    IReadOnlyList<string> Evidence);

public sealed record PrecheckCharacterResponse(
    string MergeKey,
    string Value,
    IReadOnlyList<int> PageIndices,
    string Evidence,
    double Confidence,
    PrecheckCharacterDetailsResponse? Details = null);

public sealed record PrecheckCharacterDetailsResponse(
    string OriginalName,
    string TranslatedName,
    IReadOnlyList<string> Aliases,
    string Role,
    IReadOnlyList<string> SpeechRules,
    IReadOnlyList<string> Relationships,
    int ValidFromOrder,
    int? ValidToOrder,
    bool RevealSensitive,
    IReadOnlyList<string> EvidencePages);

public sealed record PrecheckTermResponse(
    string MergeKey,
    string Value,
    IReadOnlyList<int> PageIndices,
    string Evidence,
    double Confidence,
    PrecheckTermDetailsResponse? Details = null);

public sealed record PrecheckTermDetailsResponse(
    string Source,
    string Translation,
    string Category,
    string UsageRule,
    int ValidFromOrder,
    int? ValidToOrder,
    bool RevealSensitive,
    IReadOnlyList<string> EvidencePages);

public sealed record PrecheckBatchResponse(
    string BatchId,
    IReadOnlyList<int> OwnerPageIndices,
    string Priority,
    double EstimatedCost);

public sealed record PrecheckReviewResponse(
    string? RunId,
    int ProjectRevision,
    IReadOnlyList<PrecheckPageReviewResponse> Pages,
    IReadOnlyList<PrecheckCharacterResponse> Characters,
    IReadOnlyList<PrecheckTermResponse> Terms,
    IReadOnlyList<PrecheckBatchResponse> Batches,
    PrecheckContextSummaryResponse? Context = null);

public sealed record PrecheckContextSummaryResponse(
    string OverallSynopsis,
    string Tone,
    IReadOnlyList<string> Themes,
    string OverallVoice,
    IReadOnlyList<string> DialogueRules,
    string HonorificPolicy,
    string NamePolicy,
    IReadOnlyList<string> ProhibitedTendencies,
    int CharacterCount,
    int TermCount,
    int ContinuityFactCount,
    IReadOnlyList<PrecheckContinuityFactResponse> ContinuityFacts,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record PrecheckContinuityFactResponse(
    string Fact,
    int RevealedAtOrder,
    string SafeWordingBeforeReveal,
    IReadOnlyList<string> EvidencePages);

public sealed record UpdatePrecheckItemRequest(
    string ItemKey,
    string? Value,
    string? MergeIntoKey,
    bool Delete,
    int ExpectedRevision);

public sealed record StartPrecheckRequest(
    string ProjectId,
    ExecutionMode ExecutionMode = ExecutionMode.Cached,
    string? FixtureRunId = null);

public sealed record StartTranslationRequest(
    string ProjectId,
    ExecutionMode ExecutionMode = ExecutionMode.Cached,
    string? FixtureRunId = null,
    IReadOnlyList<int>? PageIndices = null);

public sealed record StartExportRequest(
    string ProjectId,
    string? DestinationToken,
    ProjectExportFormat Format,
    bool IncludeBackground = true,
    bool IncludeLettering = true,
    ProjectExportResolution Resolution = ProjectExportResolution.Original);

public sealed record PrecheckFixtureSummaryResponse(
    string RunId,
    int PageCount,
    int BatchCount,
    IReadOnlyList<string> AvailableArtifacts,
    DateTimeOffset LastModifiedAt,
    bool IsDefault);

public sealed record ProcessEventResponse(
    string EventId,
    string Stage,
    string Title,
    string RequestSummary,
    string? ResponseSummary,
    string? Detail,
    ProcessEventStatus Status,
    string? ParallelGroup,
    IReadOnlyList<int> PageIndices,
    DateTimeOffset UpdatedAt);

public sealed record TranslationTaskSettingsResponse(
    int BatchConcurrency,
    int BatchSize,
    TranslationInputResolution InputResolution);

public sealed record JobSnapshotResponse(
    string JobId,
    string ProjectId,
    JobStatus Status,
    ExecutionMode ExecutionMode,
    string? FixtureRunId,
    int ProgressPercent,
    IReadOnlyList<ProcessEventResponse> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TranslationTaskSettingsResponse? TranslationSettings = null);
