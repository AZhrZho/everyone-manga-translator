using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MangaTranslator.Precheck;

public interface IPrecheckEngine
{
    Task<PrecheckResult> RunAsync(
        PrecheckRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<PrecheckEvent> RunStreamingAsync(
        PrecheckRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PrecheckEngine : IPrecheckEngine
{
    public const string PipelineVersion = "1.1.0";

    private static readonly JsonSerializerOptions CheckpointJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IThumbnailPrecheckAnalyzer _thumbnailAnalyzer;
    private readonly DynamicBatchPlanner _batchPlanner;
    private readonly IBatchAnalyzer _batchAnalyzer;
    private readonly IPrecheckMerger _merger;
    private readonly IAutomaticReviewProcessor _automaticReview;
    private readonly IGlobalSemanticConsolidator _globalSemanticConsolidator;
    private readonly IBookManifestBuilder _bookManifestBuilder;

    public PrecheckEngine(
        IThumbnailPrecheckAnalyzer thumbnailAnalyzer,
        DynamicBatchPlanner batchPlanner,
        IBatchAnalyzer batchAnalyzer,
        IPrecheckMerger merger,
        IAutomaticReviewProcessor automaticReview,
        IGlobalSemanticConsolidator globalSemanticConsolidator,
        IBookManifestBuilder? bookManifestBuilder = null)
    {
        _thumbnailAnalyzer = thumbnailAnalyzer ?? throw new ArgumentNullException(nameof(thumbnailAnalyzer));
        _batchPlanner = batchPlanner ?? throw new ArgumentNullException(nameof(batchPlanner));
        _batchAnalyzer = batchAnalyzer ?? throw new ArgumentNullException(nameof(batchAnalyzer));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _automaticReview = automaticReview ?? throw new ArgumentNullException(nameof(automaticReview));
        _globalSemanticConsolidator = globalSemanticConsolidator
            ?? throw new ArgumentNullException(nameof(globalSemanticConsolidator));
        _bookManifestBuilder = bookManifestBuilder ?? new DeterministicBookManifestBuilder();
    }

    public async Task<PrecheckResult> RunAsync(
        PrecheckRequest request,
        CancellationToken cancellationToken = default)
    {
        PrecheckResult? result = null;

        await foreach (var @event in RunStreamingAsync(request, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (@event is PrecheckCompleted completed)
            {
                result = completed.Result;
            }
        }

        return result ?? throw new InvalidOperationException(
            "The precheck stream completed without a final result.");
    }

    public async IAsyncEnumerable<PrecheckEvent> RunStreamingAsync(
        PrecheckRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var inputManifestHash = ComputeInputManifestHash(request);
        var state = LoadCheckpointState(request, inputManifestHash);

        var sourceManifest = state.SourceManifest ?? CreateSourceManifest(request);
        yield return new PrecheckStageStarted("source_manifest");
        yield return new SourceManifestReady(sourceManifest);
        yield return new PrecheckStageCompleted("source_manifest");
        state = state with { SourceManifest = sourceManifest };
        yield return CreateCheckpointEvent(
            request,
            "source_manifest",
            inputManifestHash,
            state);

        yield return new PrecheckStageStarted("thumbnail_precheck");
        var thumbnailPrecheck = state.ThumbnailPrecheck;
        if (thumbnailPrecheck is null)
        {
            thumbnailPrecheck = await _thumbnailAnalyzer
                .AnalyzeAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        yield return new ThumbnailPrecheckReady(thumbnailPrecheck);
        yield return new PrecheckStageCompleted("thumbnail_precheck");
        state = state with { ThumbnailPrecheck = thumbnailPrecheck };
        yield return CreateCheckpointEvent(
            request,
            "thumbnail_precheck",
            inputManifestHash,
            state);

        var crossPageRelations = new CrossPageRelationsDocument(
            "1.0.0",
            thumbnailPrecheck.RelationCandidates);

        yield return new PrecheckStageStarted("batch_planning");
        var batchPlan = state.BatchPlan ?? _batchPlanner.Plan(request, thumbnailPrecheck);
        yield return new BatchPlanReady(batchPlan);
        yield return new PrecheckStageCompleted("batch_planning");
        state = state with { BatchPlan = batchPlan };
        yield return CreateCheckpointEvent(
            request,
            "batch_planning",
            inputManifestHash,
            state);

        yield return new PrecheckStageStarted("batch_analysis");
        var plannedBatchIds = batchPlan.Batches
            .Select(batch => batch.BatchId)
            .ToHashSet(StringComparer.Ordinal);
        var observations = state.BatchObservations
            .Where(observation => plannedBatchIds.Contains(observation.BatchId))
            .GroupBy(observation => observation.BatchId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.Ordinal);
        foreach (var observation in observations.Values.OrderBy(item => item.BatchId, StringComparer.Ordinal))
        {
            yield return new BatchCompleted(observation);
        }

        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maximumConcurrency = Math.Max(1, request.Options.MaxConcurrentBatches);
        using var batchSemaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var pendingBatches = batchPlan.Batches
            .Where(batch => !observations.ContainsKey(batch.BatchId))
            .Select(batch => AnalyzeBatchWithLimitAsync(
                request,
                batch,
                thumbnailPrecheck,
                batchSemaphore,
                batchCancellation.Token))
            .ToList();

        try
        {
            while (pendingBatches.Count > 0)
            {
                var completedTask = await Task.WhenAny(pendingBatches).ConfigureAwait(false);
                pendingBatches.Remove(completedTask);
                var observation = await completedTask.ConfigureAwait(false);
                observations[observation.BatchId] = observation;
                yield return new BatchCompleted(observation);
                state = state with
                {
                    BatchObservations = OrderObservations(observations.Values)
                };
                yield return CreateCheckpointEvent(
                    request,
                    pendingBatches.Count == 0
                        ? "batch_analysis"
                        : "batch_analysis_partial",
                    inputManifestHash,
                    state);
            }
        }
        finally
        {
            if (pendingBatches.Count > 0)
            {
                batchCancellation.Cancel();
                try
                {
                    await Task.WhenAll(pendingBatches).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Preserve the first batch exception or iterator cancellation.
                }
            }
        }

        yield return new PrecheckStageCompleted("batch_analysis");
        state = state with
        {
            BatchObservations = OrderObservations(observations.Values)
        };
        yield return CreateCheckpointEvent(
            request,
            "batch_analysis",
            inputManifestHash,
            state);

        yield return new PrecheckStageStarted("merge");
        var orderedObservations = OrderObservations(observations.Values);
        var merge = state.Merge ?? _merger.Merge(
            request,
            thumbnailPrecheck,
            batchPlan,
            orderedObservations);
        yield return new PrecheckMergeReady(merge);
        yield return new PrecheckStageCompleted("merge");
        state = state with { Merge = merge };
        yield return CreateCheckpointEvent(request, "merge", inputManifestHash, state);

        yield return new PrecheckStageStarted("automatic_review");
        var autoReview = state.AutoReview;
        if (autoReview is null)
        {
            autoReview = await _automaticReview
                .ReviewAsync(request, merge, cancellationToken)
                .ConfigureAwait(false);
        }

        yield return new AutomaticReviewCompleted(autoReview);
        yield return new PrecheckStageCompleted("automatic_review");
        state = state with { AutoReview = autoReview };
        yield return CreateCheckpointEvent(
            request,
            "automatic_review",
            inputManifestHash,
            state);

        yield return new PrecheckStageStarted("global_semantic_merge");
        var semanticMerge = state.SemanticMerge;
        if (semanticMerge is null)
        {
            semanticMerge = await _globalSemanticConsolidator
                .ConsolidateAsync(
                request,
                thumbnailPrecheck,
                batchPlan,
                orderedObservations,
                autoReview,
                cancellationToken)
                .ConfigureAwait(false);
        }

        yield return new GlobalSemanticMergeCompleted(semanticMerge);
        yield return new PrecheckStageCompleted("global_semantic_merge");
        state = state with { SemanticMerge = semanticMerge };
        yield return CreateCheckpointEvent(
            request,
            "global_semantic_merge",
            inputManifestHash,
            state);

        yield return new PrecheckStageStarted("book_manifest");
        var bookManifest = state.BookManifest ?? _bookManifestBuilder.Build(
            request,
            thumbnailPrecheck,
            orderedObservations,
            semanticMerge);
        yield return new BookManifestReady(bookManifest);
        yield return new PrecheckStageCompleted("book_manifest");
        state = state with { BookManifest = bookManifest };
        yield return CreateCheckpointEvent(
            request,
            "book_manifest",
            inputManifestHash,
            state);

        var status = cancellationToken.IsCancellationRequested
            ? PrecheckStatus.Cancelled
            : PrecheckStatus.Completed;

        var warnings = sourceManifest.Issues
            .Select(issue => new PrecheckWarning(issue.Code, issue.Message))
            .Concat(orderedObservations.SelectMany(observation => observation.Warnings))
            .Concat(autoReview.UnresolvedConflicts.Select(conflict => new PrecheckWarning(
                "unresolved_auto",
                $"Conflict {conflict.ConflictId} was conservatively degraded after automatic review.",
                conflict.Pages.Count > 0 ? conflict.Pages[0] : null)))
            .ToArray();

        yield return new PrecheckCompleted(new PrecheckResult(
            request.Options.RunId,
            status,
            sourceManifest,
            thumbnailPrecheck,
            crossPageRelations,
            batchPlan,
            orderedObservations,
            autoReview.FinalMerge,
            autoReview,
            semanticMerge,
            bookManifest,
            warnings));
    }

    private static PrecheckCheckpointState LoadCheckpointState(
        PrecheckRequest request,
        string inputManifestHash)
    {
        if (request.ResumeFrom is null)
        {
            return EmptyCheckpointState();
        }

        var checkpoint = request.ResumeFrom;
        if (!StringComparer.Ordinal.Equals(checkpoint.RunId, request.Options.RunId))
        {
            throw new ArgumentException(
                $"Checkpoint run ID '{checkpoint.RunId}' does not match request run ID "
                + $"'{request.Options.RunId}'.",
                nameof(request));
        }

        if (!StringComparer.Ordinal.Equals(checkpoint.PipelineVersion, PipelineVersion))
        {
            throw new ArgumentException(
                $"Checkpoint pipeline version '{checkpoint.PipelineVersion}' is not supported; "
                + $"expected '{PipelineVersion}'.",
                nameof(request));
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                checkpoint.InputManifestHash,
                inputManifestHash))
        {
            throw new ArgumentException(
                "Checkpoint input manifest hash does not match the current request.",
                nameof(request));
        }

        try
        {
            var state = checkpoint.State.Deserialize<PrecheckCheckpointState>(
                CheckpointJsonOptions);
            if (state is null)
            {
                throw new JsonException("Checkpoint state was null.");
            }

            return state with
            {
                BatchObservations = state.BatchObservations ?? []
            };
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Checkpoint state is not a valid PrecheckCheckpointState document.",
                nameof(request),
                exception);
        }
    }

    private static PrecheckCheckpointReady CreateCheckpointEvent(
        PrecheckRequest request,
        string completedStage,
        string inputManifestHash,
        PrecheckCheckpointState state)
    {
        var completedBatchIds = state.BatchObservations
            .Select(observation => observation.BatchId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(batchId => batchId, StringComparer.Ordinal)
            .ToArray();
        return new PrecheckCheckpointReady(new PrecheckCheckpoint(
            request.Options.RunId,
            completedStage,
            completedBatchIds,
            inputManifestHash,
            PipelineVersion,
            JsonSerializer.SerializeToElement(state, CheckpointJsonOptions)));
    }

    private static PrecheckCheckpointState EmptyCheckpointState() => new(
        null,
        null,
        null,
        [],
        null,
        null,
        null,
        null);

    private static BatchObservationDocument[] OrderObservations(
        IEnumerable<BatchObservationDocument> observations) =>
        observations
            .OrderBy(item => item.BatchId, StringComparer.Ordinal)
            .ToArray();

    private static string ComputeInputManifestHash(PrecheckRequest request)
    {
        var input = new
        {
            request.BookId,
            Pages = request.Pages.OrderBy(page => page.SourceIndex).ThenBy(page => page.PageId),
            request.Options.Model,
            request.Options.ManifestModel,
            request.Options.AnalysisModel,
            request.Options.TargetLanguage,
            request.Options.MaxBatchPageCount,
            request.Options.MaxBatchCost,
            request.Options.ContextPagesPerSide,
            request.Options.MaxAutomaticReviewAttempts,
            request.Options.AutomaticReviewConfidenceThreshold,
            request.Options.ContactSheetPageCount,
            request.Options.ContactSheetColumns,
            request.Options.ContactSheetThumbnailWidth,
            request.Options.ContactSheetThumbnailHeight,
            request.Options.ThumbnailImageDetail,
            request.Options.BatchImageDetail
        };
        var json = JsonSerializer.Serialize(input, CheckpointJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private async Task<BatchObservationDocument> AnalyzeBatchWithLimitAsync(
        PrecheckRequest request,
        BatchPlan batch,
        ThumbnailPrecheckDocument thumbnailPrecheck,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _batchAnalyzer
                .AnalyzeAsync(request, batch, thumbnailPrecheck, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static void ValidateRequest(PrecheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BookId))
        {
            throw new ArgumentException("A book ID is required.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Pages);
        ArgumentNullException.ThrowIfNull(request.PageAssets);
        ArgumentNullException.ThrowIfNull(request.Options);

        var duplicatePageIds = request.Pages
            .GroupBy(page => page.PageId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePageIds.Length > 0)
        {
            throw new ArgumentException(
                $"Page IDs must be unique: {string.Join(", ", duplicatePageIds)}",
                nameof(request));
        }

        if (request.Options.MaxConcurrentBatches <= 0)
        {
            throw new ArgumentException(
                "MaxConcurrentBatches must be positive.",
                nameof(request));
        }

        if (request.Options.MaxConcurrentSemanticMergeCalls <= 0)
        {
            throw new ArgumentException(
                "MaxConcurrentSemanticMergeCalls must be positive.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Options.TargetLanguage))
        {
            throw new ArgumentException(
                "TargetLanguage is required.",
                nameof(request));
        }

        if (request.Options.AutomaticReviewConfidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentException(
                "AutomaticReviewConfidenceThreshold must be between 0 and 1.",
                nameof(request));
        }
    }

    private static SourceManifestDocument CreateSourceManifest(PrecheckRequest request)
    {
        var pages = request.Pages
            .OrderBy(page => page.SourceIndex)
            .ToArray();
        var issues = new List<SourceManifestIssue>();

        foreach (var group in pages
            .GroupBy(page => page.SourceIndex)
            .Where(group => group.Count() > 1))
        {
            var pageIds = group.Select(page => page.PageId).ToArray();
            issues.Add(new SourceManifestIssue(
                "duplicate_source_index",
                $"Source index {group.Key} is used by multiple pages: {string.Join(", ", pageIds)}.",
                group.Key,
                pageIds));
        }

        if (pages.Length > 0)
        {
            var existingIndices = pages
                .Select(page => page.SourceIndex)
                .ToHashSet();
            for (var index = pages[0].SourceIndex; index <= pages[^1].SourceIndex; index++)
            {
                if (!existingIndices.Contains(index))
                {
                    issues.Add(new SourceManifestIssue(
                        "missing_source_index",
                        $"No source page was supplied for index {index}.",
                        index));
                }
            }
        }

        foreach (var group in pages
            .Where(page => !string.IsNullOrWhiteSpace(page.ContentHash))
            .GroupBy(page => page.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            var pageIds = group.Select(page => page.PageId).ToArray();
            issues.Add(new SourceManifestIssue(
                "duplicate_content_hash",
                $"Pages share the same content hash: {string.Join(", ", pageIds)}.",
                PageIds: pageIds));
        }

        foreach (var page in pages.Where(page => page.Width <= 0 || page.Height <= 0))
        {
            issues.Add(new SourceManifestIssue(
                "invalid_page_dimensions",
                $"Page {page.PageId} has invalid dimensions {page.Width}x{page.Height}.",
                page.SourceIndex,
                [page.PageId]));
        }

        return new SourceManifestDocument(
            "1.0.0",
            request.Options.RunId,
            request.BookId,
            pages,
            issues);
    }
}
