using System.Globalization;
using System.Text.Json;
using MangaTranslator.Api.Fixtures;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public class ReplayPrecheckExecutionSource(
    IPrecheckFixtureCatalog fixtures,
    ILogger<ReplayPrecheckExecutionSource> logger) : IPrecheckExecutionSource
{
    public virtual ExecutionMode Mode => ExecutionMode.Replay;

    public virtual bool DrivesExecution => false;

    public virtual async Task<PrecheckExecutionPlan> CreateAsync(
        string projectId,
        string? fixtureRunId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        cancellationToken.ThrowIfCancellationRequested();

        var fixture = ResolveFixture(fixtureRunId);
        var plan = await Task.Run(() => ReadPlan(fixture), cancellationToken);
        return Mode == ExecutionMode.Cached
            ? plan with { ExecutionMode = Mode, AlreadyCompleted = true }
            : plan with { ExecutionMode = Mode };
    }

    public virtual Task ExecuteAsync(
        PrecheckExecutionContext context,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected PrecheckFixtureDescriptor ResolveFixture(string? fixtureRunId)
    {
        if (string.IsNullOrWhiteSpace(fixtureRunId))
        {
            return fixtures.GetDefault();
        }

        if (fixtures.TryGet(fixtureRunId, out var fixture))
        {
            return fixture!;
        }

        throw new InvalidOperationException($"找不到预处理 Fixture：{fixtureRunId}");
    }

    protected PrecheckExecutionPlan ReadPlan(PrecheckFixtureDescriptor fixture)
    {
        try
        {
            using var manifest = OpenJson(fixture, "source_manifest.json");
            using var thumbnail = OpenJson(fixture, "thumbnail_precheck.json");
            using var batchPlan = OpenJson(fixture, "batch_plan.json");

            var pages = ReadPages(manifest.RootElement);
            var batches = ReadBatches(batchPlan.RootElement);
            var relationCount = ReadArrayLength(thumbnail.RootElement, "relationCandidates");
            var batchCost = batches.Sum(batch => batch.EstimatedCost);
            var allPages = pages.Count == 0
                ? Enumerable.Range(1, fixture.PageCount).ToArray()
                : pages.Select(page => page.Index).ToArray();
            var batchPages = batches.SelectMany(batch => batch.OwnerPages).Distinct().Order().ToArray();

            var events = new List<PrecheckExecutionEvent>
            {
                new(
                    "manifest",
                    "manifest",
                    "生成 Book Manifest",
                    "扫描页面文件并建立稳定的页面身份。",
                    $"Manifest 已建立：{pages.Count} 页。",
                    $"pages: {pages.Count}; fixture: {fixture.RunId}",
                    null,
                    allPages),
                new(
                    "thumbnail",
                    "thumbnail",
                    "完成缩略图预检",
                    "识别页面类型，并整理跨页关系候选。",
                    $"识别 {pages.Count} 页，发现 {relationCount} 条跨页候选。",
                    $"relation_candidates: {relationCount}",
                    null,
                    allPages),
                new(
                    "planning",
                    "planning",
                    "规划分析批次",
                    "结合上下文、硬关系与预算生成批次计划。",
                    $"已生成 {batches.Count} 个批次，预计成本 {batchCost.ToString("0.##", CultureInfo.InvariantCulture)}。",
                    $"batches: {batches.Count}; estimated_cost: {batchCost.ToString("0.##", CultureInfo.InvariantCulture)}",
                    null,
                    batchPages)
            };

            events.AddRange(batches.Select(batch => new PrecheckExecutionEvent(
                batch.BatchId,
                "batch-analysis",
                $"分析 {batch.BatchId}",
                $"分析第 {FormatPages(batch.OwnerPages)} 页，并带入前后文页面。",
                $"{batch.BatchId} 完成：{batch.OwnerPages.Count} 个主页面。",
                $"owner_pages: [{FormatPages(batch.OwnerPages)}]; context_before: [{FormatPages(batch.ContextPagesBefore)}]; context_after: [{FormatPages(batch.ContextPagesAfter)}]; priority: {batch.Priority}; estimated_cost: {batch.EstimatedCost.ToString("0.##", CultureInfo.InvariantCulture)}",
                "batch-analysis",
                batch.OwnerPages.Count == 0 ? allPages : batch.OwnerPages)));

            events.Add(new PrecheckExecutionEvent(
                "consolidation",
                "consolidation",
                "整合全局预处理结果",
                "合并批次结果，并检查角色、术语和跨页关系的一致性。",
                "全局结果已整合，可进入审阅页面。",
                $"review_artifacts: {fixture.AvailableArtifacts.Count}; pages: {pages.Count}",
                null,
                allPages));

            return new PrecheckExecutionPlan(Mode, fixture.RunId, events);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Invalid JSON in precheck fixture {FixtureRunId}.", fixture.RunId);
            throw new InvalidOperationException(
                $"Fixture {fixture.RunId} 的预处理产物无法解析，已避免触发真实预处理。",
                exception);
        }
    }

    private static JsonDocument OpenJson(PrecheckFixtureDescriptor fixture, string fileName) =>
        JsonDocument.Parse(PlatformIoPath.ReadAllText(fixture.GetArtifactPath(fileName)));

    private static List<FixturePage> ReadPages(JsonElement root)
    {
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FixturePage>();
        var fallbackIndex = 1;
        foreach (var page in pages.EnumerateArray())
        {
            var id = ReadString(page, "pageId") ?? $"p{fallbackIndex:0000}";
            var index = ReadInt(page, "sourceIndex") ?? PageIndex(id) ?? fallbackIndex;
            result.Add(new FixturePage(id, index));
            fallbackIndex++;
        }

        return result;
    }

    private static List<FixtureBatch> ReadBatches(JsonElement root)
    {
        if (!root.TryGetProperty("batches", out var batches) || batches.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FixtureBatch>();
        var fallbackIndex = 1;
        foreach (var batch in batches.EnumerateArray())
        {
            var id = ReadString(batch, "batchId") ?? $"batch_{fallbackIndex:000}";
            result.Add(new FixtureBatch(
                id,
                ReadPageIndices(batch, "ownerPages"),
                ReadPageIndices(batch, "contextPagesBefore"),
                ReadPageIndices(batch, "contextPagesAfter"),
                ReadString(batch, "priority") ?? "normal",
                ReadDouble(batch, "estimatedCost") ?? 0));
            fallbackIndex++;
        }

        return result;
    }

    private static IReadOnlyList<int> ReadPageIndices(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetInt32(out var number) => number,
                JsonValueKind.String => PageIndex(item.GetString() ?? string.Empty) ?? 0,
                _ => 0
            })
            .Where(index => index > 0)
            .ToArray();
    }

    private static int ReadArrayLength(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string FormatPages(IReadOnlyList<int> pages) =>
        pages.Count == 0 ? "无" : string.Join(", ", pages.Select(index => index.ToString(CultureInfo.InvariantCulture)));

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetDouble(out var result)
            ? result
            : null;

    private static int? PageIndex(string pageId)
    {
        var digits = new string(pageId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private sealed record FixturePage(string Id, int Index);

    private sealed record FixtureBatch(
        string BatchId,
        IReadOnlyList<int> OwnerPages,
        IReadOnlyList<int> ContextPagesBefore,
        IReadOnlyList<int> ContextPagesAfter,
        string Priority,
        double EstimatedCost);
}

public sealed class CachedPrecheckExecutionSource(
    IPrecheckFixtureCatalog fixtures,
    ILogger<ReplayPrecheckExecutionSource> logger,
    IProjectStore projects,
    IPipelineArtifactCatalog pipelineArtifacts)
    : ReplayPrecheckExecutionSource(fixtures, logger)
{
    public override ExecutionMode Mode => ExecutionMode.Cached;

    public override async Task<PrecheckExecutionPlan> CreateAsync(
        string projectId,
        string? fixtureRunId,
        CancellationToken cancellationToken)
    {
        PipelineArtifactDescriptor? pipeline = null;
        if (!string.IsNullOrWhiteSpace(fixtureRunId))
        {
            pipelineArtifacts.TryGet(fixtureRunId, out pipeline);
        }

        pipeline ??= pipelineArtifacts.MatchProject(
            await projects.GetAsync(projectId, cancellationToken));
        if (pipeline is null)
        {
            return await base.CreateAsync(projectId, fixtureRunId, cancellationToken);
        }

        return ReadPlan(pipeline.PrecheckFixture) with
        {
            ExecutionMode = ExecutionMode.Cached,
            FixtureRunId = pipeline.RunId,
            AlreadyCompleted = true
        };
    }
}

public sealed class LivePrecheckExecutionSource(ILivePrecheckWorkflow workflow) : IPrecheckExecutionSource
{
    public ExecutionMode Mode => ExecutionMode.Live;

    public bool DrivesExecution => true;

    public async Task<PrecheckExecutionPlan> CreateAsync(
        string projectId,
        string? fixtureRunId,
        CancellationToken cancellationToken)
    {
        await workflow.ValidateAsync(projectId, cancellationToken);
        var runId = string.IsNullOrWhiteSpace(fixtureRunId)
            ? $"live-precheck-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..54]
            : fixtureRunId.Trim();
        return new PrecheckExecutionPlan(
            ExecutionMode.Live,
            runId,
            CreateInitialEvents());
    }

    public Task ExecuteAsync(
        PrecheckExecutionContext context,
        CancellationToken cancellationToken) =>
        workflow.ExecuteAsync(
            context.ProjectId,
            context.RunId,
            context.ReportAsync,
            cancellationToken);

    private static IReadOnlyList<PrecheckExecutionEvent> CreateInitialEvents() =>
    [
        Event("manifest", "manifest", "建立页面清单", "读取项目页面并建立稳定页面身份。", "页面清单已建立。"),
        Event("scan-plan", "scan-plan", "规划连续页面扫描", "按页码建立固定连续批次。", "连续扫描批次已建立。"),
        Event("page-routing", "page-routing", "确定页面翻译范围", "根据页面类型生成保守的纳入和排除结果。", "页面翻译范围已确定。"),
        Event("translation-context", "translation-context", "归并全书翻译上下文", "合并剧情、角色、术语和一致性线索。", "全书翻译上下文已生成。")
    ];

    private static PrecheckExecutionEvent Event(
        string eventId,
        string stage,
        string title,
        string request,
        string response) =>
        new(eventId, stage, title, request, response, string.Empty, null, []);
}
