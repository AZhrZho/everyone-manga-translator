using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Jobs;

public interface ILivePrecheckWorkflow
{
    Task ValidateAsync(string projectId, CancellationToken cancellationToken);

    Task ExecuteAsync(
        string projectId,
        string runId,
        Func<PrecheckExecutionEvent, CancellationToken, Task> reportAsync,
        CancellationToken cancellationToken);
}

public sealed class LivePrecheckWorkflow(
    IProjectStore projects,
    IProjectAssetStore assets,
    IProjectArtifactStore artifacts,
    IWorkflowModelSettingsResolver modelSettings,
    IApplicationSettingsStore applicationSettings,
    IHttpClientFactory httpClientFactory) : ILivePrecheckWorkflow
{
    internal const string ScanModelStageId = "precheck-manifest";
    internal const string ContextModelStageId = "precheck-analysis";
    internal const int ContextPagesPerSide = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task ValidateAsync(string projectId, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        if (project.Pages.Count == 0)
        {
            throw new InvalidOperationException("项目中没有可供预处理的页面。");
        }

        await modelSettings.ResolveAsync(ScanModelStageId, cancellationToken);
        await modelSettings.ResolveAsync(ContextModelStageId, cancellationToken);
    }

    public async Task ExecuteAsync(
        string projectId,
        string runId,
        Func<PrecheckExecutionEvent, CancellationToken, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(reportAsync);

        var project = await projects.GetAsync(projectId, cancellationToken);
        var scanSettings = await modelSettings.ResolveAsync(ScanModelStageId, cancellationToken);
        var contextSettings = await modelSettings.ResolveAsync(ContextModelStageId, cancellationToken);
        var batchSettings = await applicationSettings.GetAsync(cancellationToken);
        var descriptors = await CreatePageDescriptorsAsync(project, cancellationToken);
        var request = new MinimalPrecheckRequest(
            project.ProjectId,
            descriptors,
            new ProjectPageAssetProvider(project, assets),
            CreateOptions(
                runId,
                project.TargetLanguage,
                scanSettings,
                contextSettings,
                batchSettings.PrecheckBatchSize,
                batchSettings.PrecheckBatchConcurrency));

        using var scanHttpClient = httpClientFactory.CreateClient("workflow-model-provider");
        using var contextHttpClient = httpClientFactory.CreateClient("workflow-model-provider");
        var engine = new MinimalPrecheckEngine(
            CreateModelProvider(scanHttpClient, scanSettings),
            CreateModelProvider(contextHttpClient, contextSettings));

        await reportAsync(Event(
            "manifest",
            "manifest",
            "建立页面清单",
            "读取项目页面并建立稳定页面身份。",
            "页面清单已建立。",
            $"pages: {descriptors.Length}",
            null,
            descriptors.Select(page => page.SourceIndex).ToArray(),
            ProcessEventStatus.Running), cancellationToken);
        var sourceManifest = CreateSourceManifest(request);
        await SaveAsync(projectId, "source_manifest.json", sourceManifest, cancellationToken);
        await reportAsync(Event(
            "manifest",
            "manifest",
            "建立页面清单",
            "读取项目页面并建立稳定页面身份。",
            $"已建立 {descriptors.Length} 页的页面清单。",
            $"run: {runId}; pages: {descriptors.Length}",
            null,
            descriptors.Select(page => page.SourceIndex).ToArray(),
            ProcessEventStatus.Completed), cancellationToken);

        var plan = MinimalPrecheckEngine.Plan(request);
        await SaveAsync(projectId, "precheck_scan_plan.json", new
        {
            schemaVersion = "1.0.0",
            runId,
            pagesPerScan = batchSettings.PrecheckBatchSize,
            contextPagesPerSide = ContextPagesPerSide,
            maxConcurrentScans = batchSettings.PrecheckBatchConcurrency,
            batches = plan
        }, cancellationToken);
        await reportAsync(Event(
            "scan-plan",
            "scan-plan",
            "规划连续页面扫描",
            "按页码建立固定连续批次，不执行动态风险分析。",
            $"已建立 {plan.Count} 个扫描批次。",
            $"batches: {plan.Count}; pages_per_scan: {batchSettings.PrecheckBatchSize}; max_concurrent_scans: {batchSettings.PrecheckBatchConcurrency}",
            null,
            descriptors.Select(page => page.SourceIndex).ToArray(),
            ProcessEventStatus.Completed), cancellationToken);

        var pageIndex = descriptors.ToDictionary(page => page.PageId, page => page.SourceIndex, StringComparer.Ordinal);
        var restored = await LoadRestoredScansAsync(projectId, runId, plan, cancellationToken);
        var scans = await engine.ScanAsync(
            request,
            plan,
            restored,
            async (batch, wasRestored, token) => await reportAsync(ScanEvent(
                batch,
                pageIndex,
                wasRestored ? ProcessEventStatus.Completed : ProcessEventStatus.Running,
                wasRestored), token),
            async (batch, result, wasRestored, token) =>
            {
                if (!wasRestored)
                {
                    await SaveAsync(projectId, ScanArtifactName(runId, batch.BatchId), result, token);
                }
                await reportAsync(ScanEvent(batch, pageIndex, ProcessEventStatus.Completed, wasRestored), token);
            },
            cancellationToken);

        var pageManifest = MinimalPrecheckEngine.BuildPageManifest(request, scans);
        await SaveAsync(projectId, "page_manifest.json", pageManifest, cancellationToken);
        await reportAsync(Event(
            "page-routing",
            "page-routing",
            "确定页面翻译范围",
            "根据页面类型和置信度生成保守的纳入/排除结果。",
            $"纳入 {pageManifest.Pages.Count(page => page.IncludeInWorkflow)} 页，排除 {pageManifest.Pages.Count(page => !page.IncludeInWorkflow)} 页。",
            $"included: {pageManifest.Pages.Count(page => page.IncludeInWorkflow)}; excluded: {pageManifest.Pages.Count(page => !page.IncludeInWorkflow)}",
            null,
            pageManifest.Pages.Select(page => page.Order).ToArray(),
            ProcessEventStatus.Completed), cancellationToken);

        await reportAsync(Event(
            "translation-context",
            "translation-context",
            "归并全书翻译上下文",
            "将全部扫描批次的剧情、角色、术语和一致性线索合并为一个 Story Bible。",
            "全书翻译上下文已生成。",
            $"scan_batches: {scans.Count}; model_calls: {plan.Count + 1}",
            null,
            descriptors.Select(page => page.SourceIndex).ToArray(),
            ProcessEventStatus.Running), cancellationToken);
        var context = await LoadAsync<TranslationBookContextDocument>(
                          projectId,
                          ContextArtifactName(runId, batchSettings.PrecheckBatchSize),
                          cancellationToken)
                      ?? await engine.ConsolidateAsync(request, pageManifest, scans, cancellationToken);
        await SaveAsync(projectId, ContextArtifactName(runId, batchSettings.PrecheckBatchSize), context, cancellationToken);
        await SaveAsync(projectId, "translation_book_context.json", context, cancellationToken);

        var bookManifest = MinimalPrecheckEngine.CreateBookManifest(request, pageManifest);
        var semanticMerge = MinimalPrecheckEngine.CreateSemanticMerge(context);
        var result = new MinimalPrecheckResult(runId, pageManifest, plan, scans, context, bookManifest, semanticMerge);
        await SaveAsync(projectId, "book_manifest.json", bookManifest, cancellationToken);
        await SaveAsync(projectId, "semantic_merge.json", semanticMerge, cancellationToken);
        await SaveAsync(projectId, "precheck_result.json", result, cancellationToken);
        await SaveAsync(projectId, $"precheck_result_{Sanitize(runId)}.json", result, cancellationToken);
        await artifacts.SaveAsync(
            projectId,
            "characters.json",
            PrecheckReviewProjection.CreateCharactersJson(runId, projectId, semanticMerge.Characters),
            cancellationToken);
        await artifacts.SaveAsync(
            projectId,
            "glossary.json",
            PrecheckReviewProjection.CreateGlossaryJson(runId, projectId, semanticMerge.Glossary),
            cancellationToken);
        await ApplyManifestAsync(projectId, pageManifest, cancellationToken);
        await reportAsync(Event(
            "translation-context",
            "translation-context",
            "归并全书翻译上下文",
            "将全部扫描批次的剧情、角色、术语和一致性线索合并为一个 Story Bible。",
            "全书翻译上下文已生成，可进入页面审阅。",
            $"characters: {ArrayLength(semanticMerge.Characters)}; glossary: {ArrayLength(semanticMerge.Glossary)}; model_calls: {plan.Count + 1}",
            null,
            descriptors.Select(page => page.SourceIndex).ToArray(),
            ProcessEventStatus.Completed), cancellationToken);
    }

    internal static MinimalPrecheckOptions CreateOptions(
        string runId,
        string targetLanguage,
        WorkflowModelSettings scanSettings,
        WorkflowModelSettings contextSettings,
        int pagesPerScan = ProjectPrecheckDefaults.BatchSize,
        int maxConcurrentScans = ProjectPrecheckDefaults.BatchConcurrency) => new()
    {
        RunId = runId,
        TargetLanguage = targetLanguage,
        ScanModel = scanSettings.ModelId,
        ConsolidationModel = contextSettings.ModelId,
        PagesPerScan = pagesPerScan,
        ContextPagesPerSide = ContextPagesPerSide,
        MaxConcurrentScans = maxConcurrentScans
    };

    private async Task<PageDescriptor[]> CreatePageDescriptorsAsync(
        ProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        var result = new List<PageDescriptor>(project.Pages.Count);
        foreach (var page in project.Pages.OrderBy(item => item.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var asset = await assets.OpenReadAsync(project.ProjectId, page.SourceAssetKey, cancellationToken);
            var hash = await SHA256.HashDataAsync(asset.Content, cancellationToken);
            result.Add(new PageDescriptor(
                page.PageId,
                page.Index,
                page.FileName,
                page.Width,
                page.Height,
                asset.MediaType,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }
        return result.ToArray();
    }

    private async Task<Dictionary<string, MinimalPrecheckScanResult>> LoadRestoredScansAsync(
        string projectId,
        string runId,
        IReadOnlyList<MinimalPrecheckScanBatch> plan,
        CancellationToken cancellationToken)
    {
        var names = (await artifacts.ListAsync(projectId, cancellationToken))
            .ToDictionary(item => item.Name, item => item.Json, StringComparer.Ordinal);
        var result = new Dictionary<string, MinimalPrecheckScanResult>(StringComparer.Ordinal);
        foreach (var batch in plan)
        {
            if (!names.TryGetValue(ScanArtifactName(runId, batch.BatchId), out var json)) continue;
            var restored = JsonSerializer.Deserialize<MinimalPrecheckScanResult>(json, JsonOptions);
            if (restored is not null
                && restored.OwnerPages.SequenceEqual(batch.OwnerPages, StringComparer.Ordinal))
            {
                result[batch.BatchId] = restored;
            }
        }
        return result;
    }

    private async Task ApplyManifestAsync(
        string projectId,
        MinimalPageManifestDocument manifest,
        CancellationToken cancellationToken)
    {
        var snapshot = await projects.GetAsync(projectId, cancellationToken);
        foreach (var manifestPage in manifest.Pages.OrderBy(page => page.Order))
        {
            var page = snapshot.Pages.FirstOrDefault(item => item.PageId == manifestPage.PageId);
            if (page is null
                || page.PageType.Equals(manifestPage.PageType, StringComparison.OrdinalIgnoreCase)
                && page.IncludedInWorkflow == manifestPage.IncludeInWorkflow)
            {
                continue;
            }
            snapshot = await projects.UpdatePageReviewAsync(
                projectId,
                page.PageId,
                new UpdatePageReviewCommand(manifestPage.PageType, manifestPage.IncludeInWorkflow, snapshot.Revision),
                cancellationToken);
        }
    }

    private static SourceManifestDocument CreateSourceManifest(MinimalPrecheckRequest request) =>
        new("1.0.0", request.Options.RunId, request.BookId, request.Pages, []);

    private async Task SaveAsync<T>(string projectId, string name, T value, CancellationToken cancellationToken) =>
        await artifacts.SaveAsync(projectId, name, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private async Task<T?> LoadAsync<T>(string projectId, string name, CancellationToken cancellationToken)
    {
        var artifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => item.Name == name);
        return artifact is null ? default : JsonSerializer.Deserialize<T>(artifact.Json, JsonOptions);
    }

    private static IModelProvider CreateModelProvider(HttpClient client, WorkflowModelSettings settings) =>
        ModelProviderFactory.Create(client, settings);

    private static PrecheckExecutionEvent ScanEvent(
        MinimalPrecheckScanBatch batch,
        IReadOnlyDictionary<string, int> pageIndices,
        ProcessEventStatus status,
        bool restored)
    {
        var owners = batch.OwnerPages.Select(pageId => pageIndices[pageId]).ToArray();
        return Event(
            batch.BatchId,
            "page-scan",
            $"扫描第 {string.Join("、", owners)} 页",
            "识别页面类型，并提取剧情、角色、术语和翻译一致性线索。",
            restored
                ? $"{batch.BatchId} 已从断点恢复。"
                : status == ProcessEventStatus.Running
                    ? $"{batch.BatchId} 正在扫描。"
                    : $"{batch.BatchId} 已完成。",
            $"owner_pages: [{string.Join(", ", owners)}]; context_pages: {batch.ContextPagesBefore.Count + batch.ContextPagesAfter.Count}",
            "page-scan",
            owners,
            status);
    }

    private static PrecheckExecutionEvent Event(
        string eventId,
        string stage,
        string title,
        string request,
        string response,
        string detail,
        string? parallelGroup,
        IReadOnlyList<int> pageIndices,
        ProcessEventStatus status) =>
        new(eventId, stage, title, request, response, detail, parallelGroup, pageIndices, status);

    private static string ScanArtifactName(string runId, string batchId) =>
        $"minimal_precheck_scan_{Sanitize(runId)}_{Sanitize(batchId)}.json";

    private static string ContextArtifactName(string runId, int batchSize) =>
        $"minimal_precheck_context_{Sanitize(runId)}_batch_{batchSize}.json";

    private static string Sanitize(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());

    private static int ArrayLength(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private sealed class ProjectPageAssetProvider(
        ProjectSnapshot project,
        IProjectAssetStore assets) : IPageAssetProvider
    {
        private readonly IReadOnlyDictionary<string, ProjectPageSnapshot> pages =
            project.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);

        public async ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
            string pageId,
            ImageQuality quality,
            CancellationToken cancellationToken = default)
        {
            _ = quality;
            if (!pages.TryGetValue(pageId, out var page))
            {
                throw new ProjectPageNotFoundException(project.ProjectId, pageId);
            }
            await using var asset = await assets.OpenReadAsync(project.ProjectId, page.SourceAssetKey, cancellationToken);
            using var buffer = new MemoryStream();
            await asset.Content.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
    }
}

internal static class PrecheckReviewProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string CreateCharactersJson(string runId, string bookId, JsonElement characters)
    {
        var result = new List<object>();
        if (characters.ValueKind == JsonValueKind.Array)
        {
            foreach (var character in characters.EnumerateArray())
            {
                var key = ReadString(character, "character_id")
                          ?? StableKey(ReadString(character, "original_name") ?? Guid.NewGuid().ToString("N"));
                var value = ReadString(character, "translated_name")
                            ?? ReadString(character, "original_name")
                            ?? key;
                result.Add(new
                {
                    operation = "add",
                    mergeKey = key,
                    candidateId = (string?)null,
                    value,
                    pages = ReadEvidencePages(character),
                    evidence = ReadEvidenceSummary(character),
                    confidence = ReadDouble(character, "confidence") ?? 0d
                });
            }
        }
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            runId,
            bookId,
            characters = result,
            conflicts = Array.Empty<object>()
        }, JsonOptions);
    }

    public static string CreateGlossaryJson(string runId, string bookId, JsonElement terms)
    {
        var result = new List<object>();
        if (terms.ValueKind == JsonValueKind.Array)
        {
            foreach (var term in terms.EnumerateArray())
            {
                var source = ReadString(term, "source") ?? string.Empty;
                var value = ReadString(term, "translation") ?? source;
                result.Add(new
                {
                    operation = "add",
                    mergeKey = StableKey(source.Length == 0 ? value : source),
                    candidateId = (string?)null,
                    value,
                    pages = ReadEvidencePages(term),
                    evidence = ReadEvidenceSummary(term),
                    confidence = ReadBoolean(term, "locked") == true ? 1d : ReadDouble(term, "confidence") ?? 0.85d
                });
            }
        }
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            runId,
            bookId,
            terms = result,
            conflicts = Array.Empty<object>()
        }, JsonOptions);
    }

    private static IReadOnlyList<string> ReadEvidencePages(JsonElement item)
    {
        if (!item.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array) return [];
        return evidence.EnumerateArray()
            .Select(entry => ReadString(entry, "page_id"))
            .Where(page => !string.IsNullOrWhiteSpace(page))
            .Select(page => page!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadEvidenceSummary(JsonElement item)
    {
        if (!item.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
        {
            return ReadString(item, "selection_rationale") ?? ReadString(item, "rationale") ?? string.Empty;
        }
        var entries = evidence.EnumerateArray()
            .Select(entry => new { Page = ReadString(entry, "page_id"), Quote = ReadString(entry, "quote") })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Quote))
            .Take(4)
            .Select(entry => string.IsNullOrWhiteSpace(entry.Page) ? entry.Quote! : $"{entry.Page}: {entry.Quote}")
            .ToArray();
        return entries.Length == 0
            ? ReadString(item, "selection_rationale") ?? ReadString(item, "rationale") ?? string.Empty
            : string.Join("；", entries);
    }

    private static string StableKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "term_" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static bool? ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
