using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

public sealed record MinimalPrecheckOptions
{
    public string RunId { get; init; } = $"precheck_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

    public string ScanModel { get; init; } = "gpt-5.4";

    public string ConsolidationModel { get; init; } = "gpt-5.4";

    public string TargetLanguage { get; init; } = "zh-Hans";

    public int PagesPerScan { get; init; } = 12;

    public int ContextPagesPerSide { get; init; } = 1;

    public int MaxConcurrentScans { get; init; } = 4;

    public double MinimumExclusionConfidence { get; init; } = 0.80;

    public ImageDetailLevel ImageDetail { get; init; } = ImageDetailLevel.Original;
}

public sealed record MinimalPrecheckRequest(
    string BookId,
    IReadOnlyList<PageDescriptor> Pages,
    IPageAssetProvider PageAssets,
    MinimalPrecheckOptions Options);

public sealed record MinimalPrecheckScanBatch(
    string BatchId,
    IReadOnlyList<string> OwnerPages,
    IReadOnlyList<string> ContextPagesBefore,
    IReadOnlyList<string> ContextPagesAfter);

public sealed record MinimalPageClassification(
    string PageId,
    string PageType,
    double Confidence,
    IReadOnlyList<string> Evidence);

public sealed record MinimalPrecheckScanResult(
    string SchemaVersion,
    string BatchId,
    IReadOnlyList<string> OwnerPages,
    IReadOnlyList<MinimalPageClassification> Pages,
    JsonElement ContextDelta,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record MinimalPageManifestDocument(
    string SchemaVersion,
    string RunId,
    string BookId,
    IReadOnlyList<MinimalPageManifestEntry> Pages);

public sealed record MinimalPageManifestEntry(
    string PageId,
    int Order,
    string PageType,
    bool IncludeInWorkflow,
    double Confidence,
    IReadOnlyList<string> Evidence);

public sealed record TranslationBookContextDocument(
    string SchemaVersion,
    string RunId,
    string BookId,
    string TargetLanguage,
    JsonElement Synopsis,
    JsonElement TranslationStyle,
    JsonElement Characters,
    JsonElement Glossary,
    JsonElement ContinuityFacts,
    JsonElement UnresolvedQuestions);

public sealed record MinimalPrecheckResult(
    string RunId,
    MinimalPageManifestDocument PageManifest,
    IReadOnlyList<MinimalPrecheckScanBatch> ScanPlan,
    IReadOnlyList<MinimalPrecheckScanResult> ScanResults,
    TranslationBookContextDocument TranslationContext,
    BookManifestDocument BookManifest,
    GlobalSemanticMergeResult SemanticMerge);
