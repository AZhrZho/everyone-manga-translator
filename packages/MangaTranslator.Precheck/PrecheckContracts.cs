using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

public interface IPageAssetProvider
{
    ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
        string pageId,
        ImageQuality quality,
        CancellationToken cancellationToken = default);
}

public enum ImageQuality
{
    Thumbnail,
    Standard,
    Original
}

public sealed record PageDescriptor(
    string PageId,
    int SourceIndex,
    string FileName,
    int Width,
    int Height,
    string MediaType,
    string ContentHash);

public sealed record PrecheckRequest(
    string BookId,
    IReadOnlyList<PageDescriptor> Pages,
    IPageAssetProvider PageAssets,
    PrecheckOptions Options,
    PrecheckCheckpoint? ResumeFrom = null);

public sealed record PrecheckOptions
{
    public string RunId { get; init; } = $"precheck_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

    public string Model { get; init; } = "gpt-5.4";

    public string? ManifestModel { get; init; }

    public string? AnalysisModel { get; init; }

    public string TargetLanguage { get; init; } = "zh-Hans";

    public int MaxBatchPageCount { get; init; } = 12;

    public double MaxBatchCost { get; init; } = 24;

    public int ContextPagesPerSide { get; init; } = 1;

    public int MaxConcurrentBatches { get; init; } = 3;

    public int MaxAutomaticReviewAttempts { get; init; } = 3;

    public double AutomaticReviewConfidenceThreshold { get; init; } = 0.70;

    public int ContactSheetPageCount { get; init; } = 16;

    public int ContactSheetColumns { get; init; } = 4;

    public int ContactSheetThumbnailWidth { get; init; } = 320;

    public int ContactSheetThumbnailHeight { get; init; } = 460;

    public int MaxConcurrentThumbnailCalls { get; init; } = 2;

    public int MaxConcurrentSemanticMergeCalls { get; init; } = 4;

    public ImageDetailLevel ThumbnailImageDetail { get; init; } = ImageDetailLevel.High;

    public ImageDetailLevel BatchImageDetail { get; init; } = ImageDetailLevel.Original;
}

public sealed record PrecheckCheckpoint(
    string RunId,
    string CompletedStage,
    // Keep this as a concrete, JSON-friendly collection shape.  Checkpoints are
    // deserialized by hosts during resume, and System.Text.Json cannot construct
    // IReadOnlySet<T> from the persisted array representation.
    IReadOnlyList<string> CompletedBatchIds,
    string InputManifestHash,
    string PipelineVersion,
    JsonElement State);

/// <summary>
/// Serializable state carried by <see cref="PrecheckCheckpoint"/>. Hosts should persist the
/// checkpoint emitted by <see cref="PrecheckCheckpointReady"/> atomically and pass it back through
/// <see cref="PrecheckRequest.ResumeFrom"/> when continuing an interrupted run.
/// </summary>
public sealed record PrecheckCheckpointState(
    SourceManifestDocument? SourceManifest,
    ThumbnailPrecheckDocument? ThumbnailPrecheck,
    BatchPlanDocument? BatchPlan,
    IReadOnlyList<BatchObservationDocument> BatchObservations,
    PrecheckMergeResult? Merge,
    AutoReviewResult? AutoReview,
    GlobalSemanticMergeResult? SemanticMerge,
    BookManifestDocument? BookManifest);

public enum PrecheckStatus
{
    Completed,
    Failed,
    Cancelled
}

public sealed record PrecheckResult(
    string RunId,
    PrecheckStatus Status,
    SourceManifestDocument SourceManifest,
    ThumbnailPrecheckDocument ThumbnailPrecheck,
    CrossPageRelationsDocument CrossPageRelations,
    BatchPlanDocument BatchPlan,
    IReadOnlyList<BatchObservationDocument> BatchObservations,
    PrecheckMergeResult Merge,
    AutoReviewResult AutoReview,
    GlobalSemanticMergeResult SemanticMerge,
    BookManifestDocument BookManifest,
    IReadOnlyList<PrecheckWarning> Warnings);

public sealed record PrecheckWarning(
    string Code,
    string Message,
    string? PageId = null,
    string? BatchId = null);

public sealed record SourceManifestDocument(
    string SchemaVersion,
    string RunId,
    string BookId,
    IReadOnlyList<PageDescriptor> Pages,
    IReadOnlyList<SourceManifestIssue> Issues);

public sealed record SourceManifestIssue(
    string Code,
    string Message,
    int? SourceIndex = null,
    IReadOnlyList<string>? PageIds = null);

public sealed record ThumbnailPrecheckDocument(
    string SchemaVersion,
    GlobalObservations Global,
    IReadOnlyList<PageThumbnailObservation> Pages,
    IReadOnlyList<PageRelation> RelationCandidates);

public sealed record GlobalObservations(
    string? PrimaryLanguage,
    string? ReadingDirection,
    double ReadingDirectionConfidence,
    IReadOnlyList<string> Uncertainties);

public sealed record PageThumbnailObservation(
    string PageId,
    string PageType,
    string? ChapterCandidateId,
    string? SceneCandidateId,
    int LayoutComplexity,
    int TextRiskScore,
    int MaskRiskScore,
    int UncertaintyScore,
    bool NeedsHighResolutionReview,
    double Confidence,
    IReadOnlyList<string> Evidence);

public sealed record CrossPageRelationsDocument(
    string SchemaVersion,
    IReadOnlyList<PageRelation> Relations);

public sealed record PageRelation(
    string RelationId,
    string Type,
    IReadOnlyList<string> Pages,
    bool HardConstraint,
    bool MustSameBatch,
    bool RequiresOcrCoordination,
    bool RequiresMaskCoordination,
    double Confidence,
    string Evidence,
    string Status = "candidate");

public sealed record BatchPlanDocument(
    string SchemaVersion,
    IReadOnlyList<BatchPlan> Batches);

public sealed record BatchPlan(
    string BatchId,
    IReadOnlyList<string> OwnerPages,
    IReadOnlyList<string> ContextPagesBefore,
    IReadOnlyList<string> ContextPagesAfter,
    IReadOnlyList<string> HardRelationIds,
    double EstimatedCost,
    string Priority,
    string? SplitReasonBefore,
    string? SplitReasonAfter);

public sealed record BatchObservationDocument(
    string SchemaVersion,
    string BatchId,
    IReadOnlyList<string> OwnerPages,
    JsonElement Observations,
    JsonElement RelationVerifications,
    JsonElement ArtifactDeltas,
    JsonElement ProcessingHints,
    JsonElement Uncertainties,
    IReadOnlyList<PrecheckWarning> Warnings);

public sealed record PrecheckMergeResult(
    JsonElement Chapters,
    JsonElement Characters,
    JsonElement CulturalLocalizations,
    JsonElement Glossary,
    JsonElement StoryBible,
    JsonElement ProcessingHints,
    IReadOnlyList<PrecheckConflict> Conflicts);

public sealed record AutoReviewResult(
    PrecheckMergeResult FinalMerge,
    IReadOnlyList<AutoReviewItem> Items,
    IReadOnlyList<PrecheckConflict> ResolvedConflicts,
    IReadOnlyList<PrecheckConflict> UnresolvedConflicts);

public sealed record GlobalSemanticMergeResult(
    string TargetLanguage,
    JsonElement Chapters,
    JsonElement Characters,
    JsonElement CulturalLocalizations,
    JsonElement Glossary,
    JsonElement StoryBible);

public sealed record BookManifestDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("book_id")] string BookId,
    [property: JsonPropertyName("pages")] IReadOnlyList<BookManifestPage> Pages);

public sealed record BookManifestPage(
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("chapter_id")] string? ChapterId,
    [property: JsonPropertyName("scene_id")] string SceneId,
    [property: JsonPropertyName("page_type")] string PageType,
    [property: JsonPropertyName("include_in_mask")] bool IncludeInMask);

public sealed record PrecheckConflict(
    string ConflictId,
    string Type,
    string Artifact,
    string MergeKey,
    IReadOnlyList<string> Pages,
    IReadOnlyList<string> Claims,
    string Status,
    double Confidence,
    string Evidence);

public sealed record AutoReviewItem(
    string ReviewId,
    string Type,
    IReadOnlyList<string> Pages,
    string Status,
    int Attempts,
    string NextAction);

public sealed record GeneratedImage(
    string AssetId,
    string MediaType,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height);

public sealed record PrecheckProgress(
    string Stage,
    string Action,
    string? ItemId = null,
    int? Current = null,
    int? Total = null,
    string? Detail = null,
    TimeSpan? Elapsed = null,
    string? ErrorCode = null);

public interface IPrecheckProgressReporter
{
    void Report(PrecheckProgress progress);
}

public sealed class NullPrecheckProgressReporter : IPrecheckProgressReporter
{
    public static NullPrecheckProgressReporter Instance { get; } = new();

    private NullPrecheckProgressReporter()
    {
    }

    public void Report(PrecheckProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
    }
}

public interface IPrecheckImageComposer
{
    Task<IReadOnlyList<GeneratedImage>> CreateContactSheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeneratedImage>> CreateAdjacencySheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default);
}

public interface IThumbnailPrecheckAnalyzer
{
    Task<ThumbnailPrecheckDocument> AnalyzeAsync(
        PrecheckRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBatchAnalyzer
{
    Task<BatchObservationDocument> AnalyzeAsync(
        PrecheckRequest request,
        BatchPlan batch,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        CancellationToken cancellationToken = default);
}

public interface IPrecheckMerger
{
    PrecheckMergeResult Merge(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        BatchPlanDocument batchPlan,
        IReadOnlyList<BatchObservationDocument> observations);
}

public interface IAutomaticReviewProcessor
{
    Task<AutoReviewResult> ReviewAsync(
        PrecheckRequest request,
        PrecheckMergeResult merge,
        CancellationToken cancellationToken = default);
}

public interface IGlobalSemanticConsolidator
{
    Task<GlobalSemanticMergeResult> ConsolidateAsync(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        BatchPlanDocument batchPlan,
        IReadOnlyList<BatchObservationDocument> observations,
        AutoReviewResult automaticReview,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional durable store for the independently generated global semantic artifacts. The
/// model-backed consolidator saves each artifact immediately and reuses it after a process restart.
/// </summary>
public interface IGlobalSemanticArtifactStore
{
    ValueTask<JsonElement?> TryLoadAsync(
        string runId,
        string artifactName,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        string runId,
        string artifactName,
        JsonElement value,
        CancellationToken cancellationToken = default);
}

public interface IBookManifestBuilder
{
    BookManifestDocument Build(
        PrecheckRequest request,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        IReadOnlyList<BatchObservationDocument> observations,
        GlobalSemanticMergeResult semanticMerge);
}

public abstract record PrecheckEvent;

public sealed record PrecheckStageStarted(string Stage) : PrecheckEvent;

public sealed record PrecheckStageCompleted(string Stage) : PrecheckEvent;

public sealed record SourceManifestReady(SourceManifestDocument Document) : PrecheckEvent;

public sealed record ThumbnailPrecheckReady(ThumbnailPrecheckDocument Document) : PrecheckEvent;

public sealed record BatchPlanReady(BatchPlanDocument Document) : PrecheckEvent;

public sealed record BatchCompleted(BatchObservationDocument Document) : PrecheckEvent;

public sealed record PrecheckMergeReady(PrecheckMergeResult Result) : PrecheckEvent;

public sealed record AutomaticReviewCompleted(AutoReviewResult Result) : PrecheckEvent;

public sealed record GlobalSemanticMergeCompleted(GlobalSemanticMergeResult Result) : PrecheckEvent;

public sealed record BookManifestReady(BookManifestDocument Document) : PrecheckEvent;

public sealed record PrecheckCheckpointReady(PrecheckCheckpoint Checkpoint) : PrecheckEvent;

public sealed record PrecheckCompleted(PrecheckResult Result) : PrecheckEvent;
