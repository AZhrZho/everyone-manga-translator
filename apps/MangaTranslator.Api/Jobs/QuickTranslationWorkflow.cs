using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MangaTranslator.Api.BrowserInference;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Projects;
using MangaTranslator.Api.Settings;
using MangaTranslator.Cleaning;
using MangaTranslator.Contracts;
using MangaTranslator.Dialogue;
using MangaTranslator.FontReference.FileSystem;
using MangaTranslator.LocalDetection;
using MangaTranslator.Precheck;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public interface IQuickTranslationWorkflow
{
    Task<LiveTranslationPreparation> PrepareAsync(
        string projectId,
        string runId,
        string detectorId,
        DetectionArtifactPolicy detectionArtifactPolicy,
        IReadOnlyList<int>? pageIndices,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse settings,
        CancellationToken cancellationToken);
}

/// <summary>
/// Page-isolated Quick workflow. It intentionally does not read semantic_merge, book_manifest,
/// auto_review, characters, glossary, or any other preprocessing artifact.
/// </summary>
public sealed class QuickTranslationWorkflow(
    IProjectStore projects,
    IProjectAssetStore assets,
    IProjectAssetWriter assetWriter,
    IProjectArtifactStore artifacts,
    IProjectResultService resultService,
    IWorkflowModelSettingsResolver modelSettings,
    IHttpClientFactory httpClientFactory,
    IPipelineArtifactCatalog pipelineArtifacts,
    TranslationAnnotationService annotations,
    ITextDetectorRuntime detectorRuntime,
    FontCatalogService fonts,
    LayoutAwareCleaningService layoutCleaning) : IQuickTranslationWorkflow
{
    private const string ModelStageId = "translation";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<LiveTranslationPreparation> PrepareAsync(
        string projectId,
        string runId,
        string detectorId,
        DetectionArtifactPolicy detectionArtifactPolicy,
        IReadOnlyList<int>? pageIndices,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        if (project.WorkflowMode != MangaTranslator.ProjectSystem.ProjectWorkflowMode.Quick)
        {
            throw new InvalidOperationException("Quick translation can only run for a Quick-mode project.");
        }
        var requested = pageIndices?.ToHashSet();
        var pages = project.Pages
            .Where(page => page.IncludedInWorkflow && (requested is null || requested.Contains(page.Index)))
            .OrderBy(page => page.Index)
            .ToArray();
        if (pages.Length == 0) throw new InvalidOperationException("The project has no pages selected for translation.");

        _ = await modelSettings.ResolveAsync(ModelStageId, cancellationToken);
        var annotationSnapshot = await annotations.GetAsync(projectId, cancellationToken);
        var inputs = new List<QuickPageInput>(pages.Length);
        foreach (var page in pages)
        {
            var currentLayout = await projects.GetPageLayoutAsync(projectId, page.PageId, cancellationToken);
            var manualLettering = ManualLetteringOverrides.CreateContext(
                currentLayout.LayoutJson,
                currentLayout.HasUnversionedUserChanges);
            inputs.Add(new QuickPageInput(
                page.PageId,
                page.Index,
                page.PageType,
                page.SourceAssetKey,
                await ComputeAssetSha256Async(projectId, page.SourceAssetKey, cancellationToken),
                page.Width,
                page.Height,
                CreateGuidance(annotationSnapshot.Pages.GetValueOrDefault(page.PageId), manualLettering)));
        }

        var pipeline = string.Equals(detectorId, TextDetectorIds.Ctd, StringComparison.OrdinalIgnoreCase)
            ? pipelineArtifacts.MatchProject(project)
            : null;
        IFontReferenceImageProvider fontReference = new ProjectFontReferenceProvider(
            new FileSystemFontReferenceImageProvider(fonts.LibraryRoot),
            project.EnabledFontIds);
        var fontSnapshot = await fontReference.GetFontReferenceImageAsync(
            new FontReferenceImageRequest(
                $"quick:{projectId}",
                inputs[0].PageId,
                project.TargetLanguage),
            cancellationToken);
        var fontSetSha256 = WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(new
        {
            fontSnapshot.LibraryRevision,
            Fonts = fontSnapshot.Fonts.OrderBy(item => item.FontId, StringComparer.Ordinal)
        }, JsonOptions));
        var artifactMap = (await artifacts.ListAsync(projectId, cancellationToken))
            .ToDictionary(item => item.Name, item => item.Json, StringComparer.OrdinalIgnoreCase);
        var detector = detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
            ? CreateBrowserOnlyDetectorSelection(detectorId)
            : await detectorRuntime.ResolveAsync(detectorId, requireModel: false, cancellationToken);
        var missingBrowserPages = new List<int>();
        var needsDetectorModel = false;
        for (var index = 0; index < inputs.Count; index++)
        {
            var page = inputs[index];
            var reusableArtifactName = await FindReusableDetectionArtifactNameAsync(
                    projectId,
                    page,
                    artifactMap,
                    pipeline,
                    detector,
                    detectionArtifactPolicy,
                    cancellationToken);
            if (reusableArtifactName is null)
            {
                if (detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly)
                {
                    missingBrowserPages.Add(page.Index);
                }
                else
                {
                    needsDetectorModel = true;
                }
            }
            else
            {
                inputs[index] = page with { DetectionArtifactName = reusableArtifactName };
            }
        }
        if (missingBrowserPages.Count > 0)
        {
            throw new InvalidOperationException(
                "Browser CRAFT requires a saved browser detection result for every selected page. "
                + $"Missing pages: {string.Join(", ", missingBrowserPages)}.");
        }
        if (needsDetectorModel && !detector.HasModel)
        {
            throw new InvalidOperationException(
                "快速模式仍需使用本地文字检测模型来处理没有可复用检测与清字缓存的页面。" +
                $" Engine: {detector.DetectorId}; model path: {detectorRuntime.DefaultInstallPath(detector.DetectorId)};" +
                $" or set {detectorRuntime.ModelConfigurationHint(detector.DetectorId)}.");
        }
        var run = new QuickRunDocument(
            "1.2.0",
            runId,
            projectId,
            detector,
            detectionArtifactPolicy,
            project.TargetLanguage,
            project.EnabledFontIds,
            fontSetSha256,
            inputs,
            pipeline?.RunId);
        await SaveAsync(projectId, RunArtifactName(runId), run, cancellationToken);
        return new LiveTranslationPreparation(
            pages,
            annotationSnapshot.Pages.Count(item => !string.IsNullOrWhiteSpace(item.Value)));
    }

    public async Task<TranslationPageExecutionResult> ExecutePageAsync(
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken)
    {
        var prepared = await PreparePageAsync(context, cancellationToken);
        if (prepared.CompletedResult is not null) return prepared.CompletedResult;
        if (prepared.Dialogue is null)
        {
            await context.ReportAsync(
                "Quick mode is translating and planning lettering for this page.",
                $"regions: {prepared.Detection.Regions.Count}; no preprocessing context",
                cancellationToken);
            using var httpClient = httpClientFactory.CreateClient("workflow-model-provider");
            var provider = LiveTranslationWorkflow.CreateModelProvider(httpClient, prepared.Settings);
            var translator = new ModelBackedMultimodalDialogueTranslator(
                provider,
                QuickDialoguePromptProvider.Instance);
            prepared = prepared with
            {
                Dialogue = await translator.TranslateAsync(prepared.Request!, cancellationToken)
            };
            await SaveDialogueAsync(prepared, cancellationToken);
        }
        return await FinalizePageAsync(prepared, prepared.Dialogue, cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse batchSettings,
        CancellationToken cancellationToken)
    {
        var prepared = new List<QuickPreparedPage>(contexts.Count);
        var results = new List<TranslationBatchExecutionResult>(contexts.Count);
        foreach (var context in contexts)
        {
            var page = await PreparePageAsync(context, cancellationToken);
            if (page.CompletedResult is not null)
            {
                results.Add(new TranslationBatchExecutionResult(context.PageIndex, page.CompletedResult));
            }
            else if (page.Dialogue is not null)
            {
                results.Add(new TranslationBatchExecutionResult(
                    context.PageIndex,
                    await FinalizePageAsync(page, page.Dialogue, cancellationToken)));
            }
            else
            {
                prepared.Add(page);
            }
        }
        if (prepared.Count == 0) return results;

        foreach (var page in prepared)
        {
            await page.Context.ReportAsync(
                $"Quick mode is translating {prepared.Count} selected pages in one batch request.",
                $"batch pages: {string.Join(", ", prepared.Select(item => item.Page.Index))}; no preprocessing context",
                cancellationToken);
        }
        using var httpClient = httpClientFactory.CreateClient("workflow-model-provider");
        var provider = LiveTranslationWorkflow.CreateModelProvider(httpClient, prepared[0].Settings);
        var dialogues = await TranslationBatchModelInvoker.TranslateAsync(
            provider,
            QuickDialoguePromptProvider.Instance,
            prepared.Select(item => item.Request!).ToArray(),
            batchSettings.InputResolution,
            cancellationToken);
        var completedPages = prepared.Select(page => page with
        {
            Dialogue = dialogues.Single(item => string.Equals(
                item.PageId,
                page.Page.PageId,
                StringComparison.Ordinal))
        }).ToArray();
        foreach (var completed in completedPages)
        {
            // The provider has already completed the paid batch request. Persist every page-level
            // model result before observing a user stop so resume can finalize locally without
            // sending the same pages to the model again.
            await SaveDialogueAsync(completed, CancellationToken.None);
        }
        foreach (var completed in completedPages)
        {
            results.Add(new TranslationBatchExecutionResult(
                completed.Context.PageIndex,
                await FinalizePageAsync(completed, completed.Dialogue!, cancellationToken)));
        }
        return results.OrderBy(item => item.PageIndex).ToArray();
    }

    private async Task<QuickPreparedPage> PreparePageAsync(
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(
            context.ProjectId,
            RunArtifactName(context.RunId),
            cancellationToken);
        var page = run.Pages.FirstOrDefault(item => item.Index == context.PageIndex)
            ?? throw new InvalidOperationException($"Quick run {context.RunId} does not contain page {context.PageIndex}.");
        var checkpoint = await LoadOptionalAsync<QuickPageCheckpoint>(
            context.ProjectId,
            PageCheckpointArtifactName(context.RunId, page.PageId),
            cancellationToken);
        var settings = await modelSettings.ResolveAsync(ModelStageId, cancellationToken);
        var source = await ReadSourceAsync(context.ProjectId, page, cancellationToken);
        var detection = await GetDetectionAsync(context, run, page, source, cancellationToken);
        var translationFingerprint = CreateTranslationFingerprint(
            run,
            page,
            source,
            detection,
            settings);
        var pageFingerprint = WorkflowCacheFingerprint.Page(detection.Fingerprint, translationFingerprint);
        if (checkpoint is not null
            && WorkflowCacheFingerprint.MatchesValid(pageFingerprint, checkpoint.Fingerprint)
            && await assets.ExistsAsync(context.ProjectId, checkpoint.Result.CleanAssetKey, cancellationToken)
            && await assets.ExistsAsync(context.ProjectId, checkpoint.Result.MaskAssetKey, cancellationToken)
            && await assets.ExistsAsync(context.ProjectId, checkpoint.Result.RenderedAssetKey, cancellationToken))
        {
            await context.ReportAsync("Reusing the completed Quick-mode page checkpoint.", $"page: {page.Index}", cancellationToken);
            return new QuickPreparedPage(
                context, run, page, settings, source, detection,
                translationFingerprint, pageFingerprint, null, null, checkpoint.Result);
        }

        var dialogueArtifactName = DialogueArtifactName(context.RunId, page.PageId);
        var dialogueCheckpoint = await LoadOptionalAsync<QuickDialogueCheckpoint>(context.ProjectId, dialogueArtifactName, cancellationToken);
        var dialogue = dialogueCheckpoint is not null
                       && WorkflowCacheFingerprint.MatchesValid(
                           translationFingerprint,
                           dialogueCheckpoint.Fingerprint)
            ? dialogueCheckpoint.Result
            : null;
        IFontReferenceImageProvider fontReference = new ProjectFontReferenceProvider(
            new FileSystemFontReferenceImageProvider(fonts.LibraryRoot),
            run.EnabledFontIds);
        var request = new DialogueTranslationRequest(
                    $"{context.RunId}-{page.PageId}",
                    $"quick:{context.ProjectId}",
                    new DialoguePageInput(
                        page.PageId,
                        page.Index,
                        null,
                        page.PageId,
                        page.PageType,
                        true,
                        page.Width,
                        page.Height,
                        source.MediaType,
                        detection.Regions),
                    EmptyKnowledge(run.TargetLanguage),
                    new LiveTranslationWorkflow.SinglePageImageProvider(page.PageId, source),
                    fontReference,
                    new DialogueTranslationOptions
                    {
                        Model = settings.ModelId,
                        TargetLanguage = run.TargetLanguage
                    },
                    page.Guidance)
                {
                    BookPageIndex = [new DialogueBookPageIndexEntry(page.PageId, page.Index)]
                };
        return new QuickPreparedPage(
            context, run, page, settings, source, detection,
            translationFingerprint, pageFingerprint, request, dialogue, null);
    }

    private async Task SaveDialogueAsync(QuickPreparedPage prepared, CancellationToken cancellationToken) =>
        await SaveAsync(
            prepared.Context.ProjectId,
            DialogueArtifactName(prepared.Context.RunId, prepared.Page.PageId),
            new QuickDialogueCheckpoint(prepared.TranslationFingerprint, prepared.Dialogue!),
            cancellationToken);

    private async Task<TranslationPageExecutionResult> FinalizePageAsync(
        QuickPreparedPage prepared,
        DialogueTranslationResult dialogue,
        CancellationToken cancellationToken)
    {
        var context = prepared.Context;
        var page = prepared.Page;
        var detection = prepared.Detection;
        var manualLettering = ManualLetteringOverrides.ReadContext(page.Guidance);
        var layoutJson = ManualLetteringOverrides.Apply(JsonSerializer.Serialize(dialogue, JsonOptions), manualLettering);
        var projectPage = (await projects.GetAsync(context.ProjectId, cancellationToken)).Pages
            .Single(item => item.PageId == page.PageId);
        var effectiveCleaning = await layoutCleaning.RebuildAsync(
            context.ProjectId,
            projectPage,
            page.SourceAssetKey,
            detection.MaskAssetKey,
            layoutJson,
            $"clean/{page.PageId}/{context.JobId}",
            cancellationToken);
        var renderedAssetKey = $"rendered/{page.PageId}/{context.JobId}.png";
        await context.ReportAsync(
            "Quick-mode lettering plan is ready; running the offline renderer.",
            $"segments: {dialogue.Segments.Count}",
            cancellationToken);
        await using (var rendered = await resultService.RenderPageAsync(
                         context.ProjectId,
                         page.PageId,
                         effectiveCleaning.CleanAssetKey,
                         layoutJson,
                         cancellationToken))
        {
            await assetWriter.SaveAsync(context.ProjectId, renderedAssetKey, rendered.Content, cancellationToken);
        }

        var result = new TranslationPageExecutionResult(
            layoutJson,
            effectiveCleaning.CleanAssetKey,
            effectiveCleaning.MaskAssetKey,
            renderedAssetKey,
            $"Page {page.Index} completed in Quick mode.",
            $"Detected {detection.Regions.Count} regions and generated {dialogue.Segments.Count} lettering segments; "
            + $"detection source: {detection.Provenance}; model: {dialogue.Model}.",
            dialogue.NeedsPriorityReview,
            detection.MaskAssetKey);
        await SaveAsync(
            context.ProjectId,
            PageCheckpointArtifactName(context.RunId, page.PageId),
            new QuickPageCheckpoint(prepared.PageFingerprint, result),
            cancellationToken);
        return result;
    }

    private async Task<LiveTranslationWorkflow.LiveDetectionCheckpoint> GetDetectionAsync(
        TranslationPageExecutionContext context,
        QuickRunDocument run,
        QuickPageInput page,
        LiveTranslationWorkflow.SourceImage source,
        CancellationToken cancellationToken)
    {
        var detector = run.Detector
            ?? throw new InvalidOperationException(
                $"Quick translation run {context.RunId} has no text detector snapshot.");
        if (run.DetectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
            && page.DetectionArtifactName is null)
        {
            throw new InvalidDataException(
                $"Browser-only translation run {context.RunId} has no pinned detection checkpoint for page {page.Index}.");
        }
        var standardArtifactName = LiveTranslationWorkflow.DetectionArtifactName(page.PageId, detector.CacheKey);
        var artifactName = page.DetectionArtifactName ?? standardArtifactName;
        var existing = await LoadOptionalAsync<LiveTranslationWorkflow.LiveDetectionCheckpoint>(
            context.ProjectId,
            artifactName,
            cancellationToken);
        if (existing is not null
            && (run.DetectionArtifactPolicy != DetectionArtifactPolicy.BrowserOnly
                || IsBrowserCraftCheckpoint(existing, detector.Descriptor))
            && await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
                assets,
                context.ProjectId,
                existing,
                page.SourceAssetKey,
                page.SourceSha256,
                page.PageId,
                run.DetectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                    ? null
                    : detector.ModelSha256,
                detector.Descriptor,
                cancellationToken))
        {
            await context.ReportAsync("Reusing project detection, mask, and clean-image assets.", existing.Provenance, cancellationToken);
            return existing;
        }
        if (LiveTranslationWorkflow.ShouldFailClosedForInvalidPinnedDetection(
                run.DetectionArtifactPolicy,
                page.DetectionArtifactName))
        {
            throw new InvalidDataException(
                $"Pinned detection checkpoint {page.DetectionArtifactName} is missing or invalid.");
        }

        if (string.Equals(detector.DetectorId, TextDetectorIds.Ctd, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(run.PipelineCacheRunId)
            && pipelineArtifacts.TryGet(run.PipelineCacheRunId, out var pipeline)
            && pipeline!.Pages.FirstOrDefault(item => item.PageIndex == page.Index) is { } cachedPage
            && !string.IsNullOrWhiteSpace(cachedPage.DetectionJsonPath)
            && await WorkflowCacheFingerprint.PipelineArtifactMatchesAsync(
                cachedPage,
                page.SourceSha256,
                cancellationToken))
        {
            var cached = JsonSerializer.Deserialize<LiveTranslationWorkflow.CachedDetectionArtifact>(
                await PlatformIoPath.ReadAllTextAsync(cachedPage.DetectionJsonPath, cancellationToken),
                JsonOptions) ?? throw new InvalidDataException($"Unable to read cached detection for page {page.Index}.");
            var regions = LiveTranslationWorkflow.CreateDialogueRegions(
                page.PageId,
                cached.Regions.Select(item => item.Region).ToArray());
            var imported = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
                page.SourceAssetKey,
                page.PageId,
                regions,
                $"workflow-cache/{page.PageId}/pipeline/mask.png",
                $"workflow-cache/{page.PageId}/pipeline/clean.png",
                $"pipeline:{pipeline.RunId}",
                WorkflowCacheFingerprint.PipelineDetection(
                    page.SourceSha256,
                    cachedPage.DetectionContentHash!,
                    cachedPage.MaskContentHash!,
                    cachedPage.CleanContentHash!,
                    page.PageId,
                    LiveTranslationWorkflow.HashRegions(regions)));
            await CopyFileToAssetAsync(context.ProjectId, imported.MaskAssetKey, cachedPage.MaskImagePath, cancellationToken);
            await CopyFileToAssetAsync(context.ProjectId, imported.CleanAssetKey, cachedPage.CleanImagePath, cancellationToken);
            await SaveAsync(context.ProjectId, standardArtifactName, imported, cancellationToken);
            await context.ReportAsync("Imported reusable detection and cleaning assets.", imported.Provenance, cancellationToken);
            return imported;
        }

        var modelPath = detector.ModelPath
            ?? throw new InvalidOperationException("No reusable detection asset or local text detector model is available.");
        await context.ReportAsync("Running local text detection and mask generation.", Path.GetFileName(modelPath), cancellationToken);
        var hash = detector.ModelSha256
            ?? throw new InvalidOperationException("The detector model hash is unavailable.");
        var detected = await detectorRuntime.DetectAsync(
            detector,
            new DetectionInput(source.Data, source.MediaType),
            cancellationToken);
        if (detected.Width != page.Width || detected.Height != page.Height)
        {
            throw new InvalidDataException(
                $"Text detector output dimensions {detected.Width}x{detected.Height} do not match project page "
                + $"dimensions {page.Width}x{page.Height}.");
        }

        var maskAssetKey = $"workflow-cache/{page.PageId}/{detector.CacheKey}/mask.png";
        var cleanAssetKey = $"workflow-cache/{page.PageId}/{detector.CacheKey}/clean.png";
        var encodedMask = LiveTranslationWorkflow.EncodeMaskPng(detected.Mask);
        await SaveBytesAsync(context.ProjectId, maskAssetKey, encodedMask, cancellationToken);
        var cleaner = new OpenCvTextCleaner();
        var cleaned = await cleaner.CleanAsync(
            new TextCleaningRequest(
                source.Data,
                detected.Mask,
                new ImageClip(0, 0, page.Width, page.Height),
                source.MediaType),
            cancellationToken);
        await SaveBytesAsync(context.ProjectId, cleanAssetKey, cleaned.ImageBytes, cancellationToken);
        var dialogueRegions = LiveTranslationWorkflow.CreateDialogueRegions(page.PageId, detected.Regions);
        var generated = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
            page.SourceAssetKey,
            page.PageId,
            dialogueRegions,
            maskAssetKey,
            cleanAssetKey,
            $"local-model:{detector.Descriptor.DetectorId}:{hash[..16]}",
            WorkflowCacheFingerprint.Detection(
                page.SourceSha256,
                hash,
                WorkflowCacheFingerprint.HashBytes(encodedMask),
                WorkflowCacheFingerprint.HashBytes(cleaned.ImageBytes.Span),
                page.PageId,
                detector.Descriptor,
                LiveTranslationWorkflow.HashRegions(dialogueRegions)));
        await SaveAsync(context.ProjectId, standardArtifactName, generated, cancellationToken);
        return generated;
    }

    private async Task<string?> FindReusableDetectionArtifactNameAsync(
        string projectId,
        QuickPageInput page,
        IReadOnlyDictionary<string, string> artifactMap,
        PipelineArtifactDescriptor? pipeline,
        TextDetectorSelection detector,
        DetectionArtifactPolicy detectionArtifactPolicy,
        CancellationToken cancellationToken)
    {
        var artifactName = LiveTranslationWorkflow.DetectionArtifactName(
            page.PageId,
            detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                ? BrowserDetectionArtifactService.BrowserLatestCacheKey
                : detector.CacheKey);
        if (artifactMap.TryGetValue(artifactName, out var json))
        {
            var checkpoint = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDetectionCheckpoint>(json, JsonOptions);
            if (checkpoint is not null
                && (detectionArtifactPolicy != DetectionArtifactPolicy.BrowserOnly
                    || IsBrowserCraftCheckpoint(checkpoint, detector.Descriptor))
                && await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
                    assets,
                    projectId,
                    checkpoint,
                    page.SourceAssetKey,
                    page.SourceSha256,
                    page.PageId,
                    detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                        ? null
                        : detector.ModelSha256,
                    detector.Descriptor,
                    cancellationToken))
            {
                var immutableName = BrowserDetectionArtifactName(page.PageId, checkpoint);
                if (immutableName is null) return artifactName;
                if (artifactMap.TryGetValue(immutableName, out var immutableJson))
                {
                    if (!StringComparer.Ordinal.Equals(json, immutableJson))
                    {
                        throw new InvalidDataException(
                            $"Browser detection alias {artifactName} does not match immutable checkpoint {immutableName}.");
                    }
                    return immutableName;
                }
                if (!StringComparer.Ordinal.Equals(
                        checkpoint.Fingerprint.ContractVersion,
                        WorkflowCacheFingerprint.LegacyDetectionContractVersion))
                {
                    throw new InvalidDataException(
                        $"Browser detection alias {artifactName} points to missing immutable checkpoint {immutableName}.");
                }
                return await MaterializeLegacyBrowserCheckpointAsync(
                    projectId,
                    page.PageId,
                    artifactName,
                    checkpoint,
                    cancellationToken);
            }
        }

        if (detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly)
        {
            return null;
        }

        var cached = string.Equals(detector.DetectorId, TextDetectorIds.Ctd, StringComparison.OrdinalIgnoreCase)
            ? pipeline?.Pages.FirstOrDefault(item => item.PageIndex == page.Index)
            : null;
        return cached is not null
               && await WorkflowCacheFingerprint.PipelineArtifactMatchesAsync(
                   cached,
                   page.SourceSha256,
                   cancellationToken)
            ? artifactName
            : null;
    }

    private static bool IsBrowserCraftCheckpoint(
        LiveTranslationWorkflow.LiveDetectionCheckpoint checkpoint,
        TextMaskDetectorDescriptor detector) =>
        string.Equals(detector.DetectorId, TextDetectorIds.Craft, StringComparison.OrdinalIgnoreCase)
        && checkpoint.Provenance.StartsWith("browser-model:craft:", StringComparison.OrdinalIgnoreCase);

    private TextDetectorSelection CreateBrowserOnlyDetectorSelection(string detectorId)
    {
        var normalizedDetectorId = TextDetectorIds.Normalize(detectorId);
        if (!string.Equals(normalizedDetectorId, TextDetectorIds.Craft, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Browser-only detection currently supports only {TextDetectorIds.Craft}.");
        }

        return new TextDetectorSelection(
            TextDetectorIds.Craft,
            detectorRuntime.GetDescriptor(TextDetectorIds.Craft),
            null,
            null);
    }

    private static string? BrowserDetectionArtifactName(
        string pageId,
        LiveTranslationWorkflow.LiveDetectionCheckpoint checkpoint) =>
        checkpoint.Fingerprint is { Sha256.Length: > 0 } fingerprint
        && checkpoint.Provenance.StartsWith("browser-model:", StringComparison.OrdinalIgnoreCase)
            ? LiveTranslationWorkflow.DetectionArtifactName(
                pageId,
                BrowserDetectionArtifactService.BrowserCheckpointKey(fingerprint.Sha256))
            : null;

    private async Task<string> MaterializeLegacyBrowserCheckpointAsync(
        string projectId,
        string pageId,
        string aliasArtifactName,
        LiveTranslationWorkflow.LiveDetectionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var immutableName = BrowserDetectionArtifactName(pageId, checkpoint)
            ?? throw new InvalidOperationException("The checkpoint is not a browser detection checkpoint.");
        var checkpointKey = BrowserDetectionArtifactService.BrowserCheckpointKey(
            checkpoint.Fingerprint.Sha256);
        var maskAssetKey = $"workflow-cache/{pageId}/{checkpointKey}/mask.png";
        var cleanAssetKey = $"workflow-cache/{pageId}/{checkpointKey}/clean.png";
        await CopyAssetAsync(projectId, checkpoint.MaskAssetKey, maskAssetKey, cancellationToken);
        await CopyAssetAsync(projectId, checkpoint.CleanAssetKey, cleanAssetKey, cancellationToken);
        var migrated = checkpoint with
        {
            MaskAssetKey = maskAssetKey,
            CleanAssetKey = cleanAssetKey
        };
        await SaveAsync(projectId, immutableName, migrated, cancellationToken);
        await SaveAsync(projectId, aliasArtifactName, migrated, cancellationToken);
        return immutableName;
    }

    private async Task CopyAssetAsync(
        string projectId,
        string sourceAssetKey,
        string destinationAssetKey,
        CancellationToken cancellationToken)
    {
        await using var source = await assets.OpenReadAsync(
            projectId,
            sourceAssetKey,
            cancellationToken);
        await assetWriter.SaveAsync(
            projectId,
            destinationAssetKey,
            source.Content,
            cancellationToken);
    }

    private async Task<LiveTranslationWorkflow.SourceImage> ReadSourceAsync(
        string projectId,
        QuickPageInput page,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, page.SourceAssetKey, cancellationToken);
        using var buffer = new MemoryStream();
        await asset.Content.CopyToAsync(buffer, cancellationToken);
        return new LiveTranslationWorkflow.SourceImage(asset.MediaType, buffer.ToArray(), page.Width, page.Height);
    }

    private async Task<string> ComputeAssetSha256Async(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, assetKey, cancellationToken);
        return Convert.ToHexString(await SHA256.HashDataAsync(asset.Content, cancellationToken)).ToLowerInvariant();
    }

    private static WorkflowStageFingerprint CreateTranslationFingerprint(
        QuickRunDocument run,
        QuickPageInput page,
        LiveTranslationWorkflow.SourceImage source,
        LiveTranslationWorkflow.LiveDetectionCheckpoint detection,
        WorkflowModelSettings settings) =>
        WorkflowCacheFingerprint.Translation(
            "quick",
            page.SourceSha256,
            detection.Fingerprint.Sha256,
            run.TargetLanguage,
            settings.ProviderId,
            settings.Endpoint.AbsoluteUri,
            settings.ModelId,
            DialogueContractVersions.QuickProfile,
            DialogueContractVersions.Prompt,
            DialogueContractVersions.Schema,
            run.FontSetSha256,
            WorkflowCacheFingerprint.HashText("quick:no-book-knowledge"),
            WorkflowCacheFingerprint.HashJson(page.Guidance),
            WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(new
            {
                page.PageId,
                page.Index,
                page.PageType,
                page.Width,
                page.Height,
                source.MediaType
            }, JsonOptions)));

    private async Task CopyFileToAssetAsync(string projectId, string assetKey, string sourcePath, CancellationToken cancellationToken)
    {
        await using var source = PlatformIoPath.OpenRead(
            sourcePath,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await assetWriter.SaveAsync(projectId, assetKey, source, cancellationToken);
    }

    private async Task SaveBytesAsync(string projectId, string assetKey, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(data.ToArray(), writable: false);
        await assetWriter.SaveAsync(projectId, assetKey, stream, cancellationToken);
    }

    private async Task SaveAsync<T>(string projectId, string name, T value, CancellationToken cancellationToken) =>
        await artifacts.SaveAsync(projectId, name, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private async Task<T?> LoadOptionalAsync<T>(string projectId, string name, CancellationToken cancellationToken)
    {
        var artifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return artifact is null ? default : JsonSerializer.Deserialize<T>(artifact.Json, JsonOptions);
    }

    private async Task<T> LoadRequiredAsync<T>(string projectId, string name, CancellationToken cancellationToken) =>
        await LoadOptionalAsync<T>(projectId, name, cancellationToken)
        ?? throw new InvalidOperationException($"Quick-mode run artifact is missing: {name}.");

    private async Task<QuickRunDocument> LoadRunAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken)
    {
        var run = await LoadRequiredAsync<QuickRunDocument>(projectId, name, cancellationToken);
        if (run.Detector is not null)
        {
            _ = detectorRuntime.GetDescriptor(run.Detector.DetectorId);
            return run;
        }

        var detector = await detectorRuntime.ResolveAsync(
            TextDetectorIds.Ctd,
            requireModel: false,
            cancellationToken);
        var migrated = run with
        {
            SchemaVersion = "1.2.0",
            Detector = detector,
            DetectionArtifactPolicy = DetectionArtifactPolicy.LocalOrReusable
        };
        await SaveAsync(projectId, name, migrated, cancellationToken);
        return migrated;
    }


    private static JsonElement CreateGuidance(string? annotation, JsonObject? manualLettering)
    {
        var guidance = new JsonObject();
        if (!string.IsNullOrWhiteSpace(annotation))
        {
            guidance["user_annotation"] = annotation.Trim();
            guidance["user_annotation_instruction"] = "Use this as page-specific guidance and do not invent off-page context.";
        }
        if (manualLettering is not null)
        {
            guidance["user_lettering_overrides"] = manualLettering.DeepClone();
            guidance["user_lettering_instruction"] = "Preserve every protected user-adjusted field and user-created segment.";
        }
        using var document = JsonDocument.Parse(guidance.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static GlobalSemanticMergeResult EmptyKnowledge(string targetLanguage)
    {
        using var arrays = JsonDocument.Parse("[]");
        using var objects = JsonDocument.Parse("{}");
        return new GlobalSemanticMergeResult(
            targetLanguage,
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            objects.RootElement.Clone());
    }

    private static string RunArtifactName(string runId) => $"quick_translation_run_{Sanitize(runId)}.json";
    private static string DialogueArtifactName(string runId, string pageId) => $"quick_translation_dialogue_{Sanitize(runId)}_{Sanitize(pageId)}.json";
    private static string PageCheckpointArtifactName(string runId, string pageId) => $"quick_translation_page_{Sanitize(runId)}_{Sanitize(pageId)}.json";
    private static string Sanitize(string value) => new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record QuickRunDocument(
        string SchemaVersion,
        string RunId,
        string ProjectId,
        TextDetectorSelection? Detector,
        DetectionArtifactPolicy DetectionArtifactPolicy,
        string TargetLanguage,
        IReadOnlyList<string> EnabledFontIds,
        string FontSetSha256,
        IReadOnlyList<QuickPageInput> Pages,
        string? PipelineCacheRunId);

    private sealed record QuickPageInput(
        string PageId,
        int Index,
        string PageType,
        string SourceAssetKey,
        string SourceSha256,
        int Width,
        int Height,
        JsonElement Guidance,
        string? DetectionArtifactName = null);

    private sealed record QuickPreparedPage(
        TranslationPageExecutionContext Context,
        QuickRunDocument Run,
        QuickPageInput Page,
        WorkflowModelSettings Settings,
        LiveTranslationWorkflow.SourceImage Source,
        LiveTranslationWorkflow.LiveDetectionCheckpoint Detection,
        WorkflowStageFingerprint TranslationFingerprint,
        WorkflowStageFingerprint PageFingerprint,
        DialogueTranslationRequest? Request,
        DialogueTranslationResult? Dialogue,
        TranslationPageExecutionResult? CompletedResult);

    internal sealed record QuickDialogueCheckpoint(
        WorkflowStageFingerprint Fingerprint,
        DialogueTranslationResult Result);

    internal sealed record QuickPageCheckpoint(
        WorkflowStageFingerprint Fingerprint,
        TranslationPageExecutionResult Result);
}
