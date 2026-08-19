using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;
using MangaTranslator.ProjectSystem;
using SkiaSharp;

namespace MangaTranslator.Api.Jobs;

public sealed record LiveTranslationPreparation(
    IReadOnlyList<ProjectPageSnapshot> Pages,
    int AnnotationCount);

public interface ILiveTranslationWorkflow
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

public sealed class LiveTranslationWorkflow(
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
    LayoutAwareCleaningService layoutCleaning) : ILiveTranslationWorkflow
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
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var project = await projects.GetAsync(projectId, cancellationToken);
        var requestedPageIndices = pageIndices?.ToHashSet();
        var selectedPages = project.Pages
            .Where(page => page.IncludedInWorkflow
                           && (requestedPageIndices is null || requestedPageIndices.Contains(page.Index)))
            .OrderBy(page => page.Index)
            .ToArray();
        if (selectedPages.Length == 0)
        {
            throw new InvalidOperationException("项目中没有被纳入翻译工作流的页面。");
        }

        await modelSettings.ResolveAsync(ModelStageId, cancellationToken);
        var artifactMap = (await artifacts.ListAsync(projectId, cancellationToken))
            .ToDictionary(item => item.Name, item => item.Json, StringComparer.OrdinalIgnoreCase);
        var semantic = ReadRequired<GlobalSemanticMergeResult>(artifactMap, "semantic_merge.json");
        var bookManifest = ReadRequired<BookManifestDocument>(artifactMap, "book_manifest.json");
        var reviewedKnowledge = ApplyReviewedKnowledge(
            semantic,
            artifactMap.GetValueOrDefault("characters.json"),
            artifactMap.GetValueOrDefault("glossary.json"));
        var autoReview = ReadOptional<AutoReviewResult>(artifactMap, "auto_review.json");
        var processingHints = autoReview?.FinalMerge.ProcessingHints ?? EmptyObject();
        var annotationSnapshot = await annotations.GetAsync(projectId, cancellationToken);
        var manifestByOrder = bookManifest.Pages.ToDictionary(item => item.Order);
        var runPages = new List<LiveTranslationPageInput>(selectedPages.Length);
        foreach (var page in selectedPages)
        {
            var manifestPage = manifestByOrder.GetValueOrDefault(page.Index);
            var currentLayout = await projects.GetPageLayoutAsync(projectId, page.PageId, cancellationToken);
            var manualLettering = ManualLetteringOverrides.CreateContext(
                currentLayout.LayoutJson,
                currentLayout.HasUnversionedUserChanges);
            runPages.Add(new LiveTranslationPageInput(
                page.PageId,
                manifestPage?.PageId ?? page.PageId,
                page.Index,
                manifestPage?.ChapterId,
                manifestPage?.SceneId ?? $"page-{page.Index:0000}",
                page.PageType,
                page.IncludedInWorkflow,
                page.SourceAssetKey,
                await ComputeAssetSha256Async(projectId, page.SourceAssetKey, cancellationToken),
                page.FileName,
                page.Width,
                page.Height,
                CreatePageGuidance(
                    processingHints,
                    manifestPage?.PageId ?? page.PageId,
                    annotationSnapshot.Pages.GetValueOrDefault(page.PageId),
                    manualLettering)));
        }
        var pipeline = string.Equals(detectorId, TextDetectorIds.Ctd, StringComparison.OrdinalIgnoreCase)
            ? pipelineArtifacts.MatchProject(project)
            : null;

        var fontRoot = fonts.LibraryRoot;
        IFontReferenceImageProvider fontReference = new ProjectFontReferenceProvider(
            new FileSystemFontReferenceImageProvider(fontRoot),
            project.EnabledFontIds);
        var fontSnapshot = await fontReference.GetFontReferenceImageAsync(
            new FontReferenceImageRequest(
                bookManifest.BookId,
                runPages[0].DialoguePageId,
                project.TargetLanguage),
            cancellationToken);
        var fontSetSha256 = WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(new
        {
            fontSnapshot.LibraryRevision,
            Fonts = fontSnapshot.Fonts.OrderBy(item => item.FontId, StringComparer.Ordinal)
        }, JsonOptions));
        var bookPageIndex = bookManifest.Pages
            .OrderBy(item => item.Order)
            .Select(item => new DialogueBookPageIndexEntry(item.PageId, item.Order))
            .ToArray();
        var knowledgeSha256 = WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(new
        {
            Knowledge = reviewedKnowledge,
            BookPageIndex = bookPageIndex
        }, JsonOptions));

        var detector = detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
            ? CreateBrowserOnlyDetectorSelection(detectorId)
            : await detectorRuntime.ResolveAsync(detectorId, requireModel: false, cancellationToken);
        var missingBrowserPages = new List<int>();
        var needsDetectorModel = false;
        for (var index = 0; index < runPages.Count; index++)
        {
            var page = runPages[index];
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
                runPages[index] = page with { DetectionArtifactName = reusableArtifactName };
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
                "部分页面没有可复用的文字检测与清字产物，同时未找到本地文字检测模型。"
                + $" Engine: {detector.DetectorId}; model path: {detectorRuntime.DefaultInstallPath(detector.DetectorId)};"
                + $" or set {detectorRuntime.ModelConfigurationHint(detector.DetectorId)}.");
        }

        var document = new LiveTranslationRunDocument(
            "1.2.0",
            runId,
            projectId,
            detector,
            detectionArtifactPolicy,
            bookManifest.BookId,
            project.TargetLanguage,
            project.EnabledFontIds,
            fontSetSha256,
            knowledgeSha256,
            reviewedKnowledge,
            bookPageIndex,
            runPages,
            pipeline?.RunId);
        await SaveAsync(
            projectId,
            RunArtifactName(runId),
            document,
            cancellationToken);
        return new LiveTranslationPreparation(
            selectedPages,
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
                "文字区域与底图已就绪，正在调用图片模型完成识别、翻译和布局。",
                string.IsNullOrWhiteSpace(prepared.Page.Annotation)
                    ? $"regions: {prepared.Detection.Regions.Count}"
                    : $"regions: {prepared.Detection.Regions.Count}; 已附加本页批注",
                cancellationToken);
            using var httpClient = httpClientFactory.CreateClient("workflow-model-provider");
            var provider = CreateModelProvider(httpClient, prepared.Settings);
            var translator = new ModelBackedMultimodalDialogueTranslator(provider);
            prepared = prepared with
            {
                Dialogue = await translator.TranslateAsync(prepared.Request!, cancellationToken)
            };
            await SaveDialogueAsync(prepared, cancellationToken);
        }
        else
        {
            await context.ReportAsync(
                "模型结果已经存在，正在从页面级检查点恢复。",
                $"segments: {prepared.Dialogue.Segments.Count}",
                cancellationToken);
        }
        return await FinalizePageAsync(prepared, prepared.Dialogue!, cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse batchSettings,
        CancellationToken cancellationToken)
    {
        var prepared = new List<ExpertPreparedPage>(contexts.Count);
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
                $"正在用一次图片模型请求处理 {prepared.Count} 个已选页面。",
                $"batch pages: {string.Join(", ", prepared.Select(item => item.Page.Index))}；复用全书预处理上下文",
                cancellationToken);
        }
        using var httpClient = httpClientFactory.CreateClient("workflow-model-provider");
        var provider = CreateModelProvider(httpClient, prepared[0].Settings);
        var dialogues = await TranslationBatchModelInvoker.TranslateAsync(
            provider,
            ExpertDialoguePromptProvider.Instance,
            prepared.Select(item => item.Request!).ToArray(),
            batchSettings.InputResolution,
            cancellationToken);
        var completedPages = prepared.Select(page => page with
        {
            Dialogue = dialogues.Single(item => string.Equals(
                item.PageId,
                page.Page.DialoguePageId,
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

    private async Task<ExpertPreparedPage> PreparePageAsync(
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(
            context.ProjectId,
            RunArtifactName(context.RunId),
            cancellationToken);
        var page = run.Pages.FirstOrDefault(item => item.Index == context.PageIndex)
            ?? throw new InvalidOperationException(
                $"实时翻译运行 {context.RunId} 不包含第 {context.PageIndex} 页。");

        var completed = await LoadOptionalAsync<LiveTranslationPageCheckpoint>(
            context.ProjectId,
            PageCheckpointArtifactName(context.RunId, page.ProjectPageId),
            cancellationToken);
        var settings = await modelSettings.ResolveAsync(ModelStageId, cancellationToken);
        var source = await ReadSourceAsync(context.ProjectId, page, cancellationToken);
        var detection = await GetDetectionAsync(
            context,
            run,
            page,
            source,
            cancellationToken);
        var translationFingerprint = CreateTranslationFingerprint(
            run,
            page,
            source,
            detection,
            settings,
            DialogueContractVersions.ExpertProfile);
        var pageFingerprint = WorkflowCacheFingerprint.Page(detection.Fingerprint, translationFingerprint);
        if (completed is not null
            && WorkflowCacheFingerprint.MatchesValid(pageFingerprint, completed.Fingerprint)
            && await assets.ExistsAsync(context.ProjectId, completed.CleanAssetKey, cancellationToken)
            && await assets.ExistsAsync(context.ProjectId, completed.MaskAssetKey, cancellationToken)
            && await assets.ExistsAsync(context.ProjectId, completed.RenderedAssetKey, cancellationToken))
        {
            await context.ReportAsync(
                "正在复用本次运行已经完成的页面检查点。",
                $"run: {context.RunId}; page: {page.DialoguePageId}",
                cancellationToken);
            return new ExpertPreparedPage(
                context, run, page, settings, source, detection,
                translationFingerprint, pageFingerprint, null, null, completed.Result);
        }

        var dialogueArtifactName = DialogueArtifactName(context.RunId, page.ProjectPageId);
        var dialogueCheckpoint = await LoadOptionalAsync<LiveDialogueCheckpoint>(
            context.ProjectId,
            dialogueArtifactName,
            cancellationToken);
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
                    context.RunId + "-" + page.DialoguePageId,
                    run.BookId,
                    new DialoguePageInput(
                        page.DialoguePageId,
                        page.Index,
                        page.ChapterId,
                        page.SceneId,
                        page.PageType,
                        page.IncludedInWorkflow,
                        page.Width,
                        page.Height,
                        source.MediaType,
                        detection.Regions),
                    run.BookKnowledge,
                    new SinglePageImageProvider(page.DialoguePageId, source),
                    fontReference,
                    new DialogueTranslationOptions
                    {
                        Model = settings.ModelId,
                        TargetLanguage = run.TargetLanguage
                    },
                    page.Guidance)
                {
                    BookPageIndex = run.BookPageIndex
                };
        return new ExpertPreparedPage(
            context, run, page, settings, source, detection,
            translationFingerprint, pageFingerprint, request, dialogue, null);
    }

    private async Task SaveDialogueAsync(ExpertPreparedPage prepared, CancellationToken cancellationToken) =>
        await SaveAsync(
            prepared.Context.ProjectId,
            DialogueArtifactName(prepared.Context.RunId, prepared.Page.ProjectPageId),
            new LiveDialogueCheckpoint(prepared.TranslationFingerprint, prepared.Dialogue!),
            cancellationToken);

    private async Task<TranslationPageExecutionResult> FinalizePageAsync(
        ExpertPreparedPage prepared,
        DialogueTranslationResult dialogue,
        CancellationToken cancellationToken)
    {
        var context = prepared.Context;
        var page = prepared.Page;
        var detection = prepared.Detection;
        var manualLettering = ManualLetteringOverrides.ReadContext(page.Guidance);
        var layoutJson = ManualLetteringOverrides.Apply(
            JsonSerializer.Serialize(dialogue, JsonOptions),
            manualLettering);
        var projectPage = (await projects.GetAsync(context.ProjectId, cancellationToken)).Pages
            .Single(item => item.PageId == page.ProjectPageId);
        var effectiveCleaning = await layoutCleaning.RebuildAsync(
            context.ProjectId,
            projectPage,
            page.SourceAssetKey,
            detection.MaskAssetKey,
            layoutJson,
            $"clean/{page.ProjectPageId}/{context.JobId}",
            cancellationToken);
        var renderedAssetKey = $"rendered/{page.ProjectPageId}/{context.JobId}.png";
        await context.ReportAsync(
            "AI 布局已完成，正在基于清字底图执行正式嵌字。",
            $"segments: {dialogue.Segments.Count}",
            cancellationToken);
        await using (var rendered = await resultService.RenderPageAsync(
                         context.ProjectId,
                         page.ProjectPageId,
                         effectiveCleaning.CleanAssetKey,
                         layoutJson,
                         cancellationToken))
        {
            await assetWriter.SaveAsync(
                context.ProjectId,
                renderedAssetKey,
                rendered.Content,
                cancellationToken);
        }

        var result = new TranslationPageExecutionResult(
            layoutJson,
            effectiveCleaning.CleanAssetKey,
            effectiveCleaning.MaskAssetKey,
            renderedAssetKey,
            $"第 {context.PageIndex} 页已完成实时翻译、清字和嵌字。",
            $"检测区域 {detection.Regions.Count} 个，生成文本段 {dialogue.Segments.Count} 个；"
            + (manualLettering is null ? string.Empty : "已合并用户手调嵌字约束；")
            + $"检测来源：{detection.Provenance}；模型：{dialogue.Model}。",
            dialogue.NeedsPriorityReview,
            detection.MaskAssetKey);
        await SaveAsync(
            context.ProjectId,
            PageCheckpointArtifactName(context.RunId, page.ProjectPageId),
            new LiveTranslationPageCheckpoint(prepared.PageFingerprint, result),
            cancellationToken);
        return result;
    }

    private async Task<LiveDetectionCheckpoint> GetDetectionAsync(
        TranslationPageExecutionContext context,
        LiveTranslationRunDocument run,
        LiveTranslationPageInput page,
        SourceImage source,
        CancellationToken cancellationToken)
    {
        var detector = run.Detector
            ?? throw new InvalidOperationException(
                $"Translation run {context.RunId} has no text detector snapshot.");
        if (run.DetectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
            && page.DetectionArtifactName is null)
        {
            throw new InvalidDataException(
                $"Browser-only translation run {context.RunId} has no pinned detection checkpoint for page {page.Index}.");
        }
        var standardArtifactName = DetectionArtifactName(page.ProjectPageId, detector.CacheKey);
        var artifactName = page.DetectionArtifactName ?? standardArtifactName;
        var existing = await LoadOptionalAsync<LiveDetectionCheckpoint>(
            context.ProjectId,
            artifactName,
            cancellationToken);
        if (existing is not null
            && (run.DetectionArtifactPolicy != DetectionArtifactPolicy.BrowserOnly
                || IsBrowserCraftCheckpoint(existing, detector.Descriptor))
            && await DetectionCheckpointMatchesAsync(
                assets,
                context.ProjectId,
                existing,
                page.SourceAssetKey,
                page.SourceSha256,
                page.DialoguePageId,
                run.DetectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                    ? null
                    : detector.ModelSha256,
                detector.Descriptor,
                cancellationToken))
        {
            await context.ReportAsync(
                "正在复用项目内已持久化的文字检测、Mask 和清字底图。",
                existing.Provenance,
                cancellationToken);
            return existing;
        }
        if (ShouldFailClosedForInvalidPinnedDetection(
                run.DetectionArtifactPolicy,
                page.DetectionArtifactName))
        {
            throw new InvalidDataException(
                $"Pinned detection checkpoint {page.DetectionArtifactName} is missing or invalid.");
        }

        // Local/reusable runs may pin the standard artifact name because an external
        // pipeline cache was valid during preparation. That artifact is imported only
        // below, so a missing project checkpoint here must fall through to pipeline
        // import or local detection instead of aborting the translation.

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
            await context.ReportAsync(
                "正在导入稳定流水线中的文字检测与清字结果。",
                $"cache: {pipeline.RunId}",
                cancellationToken);
            var cached = JsonSerializer.Deserialize<CachedDetectionArtifact>(
                             await PlatformIoPath.ReadAllTextAsync(cachedPage.DetectionJsonPath, cancellationToken),
                             JsonOptions)
                         ?? throw new InvalidDataException(
                             $"无法读取第 {page.Index} 页的检测缓存。");
            var regions = CreateDialogueRegions(
                page.DialoguePageId,
                cached.Regions.Select(item => item.Region).ToArray());
            var imported = new LiveDetectionCheckpoint(
                page.SourceAssetKey,
                page.DialoguePageId,
                regions,
                $"workflow-cache/{page.ProjectPageId}/pipeline/mask.png",
                $"workflow-cache/{page.ProjectPageId}/pipeline/clean.png",
                $"pipeline:{pipeline.RunId}",
                WorkflowCacheFingerprint.PipelineDetection(
                    page.SourceSha256,
                    cachedPage.DetectionContentHash!,
                    cachedPage.MaskContentHash!,
                    cachedPage.CleanContentHash!,
                    page.DialoguePageId,
                    HashRegions(regions)));
            await CopyFileToAssetAsync(
                context.ProjectId,
                imported.MaskAssetKey,
                cachedPage.MaskImagePath,
                cancellationToken);
            await CopyFileToAssetAsync(
                context.ProjectId,
                imported.CleanAssetKey,
                cachedPage.CleanImagePath,
                cancellationToken);
            await SaveAsync(context.ProjectId, standardArtifactName, imported, cancellationToken);
            return imported;
        }

        var modelPath = detector.ModelPath
            ?? throw new InvalidOperationException(
                "没有可复用的文字检测产物，且未找到本地文字检测模型。"
                + $" Engine: {detector.DetectorId}; model path: {detectorRuntime.DefaultInstallPath(detector.DetectorId)};"
                + $" or set {detectorRuntime.ModelConfigurationHint(detector.DetectorId)}.");
        await context.ReportAsync(
            "正在运行本地文字检测并生成 Mask。",
            Path.GetFileName(modelPath),
            cancellationToken);
        var modelHash = detector.ModelSha256
            ?? throw new InvalidOperationException("The detector model hash is unavailable.");
        var detected = await detectorRuntime.DetectAsync(
            detector,
            new DetectionInput(source.Data, source.MediaType),
            cancellationToken);
        if (detected.Width != page.Width || detected.Height != page.Height)
        {
            throw new InvalidDataException(
                $"文字检测输出尺寸 {detected.Width}x{detected.Height} 与项目页面 "
                + $"{page.Width}x{page.Height} 不一致。");
        }

        var maskAssetKey = $"workflow-cache/{page.ProjectPageId}/{detector.CacheKey}/mask.png";
        var cleanAssetKey = $"workflow-cache/{page.ProjectPageId}/{detector.CacheKey}/clean.png";
        var encodedMask = EncodeMaskPng(detected.Mask);
        await SaveBytesAsync(
            context.ProjectId,
            maskAssetKey,
            encodedMask,
            cancellationToken);
        await context.ReportAsync(
            "Mask 已生成，正在执行自动清字。",
            $"regions: {detected.Regions.Count}",
            cancellationToken);
        var cleaner = new OpenCvTextCleaner();
        var cleaned = await cleaner.CleanAsync(
            new TextCleaningRequest(
                source.Data,
                detected.Mask,
                new ImageClip(0, 0, page.Width, page.Height),
                source.MediaType),
            cancellationToken);
        await SaveBytesAsync(
            context.ProjectId,
            cleanAssetKey,
            cleaned.ImageBytes,
            cancellationToken);
        var dialogueRegions = CreateDialogueRegions(page.DialoguePageId, detected.Regions);
        var result = new LiveDetectionCheckpoint(
            page.SourceAssetKey,
            page.DialoguePageId,
            dialogueRegions,
            maskAssetKey,
            cleanAssetKey,
            $"local-model:{detector.Descriptor.DetectorId}:{modelHash[..16]}",
            WorkflowCacheFingerprint.Detection(
                page.SourceSha256,
                modelHash,
                WorkflowCacheFingerprint.HashBytes(encodedMask),
                WorkflowCacheFingerprint.HashBytes(cleaned.ImageBytes.Span),
                page.DialoguePageId,
                detector.Descriptor,
                HashRegions(dialogueRegions)));
        await SaveAsync(context.ProjectId, standardArtifactName, result, cancellationToken);
        return result;
    }

    private async Task<string?> FindReusableDetectionArtifactNameAsync(
        string projectId,
        LiveTranslationPageInput page,
        IReadOnlyDictionary<string, string> artifactMap,
        PipelineArtifactDescriptor? pipeline,
        TextDetectorSelection detector,
        DetectionArtifactPolicy detectionArtifactPolicy,
        CancellationToken cancellationToken)
    {
        var artifactName = DetectionArtifactName(
            page.ProjectPageId,
            detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                ? BrowserDetectionArtifactService.BrowserLatestCacheKey
                : detector.CacheKey);
        if (artifactMap.TryGetValue(artifactName, out var json))
        {
            var checkpoint = JsonSerializer.Deserialize<LiveDetectionCheckpoint>(json, JsonOptions);
            if (checkpoint is not null
                && (detectionArtifactPolicy != DetectionArtifactPolicy.BrowserOnly
                    || IsBrowserCraftCheckpoint(checkpoint, detector.Descriptor))
                && await DetectionCheckpointMatchesAsync(
                    assets,
                    projectId,
                    checkpoint,
                    page.SourceAssetKey,
                    page.SourceSha256,
                    page.DialoguePageId,
                    detectionArtifactPolicy == DetectionArtifactPolicy.BrowserOnly
                        ? null
                        : detector.ModelSha256,
                    detector.Descriptor,
                    cancellationToken))
            {
                var immutableName = BrowserDetectionArtifactName(page.ProjectPageId, checkpoint);
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
                    page.ProjectPageId,
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
        LiveDetectionCheckpoint checkpoint,
        TextMaskDetectorDescriptor detector) =>
        string.Equals(detector.DetectorId, TextDetectorIds.Craft, StringComparison.OrdinalIgnoreCase)
        && checkpoint.Provenance.StartsWith("browser-model:craft:", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldFailClosedForInvalidPinnedDetection(
        DetectionArtifactPolicy policy,
        string? pinnedArtifactName) =>
        policy == DetectionArtifactPolicy.BrowserOnly
        && !string.IsNullOrWhiteSpace(pinnedArtifactName);

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
        LiveDetectionCheckpoint checkpoint) =>
        checkpoint.Fingerprint is { Sha256.Length: > 0 } fingerprint
        && checkpoint.Provenance.StartsWith("browser-model:", StringComparison.OrdinalIgnoreCase)
            ? DetectionArtifactName(
                pageId,
                BrowserDetectionArtifactService.BrowserCheckpointKey(fingerprint.Sha256))
            : null;

    private async Task<string> MaterializeLegacyBrowserCheckpointAsync(
        string projectId,
        string pageId,
        string aliasArtifactName,
        LiveDetectionCheckpoint checkpoint,
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

    private async Task<SourceImage> ReadSourceAsync(
        string projectId,
        LiveTranslationPageInput page,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(
            projectId,
            page.SourceAssetKey,
            cancellationToken);
        using var buffer = new MemoryStream();
        await asset.Content.CopyToAsync(buffer, cancellationToken);
        return new SourceImage(
            asset.MediaType,
            buffer.ToArray(),
            page.Width,
            page.Height);
    }

    private async Task<string> ComputeAssetSha256Async(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, assetKey, cancellationToken);
        return Convert.ToHexString(await SHA256.HashDataAsync(asset.Content, cancellationToken)).ToLowerInvariant();
    }

    internal static async Task<bool> DetectionCheckpointMatchesAsync(
        IProjectAssetStore assets,
        string projectId,
        LiveDetectionCheckpoint checkpoint,
        string sourceAssetKey,
        string sourceSha256,
        string dialoguePageId,
        string? currentDetectorModelSha256,
        TextMaskDetectorDescriptor currentDetector,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Fingerprint is null
            || !string.Equals(checkpoint.SourceAssetKey, sourceAssetKey, StringComparison.Ordinal)
            || !string.Equals(checkpoint.DialoguePageId, dialoguePageId, StringComparison.Ordinal)
            || !await assets.ExistsAsync(projectId, checkpoint.MaskAssetKey, cancellationToken)
            || !await assets.ExistsAsync(projectId, checkpoint.CleanAssetKey, cancellationToken))
        {
            return false;
        }

        var maskSha256 = await ComputeAssetSha256Async(
            assets,
            projectId,
            checkpoint.MaskAssetKey,
            cancellationToken);
        var cleanSha256 = await ComputeAssetSha256Async(
            assets,
            projectId,
            checkpoint.CleanAssetKey,
            cancellationToken);
        if (StringComparer.Ordinal.Equals(
                checkpoint.Fingerprint.ContractVersion,
                WorkflowCacheFingerprint.DetectionContractVersion))
        {
            var regionsSha256 = HashRegions(checkpoint.Regions);
            if (!checkpoint.Fingerprint.Inputs.TryGetValue("regions_sha256", out var expectedRegionsSha256)
                || !StringComparer.OrdinalIgnoreCase.Equals(expectedRegionsSha256, regionsSha256))
            {
                return false;
            }
        }
        return WorkflowCacheFingerprint.DetectionMatches(
            checkpoint.Fingerprint,
            sourceSha256,
            dialoguePageId,
            currentDetectorModelSha256,
            maskSha256,
            cleanSha256,
            checkpoint.Provenance.StartsWith("pipeline:", StringComparison.OrdinalIgnoreCase),
            currentDetector);
    }

    private static async Task<string> ComputeAssetSha256Async(
        IProjectAssetStore assets,
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        await using var asset = await assets.OpenReadAsync(projectId, assetKey, cancellationToken);
        return Convert.ToHexString(await SHA256.HashDataAsync(asset.Content, cancellationToken)).ToLowerInvariant();
    }

    internal static string HashRegions(IReadOnlyList<DialogueDetectedRegion> regions) =>
        WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(regions, JsonOptions));

    private static WorkflowStageFingerprint CreateTranslationFingerprint(
        LiveTranslationRunDocument run,
        LiveTranslationPageInput page,
        SourceImage source,
        LiveDetectionCheckpoint detection,
        WorkflowModelSettings settings,
        string promptProfile) =>
        WorkflowCacheFingerprint.Translation(
            "expert",
            page.SourceSha256,
            detection.Fingerprint.Sha256,
            run.TargetLanguage,
            settings.ProviderId,
            settings.Endpoint.AbsoluteUri,
            settings.ModelId,
            promptProfile,
            DialogueContractVersions.Prompt,
            DialogueContractVersions.Schema,
            run.FontSetSha256,
            run.KnowledgeSha256,
            WorkflowCacheFingerprint.HashJson(page.Guidance),
            WorkflowCacheFingerprint.HashText(JsonSerializer.Serialize(new
            {
                page.ProjectPageId,
                page.DialoguePageId,
                page.Index,
                page.ChapterId,
                page.SceneId,
                page.PageType,
                page.Width,
                page.Height,
                source.MediaType,
                run.BookPageIndex
            }, JsonOptions)));

    private async Task CopyFileToAssetAsync(
        string projectId,
        string assetKey,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var source = PlatformIoPath.OpenRead(
            sourcePath,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await assetWriter.SaveAsync(projectId, assetKey, source, cancellationToken);
    }

    private async Task SaveBytesAsync(
        string projectId,
        string assetKey,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        await assetWriter.SaveAsync(projectId, assetKey, stream, cancellationToken);
    }

    private async Task SaveAsync<T>(
        string projectId,
        string name,
        T value,
        CancellationToken cancellationToken) =>
        await artifacts.SaveAsync(
            projectId,
            name,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken);

    private async Task<T> LoadRequiredAsync<T>(
        string projectId,
        string name,
        CancellationToken cancellationToken) =>
        await LoadOptionalAsync<T>(projectId, name, cancellationToken)
        ?? throw new InvalidOperationException($"项目中缺少实时翻译运行产物 {name}。");

    private async Task<LiveTranslationRunDocument> LoadRunAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken)
    {
        var run = await LoadRequiredAsync<LiveTranslationRunDocument>(
            projectId,
            name,
            cancellationToken);
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


    private async Task<T?> LoadOptionalAsync<T>(
        string projectId,
        string name,
        CancellationToken cancellationToken)
    {
        var artifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return artifact is null
            ? default
            : JsonSerializer.Deserialize<T>(artifact.Json, JsonOptions);
    }

    private static T ReadRequired<T>(
        IReadOnlyDictionary<string, string> artifactMap,
        string name) =>
        ReadOptional<T>(artifactMap, name)
        ?? throw new InvalidOperationException(
            $"项目中缺少 {name}。请先完成预处理并审阅页面范围。");

    private static T? ReadOptional<T>(
        IReadOnlyDictionary<string, string> artifactMap,
        string name) =>
        artifactMap.TryGetValue(name, out var json)
            ? JsonSerializer.Deserialize<T>(json, JsonOptions)
            : default;

    private static GlobalSemanticMergeResult ApplyReviewedKnowledge(
        GlobalSemanticMergeResult source,
        string? charactersJson,
        string? glossaryJson) =>
        source with
        {
            Characters = ApplyCharacterReview(source.Characters, charactersJson),
            Glossary = ApplyGlossaryReview(source.Glossary, glossaryJson)
        };

    private static JsonElement ApplyCharacterReview(JsonElement source, string? reviewJson)
    {
        if (string.IsNullOrWhiteSpace(reviewJson)) return source.Clone();
        var review = JsonNode.Parse(reviewJson) as JsonObject
            ?? throw new InvalidDataException("characters.json 不是 JSON 对象。");
        var items = review["characters"] as JsonArray
            ?? throw new InvalidDataException("characters.json 缺少 characters 数组。");
        var values = items
            .OfType<JsonObject>()
            .Where(item => item["mergeKey"]?.GetValue<string>() is not null)
            .ToDictionary(
                item => item["mergeKey"]!.GetValue<string>(),
                item => item["value"]?.GetValue<string>() ?? string.Empty,
                StringComparer.Ordinal);
        var characters = JsonNode.Parse(source.GetRawText()) as JsonArray
            ?? throw new InvalidDataException("semantic_merge.json 的 characters 不是数组。");
        foreach (var character in characters.OfType<JsonObject>().ToArray())
        {
            var key = character["character_id"]?.GetValue<string>();
            if (key is null || !values.TryGetValue(key, out var translatedName))
            {
                characters.Remove(character);
                continue;
            }

            character["translated_name"] = translatedName;
        }

        return ToElement(characters);
    }

    private static JsonElement ApplyGlossaryReview(JsonElement source, string? reviewJson)
    {
        if (string.IsNullOrWhiteSpace(reviewJson)) return source.Clone();
        var review = JsonNode.Parse(reviewJson) as JsonObject
            ?? throw new InvalidDataException("glossary.json 不是 JSON 对象。");
        var items = review["terms"] as JsonArray
            ?? throw new InvalidDataException("glossary.json 缺少 terms 数组。");
        var values = items
            .OfType<JsonObject>()
            .Where(item => item["mergeKey"]?.GetValue<string>() is not null)
            .ToDictionary(
                item => item["mergeKey"]!.GetValue<string>(),
                item => item["value"]?.GetValue<string>() ?? string.Empty,
                StringComparer.Ordinal);
        var glossary = JsonNode.Parse(source.GetRawText()) as JsonArray
            ?? throw new InvalidDataException("semantic_merge.json 的 glossary 不是数组。");
        foreach (var term in glossary.OfType<JsonObject>().ToArray())
        {
            var sourceText = term["source"]?.GetValue<string>() ?? string.Empty;
            if (!values.TryGetValue(StableKey(sourceText), out var translation))
            {
                glossary.Remove(term);
                continue;
            }

            term["translation"] = translation;
        }

        return ToElement(glossary);
    }

    private static JsonElement CreatePageGuidance(
        JsonElement processingHints,
        string pageId,
        string? annotation,
        JsonObject? manualLettering)
    {
        JsonObject result = new();
        if (processingHints.ValueKind == JsonValueKind.Object
            && TryGetProperty(processingHints, "pages", out var pages)
            && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                if (page.ValueKind == JsonValueKind.Object
                    && TryGetProperty(page, "pageId", out var candidate)
                    && candidate.ValueKind == JsonValueKind.String
                    && string.Equals(candidate.GetString(), pageId, StringComparison.Ordinal))
                {
                    result = JsonNode.Parse(page.GetRawText()) as JsonObject ?? new JsonObject();
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(annotation))
        {
            result["user_annotation"] = annotation.Trim();
            result["user_annotation_instruction"] =
                "Treat user_annotation as supplemental page-specific guidance. Do not invent facts beyond the visible page and book knowledge.";
        }

        if (manualLettering is not null)
        {
            result["user_lettering_overrides"] = manualLettering.DeepClone();
            result["user_lettering_instruction"] =
                "Preserve every field marked in user_lettering_overrides. Keep user-created segments, omit user-deleted segment IDs, and only regenerate fields without a user override.";
        }

        return ToElement(result);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static DialogueDetectedRegion[] CreateDialogueRegions(
        string pageId,
        IReadOnlyList<DetectedTextRegion> regions) =>
        regions
            .Select((region, detectionIndex) => new { detectionIndex, region })
            .OrderBy(item => item.region.Y0)
            .ThenBy(item => item.region.X0)
            .ThenBy(item => item.region.Y1)
            .ThenBy(item => item.region.X1)
            .ThenBy(item => item.detectionIndex)
            .Select((item, stableIndex) => new DialogueDetectedRegion(
                $"{pageId}:region:{stableIndex + 1:0000}",
                item.detectionIndex,
                item.region))
            .ToArray();

    internal static byte[] EncodeMaskPng(MaskImage mask)
    {
        var pixels = mask.Pixels.ToArray();
        using var bitmap = new SKBitmap(new SKImageInfo(
            mask.Width,
            mask.Height,
            SKColorType.Gray8,
            SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp 无法编码文字 Mask。");
        return data.ToArray();
    }

    internal static IModelProvider CreateModelProvider(
        HttpClient httpClient,
        WorkflowModelSettings settings) => ModelProviderFactory.Create(httpClient, settings);

    private static string StableKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "term_" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static JsonElement EmptyObject() => ToElement(new JsonObject());

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static string RunArtifactName(string runId) =>
        $"translation_run_{Sanitize(runId)}.json";

    internal static string DetectionArtifactName(string pageId, string detectorCacheKey) =>
        $"translation_detection_{Sanitize(pageId)}_{Sanitize(detectorCacheKey)}.json";

    private static string DialogueArtifactName(string runId, string pageId) =>
        $"translation_dialogue_{Sanitize(runId)}_{Sanitize(pageId)}.json";

    private static string PageCheckpointArtifactName(string runId, string pageId) =>
        $"translation_page_{Sanitize(runId)}_{Sanitize(pageId)}.json";

    private static string Sanitize(string value) =>
        new(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_').ToArray());

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

    private sealed record LiveTranslationRunDocument(
        string SchemaVersion,
        string RunId,
        string ProjectId,
        TextDetectorSelection? Detector,
        DetectionArtifactPolicy DetectionArtifactPolicy,
        string BookId,
        string TargetLanguage,
        IReadOnlyList<string> EnabledFontIds,
        string FontSetSha256,
        string KnowledgeSha256,
        GlobalSemanticMergeResult BookKnowledge,
        IReadOnlyList<DialogueBookPageIndexEntry> BookPageIndex,
        IReadOnlyList<LiveTranslationPageInput> Pages,
        string? PipelineCacheRunId);

    private sealed record LiveTranslationPageInput(
        string ProjectPageId,
        string DialoguePageId,
        int Index,
        string? ChapterId,
        string SceneId,
        string PageType,
        bool IncludedInWorkflow,
        string SourceAssetKey,
        string SourceSha256,
        string FileName,
        int Width,
        int Height,
        JsonElement Guidance,
        string? DetectionArtifactName = null)
    {
        [JsonIgnore]
        public string? Annotation =>
            Guidance.ValueKind == JsonValueKind.Object
            && Guidance.TryGetProperty("user_annotation", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private sealed record ExpertPreparedPage(
        TranslationPageExecutionContext Context,
        LiveTranslationRunDocument Run,
        LiveTranslationPageInput Page,
        WorkflowModelSettings Settings,
        SourceImage Source,
        LiveDetectionCheckpoint Detection,
        WorkflowStageFingerprint TranslationFingerprint,
        WorkflowStageFingerprint PageFingerprint,
        DialogueTranslationRequest? Request,
        DialogueTranslationResult? Dialogue,
        TranslationPageExecutionResult? CompletedResult);

    internal sealed record LiveDetectionCheckpoint(
        string SourceAssetKey,
        string DialoguePageId,
        IReadOnlyList<DialogueDetectedRegion> Regions,
        string MaskAssetKey,
        string CleanAssetKey,
        string Provenance,
        WorkflowStageFingerprint Fingerprint);

    internal sealed record LiveDialogueCheckpoint(
        WorkflowStageFingerprint Fingerprint,
        DialogueTranslationResult Result);

    internal sealed record LiveTranslationPageCheckpoint(
        WorkflowStageFingerprint Fingerprint,
        TranslationPageExecutionResult Result)
    {
        public string CleanAssetKey => Result.CleanAssetKey;
        public string MaskAssetKey => Result.MaskAssetKey;
        public string RenderedAssetKey => Result.RenderedAssetKey;
    }

    internal sealed record CachedDetectionArtifact(
        IReadOnlyList<DialogueDetectedRegion> Regions);

    internal sealed record SourceImage(
        string MediaType,
        ReadOnlyMemory<byte> Data,
        int Width,
        int Height);

    internal sealed class SinglePageImageProvider(
        string pageId,
        SourceImage source) : IPageImageProvider
    {
        public ValueTask<DialogueImageAsset> GetOriginalPageAsync(
            string requestedPageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(pageId, requestedPageId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"页面图像提供器只包含 {pageId}，请求的是 {requestedPageId}。");
            }

            return ValueTask.FromResult(new DialogueImageAsset(
                $"original:{pageId}",
                source.MediaType,
                source.Data,
                source.Width,
                source.Height));
        }
    }
}
