using System.Text.Json;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.Api.Projects;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.LocalDetection;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public sealed class CachedTranslationExecutionSource(
    IProjectStore projects,
    IProjectAssetWriter assetWriter,
    IProjectResultService resultService,
    IPipelineArtifactCatalog pipelineArtifacts,
    TranslationAnnotationService annotations,
    LayoutAwareCleaningService layoutCleaning,
    IApplicationSettingsStore applicationSettings) : ITranslationExecutionSource
{
    public ExecutionMode Mode => ExecutionMode.Cached;

    public async Task<TranslationExecutionPlan> CreateAsync(
        StartTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        PipelineArtifactDescriptor? pipeline = null;
        if (!string.IsNullOrWhiteSpace(request.FixtureRunId))
        {
            pipelineArtifacts.TryGet(request.FixtureRunId, out pipeline);
        }

        pipeline ??= pipelineArtifacts.MatchProject(project);
        if (pipeline is null)
        {
            throw new InvalidOperationException(
                "当前项目与任何可复用的真实流水线产物都不匹配。请配置 EMT_PIPELINE_CACHE_ROOT，或改用实时执行模式。");
        }

        var availablePageIndices = pipeline.Pages.Select(item => item.PageIndex).ToHashSet();
        var requestedPageIndices = request.PageIndices?.ToHashSet();
        var pages = project.Pages
            .Where(page => page.IncludedInWorkflow
                           && availablePageIndices.Contains(page.Index)
                           && (requestedPageIndices is null || requestedPageIndices.Contains(page.Index)))
            .OrderBy(page => page.Index)
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("项目中没有同时被纳入工作流且具备真实 AI 产物的页面。");
        }

        var annotationCount = (await annotations.GetAsync(request.ProjectId, cancellationToken))
            .Pages.Count(item => !string.IsNullOrWhiteSpace(item.Value));
        var settings = await applicationSettings.GetAsync(cancellationToken);
        return new TranslationExecutionPlan(
            Mode,
            pipeline.RunId,
            pages,
            annotationCount,
            new TranslationTaskSettingsResponse(
                settings.TranslationBatchConcurrency,
                settings.TranslationBatchSize,
                TranslationInputResolution.Original));
    }

    public async Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse settings,
        CancellationToken cancellationToken)
    {
        var results = new List<TranslationBatchExecutionResult>(contexts.Count);
        foreach (var context in contexts)
        {
            results.Add(new TranslationBatchExecutionResult(
                context.PageIndex,
                await ExecutePageAsync(context, cancellationToken)));
        }
        return results;
    }

    private async Task<TranslationPageExecutionResult> ExecutePageAsync(
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!pipelineArtifacts.TryGet(context.RunId, out var pipeline))
        {
            throw new InvalidOperationException($"找不到流水线缓存 {context.RunId}。");
        }

        var cachedPage = pipeline!.Pages.FirstOrDefault(item => item.PageIndex == context.PageIndex)
            ?? throw new InvalidOperationException($"流水线缓存中没有第 {context.PageIndex} 页的完整结果。");
        var project = await projects.GetAsync(context.ProjectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.Index == context.PageIndex)
            ?? throw new ProjectPageNotFoundException(context.ProjectId, $"page-{context.PageIndex:0000}");
        await context.ReportAsync(
            "正在读取已验证的页面流水线产物。",
            $"缓存：{pipeline.RunId}",
            cancellationToken);

        var cachedLayout = await PlatformIoPath.ReadAllTextAsync(cachedPage.DialogueJsonPath, cancellationToken);
        var currentLayout = await projects.GetPageLayoutAsync(context.ProjectId, page.PageId, cancellationToken);
        var manualLettering = ManualLetteringOverrides.CreateContext(
            currentLayout.LayoutJson,
            currentLayout.HasUnversionedUserChanges);
        var layout = ManualLetteringOverrides.Apply(cachedLayout, manualLettering);
        var renderedAssetKey = $"rendered/{page.PageId}/{context.JobId}.png";
        var candidateMaskAssetKey = LayoutAwareCleaningService.CandidateMaskAssetKey(page.PageId);
        await CopyAssetAsync(context.ProjectId, candidateMaskAssetKey, cachedPage.MaskImagePath, cancellationToken);
        var effectiveCleaning = await layoutCleaning.RebuildAsync(
            context.ProjectId,
            page,
            page.SourceAssetKey,
            candidateMaskAssetKey,
            layout,
            $"clean/{page.PageId}/{context.JobId}",
            cancellationToken);
        await context.ReportAsync(
            "底图和 Mask 已就绪，正在生成正式嵌字结果。",
            $"page: {context.PageIndex}",
            cancellationToken);
        await using (var rendered = await resultService.RenderPageAsync(
                         context.ProjectId,
                         page.PageId,
                         effectiveCleaning.CleanAssetKey,
                         layout,
                         cancellationToken))
        {
            await assetWriter.SaveAsync(
                context.ProjectId,
                renderedAssetKey,
                rendered.Content,
                cancellationToken);
        }

        using var layoutDocument = JsonDocument.Parse(layout);
        var segmentCount = layoutDocument.RootElement.TryGetProperty("segments", out var segments)
                           && segments.ValueKind == JsonValueKind.Array
            ? segments.GetArrayLength()
            : 0;
        var needsPriorityReview = NeedsPriorityReview(layoutDocument.RootElement);
        return new TranslationPageExecutionResult(
            layout,
            effectiveCleaning.CleanAssetKey,
            effectiveCleaning.MaskAssetKey,
            renderedAssetKey,
            $"第 {context.PageIndex} 页已完成翻译、清字和嵌字。",
            $"复用真实 AI 布局 {segmentCount} 段；已写入 Mask、底图和结果图。"
            + (manualLettering is null ? string.Empty : "已合并用户手调嵌字约束。")
            + $"缓存：{pipeline.RunId}",
            needsPriorityReview,
            candidateMaskAssetKey);
    }

    public static bool NeedsPriorityReview(JsonElement root)
    {
        if (TryReadBoolean(root, "needs_priority_review")) return true;
        if (!root.TryGetProperty("segments", out var segments)
            || segments.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        return segments.EnumerateArray().Any(segment =>
            segment.ValueKind is JsonValueKind.Object
            && TryReadBoolean(segment, "needs_priority_review"));
    }

    private static bool TryReadBoolean(JsonElement value, string propertyName) =>
        value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True;

    private async Task CopyAssetAsync(
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
}

public sealed class LiveTranslationExecutionSource(
    IProjectStore projects,
    IApplicationSettingsStore applicationSettings,
    ILiveTranslationWorkflow expertWorkflow,
    IQuickTranslationWorkflow quickWorkflow) : ITranslationExecutionSource
{
    public ExecutionMode Mode => ExecutionMode.Live;

    public async Task<TranslationExecutionPlan> CreateAsync(
        StartTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var quick = project.WorkflowMode == MangaTranslator.ProjectSystem.ProjectWorkflowMode.Quick;
        var generatedRunId = $"{(quick ? "quick" : "expert")}-translation-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var runId = string.IsNullOrWhiteSpace(request.FixtureRunId)
            ? generatedRunId[..Math.Min(64, generatedRunId.Length)]
            : request.FixtureRunId.Trim();
        var appSettings = await applicationSettings.GetAsync(cancellationToken);
        var (detectorId, detectionArtifactPolicy) = ResolveDetectionSelection(
            appSettings.LocalTextDetectionEngine);
        var preparation = quick
            ? await quickWorkflow.PrepareAsync(
                request.ProjectId,
                runId,
                detectorId,
                detectionArtifactPolicy,
                request.PageIndices,
                cancellationToken)
            : await expertWorkflow.PrepareAsync(
                request.ProjectId,
                runId,
                detectorId,
                detectionArtifactPolicy,
                request.PageIndices,
                cancellationToken);
        return new TranslationExecutionPlan(
            Mode,
            runId,
            preparation.Pages,
            preparation.AnnotationCount,
            new TranslationTaskSettingsResponse(
                appSettings.TranslationBatchConcurrency,
                appSettings.TranslationBatchSize,
                TranslationInputResolution.Original));
    }

    public Task<IReadOnlyList<TranslationBatchExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<TranslationPageExecutionContext> contexts,
        TranslationTaskSettingsResponse settings,
        CancellationToken cancellationToken) =>
        contexts.Count > 0
        && contexts[0].RunId.StartsWith("quick-translation-", StringComparison.OrdinalIgnoreCase)
            ? quickWorkflow.ExecuteBatchAsync(contexts, settings, cancellationToken)
            : expertWorkflow.ExecuteBatchAsync(contexts, settings, cancellationToken);

    internal static (string DetectorId, DetectionArtifactPolicy ArtifactPolicy) ResolveDetectionSelection(
        LocalTextDetectionEngine engine) => engine switch
        {
            LocalTextDetectionEngine.Ctd => (
                TextDetectorIds.Ctd,
                DetectionArtifactPolicy.LocalOrReusable),
            LocalTextDetectionEngine.Craft => (
                TextDetectorIds.Craft,
                DetectionArtifactPolicy.LocalOrReusable),
            LocalTextDetectionEngine.BrowserCraft => (
                TextDetectorIds.Craft,
                DetectionArtifactPolicy.BrowserOnly),
            _ => throw new InvalidOperationException(
                $"Unknown local text detection engine: {engine}.")
        };
}
