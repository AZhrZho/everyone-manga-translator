using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.Api.BrowserInference;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.Api.Projects;
using MangaTranslator.Api.Settings;
using MangaTranslator.LocalDetection;
using MangaTranslator.ProjectSystem;
using SkiaSharp;
using ProviderEndpointType = MangaTranslator.Contracts.ProviderEndpointType;

namespace MangaTranslator.Api.Tests;

public sealed class BrowserDetectionArtifactServiceTests
{
    [Theory]
    [InlineData(DetectionArtifactPolicy.LocalOrReusable, false)]
    [InlineData(DetectionArtifactPolicy.BrowserOnly, true)]
    public void InvalidPinnedDetectionOnlyFailsClosedForBrowserOnlyRuns(
        DetectionArtifactPolicy policy,
        bool expected)
    {
        Assert.Equal(
            expected,
            LiveTranslationWorkflow.ShouldFailClosedForInvalidPinnedDetection(
                policy,
                "translation_detection_page-1_checkpoint.json"));
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static readonly TextMaskDetectorDescriptor CraftDescriptor = new(
        TextDetectorIds.Craft,
        "craft-onnx-clean-mask-v2",
        new string('1', 64),
        "craft.onnx");

    [Fact]
    public void BrowserCraftSettingMapsToCraftWithBrowserOnlyPolicy()
    {
        var selection = LiveTranslationExecutionSource.ResolveDetectionSelection(
            MangaTranslator.Contracts.LocalTextDetectionEngine.BrowserCraft);

        Assert.Equal(TextDetectorIds.Craft, selection.DetectorId);
        Assert.Equal(DetectionArtifactPolicy.BrowserOnly, selection.ArtifactPolicy);
    }

    [Fact]
    public async Task SaveCreatesReusableQuickWorkflowCheckpointWithoutServerModel()
    {
        var source = CreateSourcePng(32, 24);
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor);
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            runtime);
        var modelSha256 = new string('a', 64);

        var response = await service.SaveAsync(
            "project-1",
            "page-1",
            Metadata(modelSha256),
            new MemoryStream(mask, writable: false),
            CancellationToken.None);

        Assert.Equal(TextDetectorIds.Craft, response.DetectorId);
        Assert.Equal(modelSha256, response.ModelSha256);
        Assert.Equal(1, response.RegionCount);
        Assert.Equal(
            $"workflow-cache/page-1/browser-{response.FingerprintSha256}/mask.png",
            response.MaskAssetKey);
        Assert.Equal(
            $"workflow-cache/page-1/browser-{response.FingerprintSha256}/clean.png",
            response.CleanAssetKey);
        Assert.True(assets.Contains(response.MaskAssetKey));
        Assert.True(assets.Contains(response.CleanAssetKey));

        var modelArtifactName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            response.DetectorCacheKey);
        var noModelSelection = new TextDetectorSelection(
            TextDetectorIds.Craft,
            CraftDescriptor,
            null,
            null);
        var aliasArtifactName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            noModelSelection.CacheKey);
        Assert.NotEqual(response.DetectorCacheKey, noModelSelection.CacheKey);
        var modelArtifact = Assert.Single(
            artifacts.Items,
            item => item.Name == modelArtifactName);
        var aliasArtifact = Assert.Single(
            artifacts.Items,
            item => item.Name == aliasArtifactName);
        var browserLatestArtifact = Assert.Single(
            artifacts.Items,
            item => item.Name == LiveTranslationWorkflow.DetectionArtifactName(
                "page-1",
                BrowserDetectionArtifactService.BrowserLatestCacheKey));
        Assert.Equal(modelArtifact.Json, aliasArtifact.Json);
        Assert.Equal(modelArtifact.Json, browserLatestArtifact.Json);

        var checkpoint = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDetectionCheckpoint>(
            modelArtifact.Json,
            JsonOptions);
        Assert.NotNull(checkpoint);
        Assert.Equal("page-1", checkpoint.DialoguePageId);
        Assert.Equal("page-1:region:0001", Assert.Single(checkpoint.Regions).RegionId);

        var selection = new TextDetectorSelection(
            TextDetectorIds.Craft,
            CraftDescriptor,
            null,
            modelSha256);
        Assert.Equal(selection.CacheKey, response.DetectorCacheKey);
        Assert.True(await LiveTranslationWorkflow.DetectionCheckpointMatchesAsync(
            assets,
            "project-1",
            checkpoint,
            "source/page.png",
            response.SourceSha256,
            "page-1",
            currentDetectorModelSha256: null,
            CraftDescriptor,
            CancellationToken.None));
    }

    [Fact]
    public async Task DifferentBrowserResultsKeepIndependentImmutableAssetsAndCheckpoints()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor));
        var metadata = Metadata(new string('a', 64));

        var first = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata,
            new MemoryStream(CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14), writable: false),
            CancellationToken.None);
        var firstMask = assets.Get(first.MaskAssetKey);
        var second = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata with
            {
                Regions =
                [
                    metadata.Regions[0] with
                    {
                        X0 = 16,
                        X1 = 24,
                        Polygon =
                        [
                            new BrowserDetectionPoint(16, 6),
                            new BrowserDetectionPoint(23, 6),
                            new BrowserDetectionPoint(23, 15),
                            new BrowserDetectionPoint(16, 15)
                        ]
                    }
                ]
            },
            new MemoryStream(CreateMaskPng(32, 24, (x, y) => x is >= 16 and < 24 && y is >= 7 and < 14), writable: false),
            CancellationToken.None);

        Assert.NotEqual(first.FingerprintSha256, second.FingerprintSha256);
        Assert.NotEqual(first.MaskAssetKey, second.MaskAssetKey);
        Assert.Equal(firstMask, assets.Get(first.MaskAssetKey));
        Assert.True(assets.Contains(second.MaskAssetKey));
        Assert.Contains(artifacts.Items, item => item.Name == LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            BrowserDetectionArtifactService.BrowserCheckpointKey(first.FingerprintSha256)));
        Assert.Contains(artifacts.Items, item => item.Name == LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            BrowserDetectionArtifactService.BrowserCheckpointKey(second.FingerprintSha256)));
    }

    [Fact]
    public async Task RegionGeometryParticipatesInBrowserCheckpointIdentity()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor));
        var metadata = Metadata(new string('b', 64));
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);

        var first = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata,
            new MemoryStream(mask, writable: false),
            CancellationToken.None);
        var second = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata with { Regions = [metadata.Regions[0] with { Confidence = 0.8f }] },
            new MemoryStream(mask, writable: false),
            CancellationToken.None);

        Assert.NotEqual(first.FingerprintSha256, second.FingerprintSha256);
        Assert.NotEqual(first.MaskAssetKey, second.MaskAssetKey);
    }

    [Fact]
    public async Task EquivalentWebGpuAndWasmUploadsAreIdempotent()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor));
        var metadata = Metadata(new string('c', 64));
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);

        var webGpu = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata,
            new MemoryStream(mask, writable: false),
            CancellationToken.None);
        var wasm = await service.SaveAsync(
            "project-1",
            "page-1",
            metadata with { Backend = BrowserInferenceBackend.Wasm },
            new MemoryStream(mask, writable: false),
            CancellationToken.None);

        Assert.Equal(webGpu.FingerprintSha256, wasm.FingerprintSha256);
        Assert.Equal(webGpu.MaskAssetKey, wasm.MaskAssetKey);
        Assert.EndsWith(":webgpu", webGpu.Provenance, StringComparison.Ordinal);
        Assert.EndsWith(":wasm", wasm.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpertProjectUsesManifestPageIdForCheckpointReuse()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        artifacts.Items.Add(new ProjectArtifact(
            "book_manifest.json",
            """
            {
              "schema_version": "1.0",
              "book_id": "book",
              "pages": [
                {
                  "page_id": "dialogue-p0001",
                  "order": 1,
                  "chapter_id": null,
                  "scene_id": "scene-1",
                  "page_type": "story",
                  "include_in_mask": true
                }
              ]
            }
            """,
            DateTimeOffset.UtcNow));
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Expert)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor));

        var response = await service.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('b', 64)),
            new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
            CancellationToken.None);

        var checkpointArtifact = Assert.Single(
            artifacts.Items,
            item => item.Name == LiveTranslationWorkflow.DetectionArtifactName(
                "page-1",
                response.DetectorCacheKey));
        var checkpoint = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDetectionCheckpoint>(
            checkpointArtifact.Json,
            JsonOptions);
        Assert.NotNull(checkpoint);
        Assert.Equal("dialogue-p0001", checkpoint.DialoguePageId);
        Assert.StartsWith("dialogue-p0001:region:", Assert.Single(checkpoint.Regions).RegionId);
        Assert.Equal(checkpoint.Fingerprint.Sha256, response.FingerprintSha256);
    }

    [Fact]
    public async Task StaleSourceHashIsRejectedBeforeWritingArtifacts()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor));

        var exception = await Assert.ThrowsAsync<BrowserDetectionConflictException>(() =>
            service.SaveAsync(
                "project-1",
                "page-1",
                Metadata(new string('c', 64)) with { SourceSha256 = new string('0', 64) },
                new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
                CancellationToken.None));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(artifacts.Items);
        Assert.Single(assets.Keys);
    }

    [Fact]
    public async Task InstalledServerModelDoesNotConstrainBrowserModelIdentity()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor, new string('d', 64)));

        var response = await service.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('e', 64)),
            new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
            CancellationToken.None);

        Assert.Equal(new string('e', 64), response.ModelSha256);
    }

    [Fact]
    public async Task DetectorContractIdentityIsRequired()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            new MemoryArtifactStore(),
            new StubDetectorRuntime(CraftDescriptor));
        var metadata = Metadata(new string('f', 64)) with { AlgorithmVersion = string.Empty };

        await Assert.ThrowsAsync<BrowserDetectionConflictException>(() =>
            service.SaveAsync(
                "project-1",
                "page-1",
                metadata,
                new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
                CancellationToken.None));
    }

    [Fact]
    public async Task InstalledServerModelStillCreatesHostIndependentAliases()
    {
        var source = CreateSourcePng(32, 24);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var modelSha256 = new string('f', 64);
        var service = new BrowserDetectionArtifactService(
            new StubProjectStore(CreateProject(ProjectWorkflowMode.Quick)),
            assets,
            assets,
            artifacts,
            new StubDetectorRuntime(CraftDescriptor, modelSha256));

        var response = await service.SaveAsync(
            "project-1",
            "page-1",
            Metadata(modelSha256),
            new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
            CancellationToken.None);

        var modelArtifactName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            response.DetectorCacheKey);
        var noModelArtifactName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            new TextDetectorSelection(TextDetectorIds.Craft, CraftDescriptor, null, null).CacheKey);
        Assert.Single(artifacts.Items, item => item.Name == modelArtifactName);
        Assert.Single(artifacts.Items, item => item.Name == noModelArtifactName);
    }

    [Fact]
    public async Task QuickWorkflowPrepareReusesBrowserCheckpointWithoutServerModel()
    {
        var source = CreateSourcePng(32, 24);
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor);
        var uploadService = new BrowserDetectionArtifactService(
            projects,
            assets,
            assets,
            artifacts,
            runtime);
        await uploadService.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('a', 64)),
            new MemoryStream(mask, writable: false),
            CancellationToken.None);

        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = new QuickTranslationWorkflow(
            projects,
            assets,
            assets,
            artifacts,
            new StubProjectResultService(),
            new StubWorkflowModelSettingsResolver(),
            new StubHttpClientFactory(),
            new StubPipelineArtifactCatalog(),
            new TranslationAnnotationService(projects, artifacts),
            runtime,
            fontLibrary.Catalog,
            new LayoutAwareCleaningService(assets, assets, projects, artifacts));

        var preparation = await workflow.PrepareAsync(
            "project-1",
            "run-1",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.LocalOrReusable,
            pageIndices: null,
            CancellationToken.None);

        Assert.Equal("page-1", Assert.Single(preparation.Pages).PageId);
        Assert.Equal(0, preparation.AnnotationCount);
        Assert.Contains(
            artifacts.Items,
            item => item.Name == "quick_translation_run_run-1.json");
        var runJson = Assert.Single(
            artifacts.Items,
            item => item.Name == "quick_translation_run_run-1.json").Json;
        using var runDocument = JsonDocument.Parse(runJson);
        var pinnedArtifactName = runDocument.RootElement
            .GetProperty("pages")[0]
            .GetProperty("detectionArtifactName")
            .GetString();
        Assert.StartsWith("translation_detection_page-1_browser-", pinnedArtifactName);
        Assert.Contains(artifacts.Items, item => item.Name == pinnedArtifactName);
    }

    [Fact]
    public async Task BrowserOnlyPreparePinsStableAliasAndIgnoresInstalledServerModel()
    {
        var source = CreateSourcePng(32, 24);
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor, new string('d', 64));
        var uploadService = new BrowserDetectionArtifactService(
            projects,
            assets,
            assets,
            artifacts,
            runtime);
        var response = await uploadService.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('e', 64)),
            new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
            CancellationToken.None);

        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = CreateQuickWorkflow(projects, assets, artifacts, runtime, fontLibrary.Catalog);
        await workflow.PrepareAsync(
            "project-1",
            "browser-only-run",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.BrowserOnly,
            pageIndices: null,
            CancellationToken.None);

        var runJson = Assert.Single(
            artifacts.Items,
            item => item.Name == "quick_translation_run_browser-only-run.json").Json;
        using var runDocument = JsonDocument.Parse(runJson);
        Assert.Equal(
            "browser_only",
            runDocument.RootElement.GetProperty("detectionArtifactPolicy").GetString());
        Assert.Equal(
            LiveTranslationWorkflow.DetectionArtifactName(
                "page-1",
                BrowserDetectionArtifactService.BrowserCheckpointKey(response.FingerprintSha256)),
            runDocument.RootElement.GetProperty("pages")[0]
                .GetProperty("detectionArtifactName")
                .GetString());
        Assert.Equal(0, runtime.DetectCallCount);
    }

    [Fact]
    public async Task BrowserOnlyPrepareFailsClosedWhenBrowserResultIsMissingEvenWithInstalledModel()
    {
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = CreateSourcePng(32, 24)
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor, new string('a', 64));
        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = CreateQuickWorkflow(projects, assets, artifacts, runtime, fontLibrary.Catalog);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareAsync(
            "project-1",
            "browser-only-missing",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.BrowserOnly,
            pageIndices: null,
            CancellationToken.None));

        Assert.Contains("Missing pages: 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.DetectCallCount);
        Assert.DoesNotContain(
            artifacts.Items,
            item => item.Name == "quick_translation_run_browser-only-missing.json");
    }

    [Fact]
    public async Task QuickWorkflowMigratesLegacyBrowserAliasToImmutableAssetsBeforePinning()
    {
        var source = CreateSourcePng(32, 24);
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);
        var clean = source.ToArray();
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source,
            ["workflow-cache/page-1/legacy/mask.png"] = mask,
            ["workflow-cache/page-1/legacy/clean.png"] = clean
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor);
        var selection = await runtime.ResolveAsync(TextDetectorIds.Craft, false, CancellationToken.None);
        var regions = LiveTranslationWorkflow.CreateDialogueRegions(
            "page-1",
            [new DetectedTextRegion(
                5,
                6,
                15,
                16,
                DetectedTextLanguage.Unknown,
                DetectedTextOrientation.Horizontal,
                10,
                0.9f,
                [new DetectedTextLine(
                    [new ImagePoint(5, 6), new ImagePoint(14, 6), new ImagePoint(14, 15), new ImagePoint(5, 15)],
                    0.9f)])]);
        var legacyFingerprint = WorkflowStageFingerprint.Create(
            "detection-cleaning",
            WorkflowCacheFingerprint.LegacyDetectionContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_sha256"] = WorkflowCacheFingerprint.HashBytes(source),
                ["dialogue_page_id"] = "page-1",
                ["detector_model_sha256"] = new string('a', 64),
                ["mask_sha256"] = WorkflowCacheFingerprint.HashBytes(mask),
                ["clean_sha256"] = WorkflowCacheFingerprint.HashBytes(clean),
                ["detector_id"] = CraftDescriptor.DetectorId,
                ["detector_algorithm"] = CraftDescriptor.AlgorithmVersion,
                ["detector_options_sha256"] = CraftDescriptor.OptionsFingerprint,
                ["cleaner_algorithm"] = WorkflowCacheFingerprint.CleanerAlgorithmVersion,
                ["cleaner_options"] = WorkflowCacheFingerprint.CleanerOptionsVersion
            });
        var legacy = new LiveTranslationWorkflow.LiveDetectionCheckpoint(
            "source/page.png",
            "page-1",
            regions,
            "workflow-cache/page-1/legacy/mask.png",
            "workflow-cache/page-1/legacy/clean.png",
            "browser-model:craft:aaaaaaaaaaaaaaaa:webgpu",
            legacyFingerprint);
        var aliasName = LiveTranslationWorkflow.DetectionArtifactName("page-1", selection.CacheKey);
        artifacts.Items.Add(new ProjectArtifact(
            aliasName,
            JsonSerializer.Serialize(legacy, JsonOptions),
            DateTimeOffset.UtcNow));

        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = new QuickTranslationWorkflow(
            projects,
            assets,
            assets,
            artifacts,
            new StubProjectResultService(),
            new StubWorkflowModelSettingsResolver(),
            new StubHttpClientFactory(),
            new StubPipelineArtifactCatalog(),
            new TranslationAnnotationService(projects, artifacts),
            runtime,
            fontLibrary.Catalog,
            new LayoutAwareCleaningService(assets, assets, projects, artifacts));

        await workflow.PrepareAsync(
            "project-1",
            "legacy-run",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.LocalOrReusable,
            pageIndices: null,
            CancellationToken.None);

        var immutableName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            BrowserDetectionArtifactService.BrowserCheckpointKey(legacyFingerprint.Sha256));
        var immutable = Assert.Single(artifacts.Items, item => item.Name == immutableName);
        var migrated = JsonSerializer.Deserialize<LiveTranslationWorkflow.LiveDetectionCheckpoint>(
            immutable.Json,
            JsonOptions);
        Assert.NotNull(migrated);
        Assert.Equal(
            $"workflow-cache/page-1/browser-{legacyFingerprint.Sha256}/mask.png",
            migrated.MaskAssetKey);
        Assert.True(assets.Contains(migrated.MaskAssetKey));
        Assert.True(assets.Contains(migrated.CleanAssetKey));
        var runJson = Assert.Single(
            artifacts.Items,
            item => item.Name == "quick_translation_run_legacy-run.json").Json;
        using var runDocument = JsonDocument.Parse(runJson);
        Assert.Equal(
            immutableName,
            runDocument.RootElement.GetProperty("pages")[0]
                .GetProperty("detectionArtifactName")
                .GetString());
    }

    [Fact]
    public async Task QuickWorkflowPrepareFailsWhenBrowserAliasLosesImmutableCheckpoint()
    {
        var source = CreateSourcePng(32, 24);
        var mask = CreateMaskPng(32, 24, (x, y) => x is >= 6 and < 13 && y is >= 7 and < 14);
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor);
        var uploadService = new BrowserDetectionArtifactService(
            projects,
            assets,
            assets,
            artifacts,
            runtime);
        var response = await uploadService.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('a', 64)),
            new MemoryStream(mask, writable: false),
            CancellationToken.None);
        var immutableName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            BrowserDetectionArtifactService.BrowserCheckpointKey(response.FingerprintSha256));
        artifacts.Items.RemoveAll(item => item.Name == immutableName);

        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = new QuickTranslationWorkflow(
            projects,
            assets,
            assets,
            artifacts,
            new StubProjectResultService(),
            new StubWorkflowModelSettingsResolver(),
            new StubHttpClientFactory(),
            new StubPipelineArtifactCatalog(),
            new TranslationAnnotationService(projects, artifacts),
            runtime,
            fontLibrary.Catalog,
            new LayoutAwareCleaningService(assets, assets, projects, artifacts));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => workflow.PrepareAsync(
            "project-1",
            "missing-immutable-run",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.LocalOrReusable,
            pageIndices: null,
            CancellationToken.None));

        Assert.Contains(immutableName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            artifacts.Items,
            item => item.Name == "quick_translation_run_missing-immutable-run.json");
    }

    [Fact]
    public async Task QuickWorkflowPrepareFailsWhenImmutableCheckpointDiffersFromValidatedAlias()
    {
        var source = CreateSourcePng(32, 24);
        var project = CreateProject(ProjectWorkflowMode.Quick);
        var projects = new StubProjectStore(project);
        var assets = new MemoryAssetStore(new Dictionary<string, byte[]>
        {
            ["source/page.png"] = source
        });
        var artifacts = new MemoryArtifactStore();
        var runtime = new StubDetectorRuntime(CraftDescriptor);
        var uploadService = new BrowserDetectionArtifactService(
            projects,
            assets,
            assets,
            artifacts,
            runtime);
        var response = await uploadService.SaveAsync(
            "project-1",
            "page-1",
            Metadata(new string('a', 64)),
            new MemoryStream(CreateMaskPng(32, 24, (_, _) => false), writable: false),
            CancellationToken.None);
        var immutableName = LiveTranslationWorkflow.DetectionArtifactName(
            "page-1",
            BrowserDetectionArtifactService.BrowserCheckpointKey(response.FingerprintSha256));
        var immutable = Assert.Single(artifacts.Items, item => item.Name == immutableName);
        artifacts.Items.Remove(immutable);
        artifacts.Items.Add(immutable with { Json = immutable.Json + " " });

        using var fontLibrary = new TemporaryFontLibrary();
        var workflow = CreateQuickWorkflow(projects, assets, artifacts, runtime, fontLibrary.Catalog);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => workflow.PrepareAsync(
            "project-1",
            "mismatched-immutable-run",
            TextDetectorIds.Craft,
            DetectionArtifactPolicy.BrowserOnly,
            pageIndices: null,
            CancellationToken.None));

        Assert.Contains(immutableName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            artifacts.Items,
            item => item.Name == "quick_translation_run_mismatched-immutable-run.json");
    }

    private static BrowserDetectionUploadMetadata Metadata(string modelSha256) => new(
        BrowserInferenceContractVersions.DetectionUpload,
        TextDetectorIds.Craft,
        modelSha256,
        32,
        24,
        BrowserInferenceBackend.WebGpu,
        "onnxruntime-web/1.27.0",
        [new BrowserDetectionRegion(
            5,
            6,
            15,
            16,
            DetectedTextOrientation.Horizontal,
            10,
            0.9f,
            [
                new BrowserDetectionPoint(5, 6),
                new BrowserDetectionPoint(14, 6),
                new BrowserDetectionPoint(14, 15),
                new BrowserDetectionPoint(5, 15)
            ])],
        WorkflowCacheFingerprint.HashBytes(CreateSourcePng(32, 24)),
        CraftDescriptor.AlgorithmVersion,
        CraftDescriptor.OptionsFingerprint);

    private static QuickTranslationWorkflow CreateQuickWorkflow(
        IProjectStore projects,
        MemoryAssetStore assets,
        MemoryArtifactStore artifacts,
        ITextDetectorRuntime runtime,
        MangaTranslator.Api.Settings.FontCatalogService fonts) => new(
        projects,
        assets,
        assets,
        artifacts,
        new StubProjectResultService(),
        new StubWorkflowModelSettingsResolver(),
        new StubHttpClientFactory(),
        new StubPipelineArtifactCatalog(),
        new TranslationAnnotationService(projects, artifacts),
        runtime,
        fonts,
        new LayoutAwareCleaningService(assets, assets, projects, artifacts));

    private static ProjectSnapshot CreateProject(ProjectWorkflowMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProjectSnapshot(
            "project-1",
            "Project",
            ProjectSourceKind.Folder,
            1,
            true,
            mode,
            "zh-Hans",
            [],
            null,
            null,
            "project",
            [new ProjectPageSnapshot(
                "page-1",
                1,
                "page.png",
                32,
                24,
                "story",
                true,
                PageReviewTag.Unreviewed,
                "source/page.png",
                false,
                false,
                1,
                null,
                null,
                null,
                0)],
            now,
            now);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static byte[] CreateSourcePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(new SKRect(6, 7, 13, 14), paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] CreateMaskPng(
        int width,
        int height,
        Func<int, int, bool> selected)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, selected(x, y) ? SKColors.White : SKColors.Black);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class StubDetectorRuntime(
        TextMaskDetectorDescriptor descriptor,
        string? installedModelSha256 = null) : ITextDetectorRuntime
    {
        public int DetectCallCount { get; private set; }

        public IReadOnlyList<TextMaskDetectorDescriptor> Detectors => [descriptor];

        public TextMaskDetectorDescriptor GetDescriptor(string detectorId) => descriptor;

        public Task<TextDetectorSelection> ResolveAsync(
            string detectorId,
            bool requireModel,
            CancellationToken cancellationToken) => Task.FromResult(new TextDetectorSelection(
                descriptor.DetectorId,
                descriptor,
                installedModelSha256 is null ? null : "craft.onnx",
                installedModelSha256));

        public ValueTask<TextDetectionResult> DetectAsync(
            TextDetectorSelection selection,
            DetectionInput input,
            CancellationToken cancellationToken = default)
        {
            DetectCallCount++;
            throw new NotSupportedException();
        }

        public string DefaultInstallPath(string detectorId) => "craft.onnx";

        public string ModelConfigurationHint(string detectorId) => "TEST_CRAFT_MODEL";
    }

    private sealed class StubProjectStore(ProjectSnapshot project) : IProjectStore
    {
        public Task<ProjectSnapshot> GetAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(project);

        public Task<IReadOnlyList<ProjectListItem>> ListRecentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportFolderAsync(ImportFolderProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportPdfAsync(ImportPdfProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> OpenAsync(OpenProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> RenameAsync(string projectId, RenameProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageReviewAsync(string projectId, string pageId, UpdatePageReviewCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageReviewTagAsync(string projectId, string pageId, UpdatePageReviewTagCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageLayoutSnapshot> GetPageLayoutAsync(
            string projectId,
            string pageId,
            CancellationToken cancellationToken)
        {
            var page = project.Pages.Single(item => item.PageId == pageId);
            return Task.FromResult(new PageLayoutSnapshot(
                project.ProjectId,
                page.PageId,
                project.Revision,
                page.Revision,
                page.HasUnversionedUserChanges,
                page.CurrentLetteringVersionId,
                null));
        }
        public Task<ProjectSnapshot> UpdatePageLayoutAsync(string projectId, string pageId, UpdatePageLayoutCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageImageEditSnapshot> GetPageImageEditAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageImageEditAsync(string projectId, string pageId, UpdatePageImageEditCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PageVersionSnapshot>> ListPageVersionsAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> CreatePageVersionAsync(string projectId, string pageId, CreatePageVersionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> SwitchPageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> DeletePageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryAssetStore(Dictionary<string, byte[]> items)
        : IProjectAssetStore, IProjectAssetWriter
    {
        public IReadOnlyCollection<string> Keys => items.Keys;

        public bool Contains(string assetKey) => items.ContainsKey(assetKey);

        public byte[] Get(string assetKey) => items[assetKey].ToArray();

        public Task<bool> ExistsAsync(string projectId, string assetKey, CancellationToken cancellationToken) =>
            Task.FromResult(items.ContainsKey(assetKey));

        public Task<ProjectAsset> OpenReadAsync(
            string projectId,
            string assetKey,
            CancellationToken cancellationToken)
        {
            var data = items[assetKey];
            return Task.FromResult(new ProjectAsset(
                new MemoryStream(data, writable: false),
                Path.GetExtension(assetKey).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "application/octet-stream",
                Path.GetFileName(assetKey),
                data.Length));
        }

        public async Task SaveAsync(
            string projectId,
            string assetKey,
            Stream content,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            items[assetKey] = buffer.ToArray();
        }
    }

    private sealed class MemoryArtifactStore : IProjectArtifactStore
    {
        public List<ProjectArtifact> Items { get; } = [];

        public Task SaveAsync(
            string projectId,
            string name,
            string json,
            CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            Items.Add(new ProjectArtifact(name, json, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectArtifact>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectArtifact>>(Items.ToArray());

        public Task<int> UpdateAsync(
            string projectId,
            string name,
            string json,
            int expectedProjectRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubWorkflowModelSettingsResolver : IWorkflowModelSettingsResolver
    {
        public Task<WorkflowModelSettings> ResolveAsync(
            string stageId,
            CancellationToken cancellationToken) => Task.FromResult(new WorkflowModelSettings(
                stageId,
                "openai",
                ProviderEndpointType.OpenAIResponses,
                new Uri("https://example.test/v1/responses"),
                "test-key",
                "test-model"));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    private sealed class StubPipelineArtifactCatalog : IPipelineArtifactCatalog
    {
        public IReadOnlyList<PipelineArtifactDescriptor> List() => [];

        public bool TryGet(string runIdOrBookId, out PipelineArtifactDescriptor? descriptor)
        {
            descriptor = null;
            return false;
        }

        public PipelineArtifactDescriptor? MatchProject(ProjectSnapshot project) => null;
    }

    private sealed class StubProjectResultService : IProjectResultService
    {
        public Task<ProjectAsset> RenderPageAsync(
            string projectId,
            string pageId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectAsset> RenderPageAsync(
            string projectId,
            string pageId,
            string backgroundAssetKey,
            string? layoutJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryFontLibrary : IDisposable
    {
        public TemporaryFontLibrary()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"emt-browser-detection-fonts-{Guid.NewGuid():N}");
            var sampleDirectory = Path.Combine(Root, "samples");
            Directory.CreateDirectory(sampleDirectory);
            File.WriteAllBytes(
                Path.Combine(sampleDirectory, "reference.png"),
                CreateSourcePng(1, 1));
            File.WriteAllText(
                Path.Combine(Root, "font_catalog.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    libraryRevision = 1,
                    fonts = new[]
                    {
                        new
                        {
                            fontId = "F0001",
                            displayName = "Test Font",
                            familyName = "Test Family",
                            category = "dialogue",
                            tags = Array.Empty<string>()
                        }
                    },
                    latestSample = new
                    {
                        relativePath = "samples/reference.png",
                        sourceLibraryRevision = 1,
                        fontSizePx = 12,
                        widthPx = 1,
                        heightPx = 1,
                        cells = new[]
                        {
                            new
                            {
                                fontId = "F0001",
                                style = "regular",
                                renderMode = "native",
                                sourceStyle = "regular",
                                missingGlyphCount = 0,
                                missingGlyphs = Array.Empty<string>(),
                                label = "native"
                            }
                        }
                    }
                },
                JsonOptions));

            var constructor = typeof(FontCatalogService).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string)],
                modifiers: null)
                ?? throw new InvalidOperationException("FontCatalogService test constructor was not found.");
            Catalog = (FontCatalogService)constructor.Invoke([Root]);
        }

        public string Root { get; }

        public FontCatalogService Catalog { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
