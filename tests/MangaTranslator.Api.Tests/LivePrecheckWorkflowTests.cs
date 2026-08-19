using System.Text.Json;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Settings;
using MangaTranslator.Contracts;

namespace MangaTranslator.Api.Tests;

public sealed class LivePrecheckWorkflowTests
{
    [Fact]
    public void CreatesOptionsFromProjectLanguageAndBothConfiguredStages()
    {
        var manifest = Settings(LivePrecheckWorkflow.ScanModelStageId, "scan-model");
        var analysis = Settings(LivePrecheckWorkflow.ContextModelStageId, "context-model");

        var options = LivePrecheckWorkflow.CreateOptions(
            "run-1",
            "en-US",
            manifest,
            analysis);

        Assert.Equal("run-1", options.RunId);
        Assert.Equal("en-US", options.TargetLanguage);
        Assert.Equal("scan-model", options.ScanModel);
        Assert.Equal("context-model", options.ConsolidationModel);
        Assert.Equal(12, options.PagesPerScan);
        Assert.Equal(1, options.ContextPagesPerSide);
        Assert.Equal(4, options.MaxConcurrentScans);
    }

    [Fact]
    public void CreatesOptionsFromProjectBatchSettings()
    {
        var options = LivePrecheckWorkflow.CreateOptions(
            "run-custom",
            "zh-Hans",
            Settings(LivePrecheckWorkflow.ScanModelStageId, "scan-model"),
            Settings(LivePrecheckWorkflow.ContextModelStageId, "context-model"),
            pagesPerScan: 7,
            maxConcurrentScans: 3);

        Assert.Equal(7, options.PagesPerScan);
        Assert.Equal(3, options.MaxConcurrentScans);
    }

    [Fact]
    public void GlossaryReviewProjectionUsesTranslationCompatibleStableKeys()
    {
        var terms = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                source = "王都",
                translation = "Royal Capital",
                confidence = 0.9,
                evidence = Array.Empty<object>()
            }
        });

        using var document = JsonDocument.Parse(
            PrecheckReviewProjection.CreateGlossaryJson("run-1", "book-1", terms));
        var mergeKey = document.RootElement.GetProperty("terms")[0].GetProperty("mergeKey").GetString();

        Assert.StartsWith("term_", mergeKey, StringComparison.Ordinal);
        Assert.Equal(21, mergeKey?.Length);
    }

    private static WorkflowModelSettings Settings(string stageId, string modelId) =>
        new(stageId, "provider", ProviderEndpointType.OpenAIResponses, new Uri("https://example.com/v1/responses"), "secret", modelId);
}
