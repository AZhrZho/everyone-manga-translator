namespace MangaTranslator.Pipeline.Integration.Tests;

public sealed class PipelineStageFingerprintTests
{
    [Fact]
    public void CreateIsStableAcrossInputOrdering()
    {
        var first = PipelineStageFingerprint.Create(
            "render",
            "v1",
            new Dictionary<string, string>
            {
                ["dialogue"] = "abc",
                ["debug"] = "true"
            });
        var second = PipelineStageFingerprint.Create(
            "render",
            "v1",
            new Dictionary<string, string>
            {
                ["debug"] = "true",
                ["dialogue"] = "abc"
            });

        Assert.True(first.Matches(second));
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public void CreateChangesWhenContractOrInputChanges()
    {
        var baseline = PipelineStageFingerprint.Create(
            "mixed",
            "v1",
            new Dictionary<string, string> { ["alpha"] = "0.2" });
        var changedContract = PipelineStageFingerprint.Create(
            "mixed",
            "v2",
            new Dictionary<string, string> { ["alpha"] = "0.2" });
        var changedInput = PipelineStageFingerprint.Create(
            "mixed",
            "v1",
            new Dictionary<string, string> { ["alpha"] = "0.3" });

        Assert.False(baseline.Matches(changedContract));
        Assert.False(baseline.Matches(changedInput));
    }
}
