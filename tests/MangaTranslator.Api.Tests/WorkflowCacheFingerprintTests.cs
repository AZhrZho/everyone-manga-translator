using System.Text.Json;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.LocalDetection;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Tests;

public sealed class WorkflowCacheFingerprintTests
{
    private static readonly TextMaskDetectorDescriptor CraftDetector = new(
        "craft",
        "craft-onnx-clean-mask-v2",
        "options-a",
        "craft.onnx");

    private static readonly TextMaskDetectorDescriptor CtdDetector = new(
        TextDetectorIds.Ctd,
        "comic-text-detector-onnx-v1",
        "ctd-options-a",
        "comictextdetector.pt.onnx");

    [Fact]
    public void StageFingerprintIsStableAcrossInputOrderingAndRejectsTampering()
    {
        var first = WorkflowStageFingerprint.Create(
            "stage",
            "v1",
            new Dictionary<string, string>
            {
                ["model"] = "gpt",
                ["language"] = "zh-Hans"
            });
        var second = WorkflowStageFingerprint.Create(
            "stage",
            "v1",
            new Dictionary<string, string>
            {
                ["language"] = "zh-Hans",
                ["model"] = "gpt"
            });

        Assert.True(first.Matches(second));
        Assert.True(first.HasValidDigest());
        Assert.False((first with { Sha256 = new string('0', 64) }).HasValidDigest());
    }

    [Fact]
    public void DetectionFingerprintInvalidatesForSourceModelAndDetectorIdentityChanges()
    {
        var baseline = WorkflowCacheFingerprint.Detection(
            "source-a",
            "model-a",
            "mask-a",
            "clean-a",
            "p0001",
            CraftDetector,
            "regions");

        Assert.True(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-b",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-b",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-a",
            "mask-b",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline with { ContractVersion = "old" },
            "source-a",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector));

        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector with { DetectorId = "other-detector" }));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector with { AlgorithmVersion = "craft-onnx-clean-mask-v3" }));
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source-a",
            "p0001",
            "model-a",
            "mask-a",
            "clean-a",
            pipelineArtifact: false,
            CraftDetector with { OptionsFingerprint = "options-b" }));
    }

    [Fact]
    public void DetectionFingerprintSeparatesCraftAndCtdCaches()
    {
        var craft = WorkflowCacheFingerprint.Detection(
            "source",
            "model",
            "mask",
            "clean",
            "p0001",
            CraftDetector,
            "regions");
        var ctd = WorkflowCacheFingerprint.Detection(
            "source",
            "model",
            "mask",
            "clean",
            "p0001",
            CtdDetector,
            "regions");

        Assert.NotEqual(craft.Sha256, ctd.Sha256);
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            craft,
            "source",
            "p0001",
            "model",
            "mask",
            "clean",
            pipelineArtifact: false,
            CtdDetector));
        Assert.True(WorkflowCacheFingerprint.DetectionMatches(
            ctd,
            "source",
            "p0001",
            "model",
            "mask",
            "clean",
            pipelineArtifact: false,
            CtdDetector));
    }

    [Fact]
    public void DetectionFingerprintChangesWhenRegionsChange()
    {
        var first = WorkflowCacheFingerprint.Detection(
            "source",
            "model",
            "mask",
            "clean",
            "p0001",
            CraftDetector,
            "regions-a");
        var second = WorkflowCacheFingerprint.Detection(
            "source",
            "model",
            "mask",
            "clean",
            "p0001",
            CraftDetector,
            "regions-b");

        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    public void TranslationFingerprintInvalidatesForUserGuidanceFontsAndModel()
    {
        var baseline = Translation("guidance-a", "fonts-a", "model-a");

        Assert.False(baseline.Matches(Translation("guidance-b", "fonts-a", "model-a")));
        Assert.False(baseline.Matches(Translation("guidance-a", "fonts-b", "model-a")));
        Assert.False(baseline.Matches(Translation("guidance-a", "fonts-a", "model-b")));
    }

    [Fact]
    public void CanonicalJsonHashIgnoresObjectPropertyOrderButNotValues()
    {
        using var first = JsonDocument.Parse("{\"b\":2,\"a\":1}");
        using var second = JsonDocument.Parse("{\"a\":1,\"b\":2}");
        using var changed = JsonDocument.Parse("{\"a\":1,\"b\":3}");

        Assert.Equal(
            WorkflowCacheFingerprint.HashJson(first.RootElement),
            WorkflowCacheFingerprint.HashJson(second.RootElement));
        Assert.NotEqual(
            WorkflowCacheFingerprint.HashJson(first.RootElement),
            WorkflowCacheFingerprint.HashJson(changed.RootElement));
    }

    [Fact]
    public void LegacyCheckpointsWithoutFingerprintSafelyInvalidate()
    {
        var detection = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDetectionCheckpoint>(
            """{"sourceAssetKey":"source/page.png","dialoguePageId":"p0001","regions":[],"maskAssetKey":"mask.png","cleanAssetKey":"clean.png","provenance":"local-model:legacy"}""");
        var expertDialogue = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDialogueCheckpoint>("{}");
        var expertPage = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveTranslationPageCheckpoint>("{}");
        var quickDialogue = JsonSerializer.Deserialize<QuickTranslationWorkflow.QuickDialogueCheckpoint>("{}");
        var quickPage = JsonSerializer.Deserialize<QuickTranslationWorkflow.QuickPageCheckpoint>("{}");
        var expected = WorkflowCacheFingerprint.Detection(
            "source",
            "model",
            "mask",
            "clean",
            "p0001",
            CraftDetector,
            "regions");

        Assert.NotNull(detection);
        Assert.Null(detection.Fingerprint);
        Assert.False(WorkflowCacheFingerprint.DetectionMatches(
            detection.Fingerprint,
            "source",
            "p0001",
            "model",
            "mask",
            "clean",
            pipelineArtifact: false,
            CraftDetector));
        Assert.False(WorkflowCacheFingerprint.MatchesValid(expected, expertDialogue?.Fingerprint));
        Assert.False(WorkflowCacheFingerprint.MatchesValid(expected, expertPage?.Fingerprint));
        Assert.False(WorkflowCacheFingerprint.MatchesValid(expected, quickDialogue?.Fingerprint));
        Assert.False(WorkflowCacheFingerprint.MatchesValid(expected, quickPage?.Fingerprint));

        var incomplete = JsonSerializer.Deserialize<WorkflowStageFingerprint>(
            """{"stage":null,"contractVersion":null,"sha256":null,"inputs":{}}""");
        Assert.NotNull(incomplete);
        Assert.False(incomplete.HasValidDigest());
    }

    [Fact]
    public async Task PipelineArtifactRequiresUnchangedDetectionMaskAndCleanContent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"emt-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var detectionPath = Path.Combine(root, "detection.json");
            var maskPath = Path.Combine(root, "mask.png");
            var cleanPath = Path.Combine(root, "clean.png");
            await File.WriteAllTextAsync(detectionPath, "{\"regions\":[]}");
            await File.WriteAllBytesAsync(maskPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(cleanPath, [4, 5, 6]);
            var artifact = new PipelinePageArtifact(
                "p0001",
                1,
                "page.png",
                Path.Combine(root, "dialogue.json"),
                maskPath,
                cleanPath,
                detectionPath,
                "source-sha",
                HashFile(detectionPath),
                HashFile(maskPath),
                HashFile(cleanPath));

            Assert.True(await WorkflowCacheFingerprint.PipelineArtifactMatchesAsync(
                artifact,
                "source-sha",
                CancellationToken.None));

            await File.WriteAllBytesAsync(maskPath, [9, 9, 9]);

            Assert.False(await WorkflowCacheFingerprint.PipelineArtifactMatchesAsync(
                artifact,
                "source-sha",
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PipelineArtifactAcceptsFilesBeyondLegacyWindowsMaxPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"emt-cache-long-path-{Guid.NewGuid():N}");
        var current = root;
        try
        {
            PlatformIoPath.CreateDirectory(root);
            while (Path.Combine(current, "detection.json").Length <= 280)
            {
                current = Path.Combine(current, $"segment-{Guid.NewGuid():N}");
                PlatformIoPath.CreateDirectory(current);
            }

            var detectionPath = Path.Combine(current, "detection.json");
            var maskPath = Path.Combine(current, "mask.png");
            var cleanPath = Path.Combine(current, "clean.png");
            await File.WriteAllTextAsync(PlatformIoPath.NormalizeForIo(detectionPath), "{\"regions\":[]}");
            await File.WriteAllBytesAsync(PlatformIoPath.NormalizeForIo(maskPath), [1, 2, 3]);
            await File.WriteAllBytesAsync(PlatformIoPath.NormalizeForIo(cleanPath), [4, 5, 6]);
            var artifact = new PipelinePageArtifact(
                "p0001",
                1,
                "page.png",
                Path.Combine(current, "dialogue.json"),
                maskPath,
                cleanPath,
                detectionPath,
                "source-sha",
                HashFile(detectionPath),
                HashFile(maskPath),
                HashFile(cleanPath));

            Assert.True(detectionPath.Length > 260);
            Assert.True(await WorkflowCacheFingerprint.PipelineArtifactMatchesAsync(
                artifact,
                "source-sha",
                CancellationToken.None));
        }
        finally
        {
            if (PlatformIoPath.DirectoryExists(root))
            {
                Directory.Delete(PlatformIoPath.NormalizeForIo(root), recursive: true);
            }
        }
    }

    [Fact]
    public void PipelineDetectionFingerprintIncludesEveryImportedArtifact()
    {
        var baseline = WorkflowCacheFingerprint.PipelineDetection(
            "source", "detection", "mask", "clean", "p0001", "regions");

        Assert.False(baseline.Matches(WorkflowCacheFingerprint.PipelineDetection(
            "source", "changed", "mask", "clean", "p0001", "regions")));
        Assert.False(baseline.Matches(WorkflowCacheFingerprint.PipelineDetection(
            "source", "detection", "changed", "clean", "p0001", "regions")));
        Assert.False(baseline.Matches(WorkflowCacheFingerprint.PipelineDetection(
            "source", "detection", "mask", "changed", "p0001", "regions")));
        Assert.True(WorkflowCacheFingerprint.DetectionMatches(
            baseline,
            "source",
            "p0001",
            currentDetectorModelSha256: null,
            currentMaskSha256: "mask",
            currentCleanSha256: "clean",
            pipelineArtifact: true,
            currentDetector: CraftDetector with
            {
                DetectorId = "unrelated-local-detector",
                AlgorithmVersion = "unrelated-v1",
                OptionsFingerprint = "unrelated-options"
            }));
    }

    [Fact]
    public async Task ProjectDetectionCheckpointInvalidatesWhenStoredAssetsChange()
    {
        byte[] mask = [1, 2, 3];
        byte[] clean = [4, 5, 6];
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["mask.png"] = mask,
            ["clean.png"] = clean
        });
        var checkpoint = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
            "source.png",
            "p0001",
            [],
            "mask.png",
            "clean.png",
            "local-model:model",
            WorkflowStageFingerprint.Create(
                "detection-cleaning",
                WorkflowCacheFingerprint.LegacyDetectionContractVersion,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source_sha256"] = "source",
                    ["dialogue_page_id"] = "p0001",
                    ["detector_model_sha256"] = "model",
                    ["mask_sha256"] = WorkflowCacheFingerprint.HashBytes(mask),
                    ["clean_sha256"] = WorkflowCacheFingerprint.HashBytes(clean),
                    ["detector_id"] = CraftDetector.DetectorId,
                    ["detector_algorithm"] = CraftDetector.AlgorithmVersion,
                    ["detector_options_sha256"] = CraftDetector.OptionsFingerprint,
                    ["cleaner_algorithm"] = WorkflowCacheFingerprint.CleanerAlgorithmVersion,
                    ["cleaner_options"] = WorkflowCacheFingerprint.CleanerOptionsVersion
                }));

        Assert.True(await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
            assets,
            "project",
            checkpoint,
            "source.png",
            "source",
            "p0001",
            "model",
            CraftDetector,
            CancellationToken.None));

        assets.Set("mask.png", [9, 9, 9]);

        Assert.False(await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
            assets,
            "project",
            checkpoint,
            "source.png",
            "source",
            "p0001",
            "model",
            CraftDetector,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProjectDetectionCheckpointWithoutFingerprintInvalidatesSafely()
    {
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["mask.png"] = [1, 2, 3],
            ["clean.png"] = [4, 5, 6]
        });
        var checkpoint = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
            "source.png",
            "p0001",
            [],
            "mask.png",
            "clean.png",
            "browser-model:craft:model",
            null!);

        Assert.False(await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
            assets,
            "project",
            checkpoint,
            "source.png",
            "source",
            "p0001",
            "model",
            CraftDetector,
            CancellationToken.None));
    }

    private static WorkflowStageFingerprint Translation(
        string guidance,
        string fonts,
        string model) =>
        WorkflowCacheFingerprint.Translation(
            "expert",
            "source",
            "detection",
            "zh-Hans",
            "provider",
            "https://example.com/v1/responses",
            model,
            "expert-v1",
            "prompt-v1",
            "schema-v1",
            fonts,
            "knowledge",
            guidance,
            "page");

    private static string HashFile(string path)
    {
        using var stream = PlatformIoPath.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private sealed class MemoryAssetStore(Dictionary<string, byte[]> assets) : IProjectAssetStore
    {
        public Task<bool> ExistsAsync(string projectId, string assetKey, CancellationToken cancellationToken) =>
            Task.FromResult(assets.ContainsKey(assetKey));

        public Task<ProjectAsset> OpenReadAsync(string projectId, string assetKey, CancellationToken cancellationToken)
        {
            var data = assets[assetKey];
            return Task.FromResult(new ProjectAsset(
                new MemoryStream(data, writable: false),
                "application/octet-stream",
                Path.GetFileName(assetKey),
                data.Length));
        }

        public void Set(string assetKey, byte[] data) => assets[assetKey] = data;
    }
}
