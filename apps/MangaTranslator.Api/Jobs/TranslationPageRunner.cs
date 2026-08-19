using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public sealed class TranslationPageRunner(
    IProjectStore projects,
    IProjectEditLock editLock,
    ProjectOperationGate operationGate) : ITranslationPageRunner
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> CommitGates = new(StringComparer.Ordinal);

    public async Task<TranslationPagePipelineOutcome> RunAsync(
        TranslationPageExecutionContext context,
        TranslationPageExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (editLock.IsLocked(context.ProjectId))
        {
            throw new InvalidOperationException("项目正在导出，不能写入新的 AI 页面结果。");
        }
        var project = await projects.GetAsync(context.ProjectId, cancellationToken);
        var page = project.Pages.FirstOrDefault(item => item.Index == context.PageIndex)
            ?? throw new ProjectPageNotFoundException(
                context.ProjectId,
                $"page-{context.PageIndex:0000}");
        var versions = await projects.ListPageVersionsAsync(
            context.ProjectId,
            page.PageId,
            cancellationToken);
        if (versions.Any(item =>
                item.Track == PageVersionTrack.Lettering
                && item.Kind == PageVersionKind.Ai
                && string.Equals(item.LayoutJson, result.LayoutJson, StringComparison.Ordinal)
                && string.Equals(item.CleanAssetKey, result.CleanAssetKey, StringComparison.Ordinal)))
        {
            return new TranslationPagePipelineOutcome(
                $"第 {context.PageIndex} 页已恢复完成。",
                "检测到同一任务已经创建的 AI 页面版本，直接复用已有检查点。");
        }
        var label = VersionLabel(versions);

        var commitGate = CommitGates.GetOrAdd(context.ProjectId, static _ => new SemaphoreSlim(1, 1));
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            using var operation = await operationGate.EnterAsync(
                context.ProjectId,
                cancellationToken);
            if (editLock.IsLocked(context.ProjectId))
            {
                throw new InvalidOperationException(
                    "The project became locked for export before the AI result could be committed.");
            }
            var committed = await CommitAiVersionAsync(
                context,
                page.PageId,
                label,
                result,
                cancellationToken);
            await ApplyReviewTagAsync(
                context.ProjectId,
                page.PageId,
                result.NeedsPriorityReview ? PageReviewTag.Priority : PageReviewTag.Unreviewed,
                committed.Revision,
                cancellationToken);
        }
        finally
        {
            commitGate.Release();
        }
        return new TranslationPagePipelineOutcome(result.ResponseSummary, result.Detail);
    }

    public async Task<TranslationPagePipelineOutcome> RunAsync(
        ITranslationExecutionSource source,
        TranslationPageExecutionContext context,
        CancellationToken cancellationToken) => await RunAsync(
            context,
            await source.ExecutePageAsync(context, cancellationToken),
            cancellationToken);

    private async Task<ProjectSnapshot> CommitAiVersionAsync(
        TranslationPageExecutionContext context,
        string pageId,
        string label,
        TranslationPageExecutionResult result,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var project = await projects.GetAsync(context.ProjectId, cancellationToken);
            var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
                ?? throw new ProjectPageNotFoundException(context.ProjectId, pageId);
            var versions = await projects.ListPageVersionsAsync(
                context.ProjectId,
                pageId,
                cancellationToken);
            if (versions.Any(version =>
                    version.Track == PageVersionTrack.Lettering
                    && version.IsCurrent
                    && version.IsWorkingCopy))
            {
                project = await projects.CreatePageVersionAsync(
                    context.ProjectId,
                    pageId,
                    new CreatePageVersionCommand(
                        $"自动保护嵌字 · 第 {context.PageIndex} 页",
                        project.Revision,
                        PageVersionKind.User,
                        Track: PageVersionTrack.Lettering),
                    cancellationToken);
            }

            try
            {
                project = await projects.CreatePageVersionAsync(
                    context.ProjectId,
                    pageId,
                    new CreatePageVersionCommand(
                        label,
                        project.Revision,
                        PageVersionKind.Ai,
                        LayoutJson: result.LayoutJson,
                        CleanAssetKey: result.CleanAssetKey,
                        MaskAssetKey: result.MaskAssetKey,
                        RenderedAssetKey: result.RenderedAssetKey,
                        Track: PageVersionTrack.Lettering,
                        CandidateMaskAssetKey: result.CandidateMaskAssetKey),
                    cancellationToken);
                return project;
            }
            catch (ProjectRevisionConflictException) when (attempt < 10)
            {
                // A user edit landed while the page was being committed. Re-read and
                // protect that edit before retrying the AI version switch.
            }
        }

        throw new ProjectRevisionConflictException(
            (await projects.GetAsync(context.ProjectId, cancellationToken)).Revision);
    }

    private async Task ApplyReviewTagAsync(
        string projectId,
        string pageId,
        PageReviewTag reviewTag,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await projects.UpdatePageReviewTagAsync(
                    projectId,
                    pageId,
                    new UpdatePageReviewTagCommand(reviewTag, expectedRevision),
                    cancellationToken);
                return;
            }
            catch (ProjectRevisionConflictException) when (attempt < 10)
            {
                expectedRevision = (await projects.GetAsync(projectId, cancellationToken)).Revision;
            }
        }

        throw new ProjectRevisionConflictException(
            (await projects.GetAsync(projectId, cancellationToken)).Revision);
    }

    private static string VersionLabel(IReadOnlyList<PageVersionSnapshot> versions) =>
        $"AI 生成 {versions.Count(item => item.Track == PageVersionTrack.Lettering && item.Kind == PageVersionKind.Ai) + 1}";
}
