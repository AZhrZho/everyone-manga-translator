using MangaTranslator.Api.Jobs;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.Tests;

public sealed class TextDetectorRuntimeTests
{
    private static readonly TextMaskDetectorDescriptor CraftDescriptor = new(
        TextDetectorIds.Craft,
        "craft-algorithm-v1",
        "craft-options-a",
        "craft.onnx");

    private static readonly TextMaskDetectorDescriptor CtdDescriptor = new(
        TextDetectorIds.Ctd,
        "ctd-algorithm-v1",
        "ctd-options-a",
        "comictextdetector.pt.onnx");

    [Fact]
    public void UnknownNonEmptyDetectorIdThrows()
    {
        using var runtime = CreateRuntime(new FixedModelLocator());

        var exception = Assert.Throws<ArgumentException>(() =>
            runtime.GetDescriptor("unknown-detector"));

        Assert.Contains("unknown-detector", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySelectionUsesCtd(string? detectorId)
    {
        using var runtime = CreateRuntime(new FixedModelLocator());

        Assert.Equal(
            TextDetectorIds.Ctd,
            runtime.GetDescriptor(detectorId!).DetectorId);
    }

    [Fact]
    public void LegacyDetectorIdNormalizesToCtd()
    {
        using var runtime = CreateRuntime(new FixedModelLocator());

        Assert.Equal(CtdDescriptor, runtime.GetDescriptor("manga-image-translator"));
    }

    [Fact]
    public void RegistryExposesBothFactories()
    {
        var craft = new RecordingFactory(CraftDescriptor);
        var ctd = new RecordingFactory(CtdDescriptor);
        using var runtime = new TextDetectorRuntime([craft, ctd], new FixedModelLocator());

        Assert.Equal(
            [TextDetectorIds.Craft, TextDetectorIds.Ctd],
            runtime.Detectors.Select(item => item.DetectorId));
        Assert.Equal(CraftDescriptor, runtime.GetDescriptor("CRAFT"));
        Assert.Equal(CtdDescriptor, runtime.GetDescriptor("CTD"));
    }

    [Fact]
    public async Task RepeatedDetectionWithSameModelReusesDetectorSession()
    {
        var root = CreateTempRoot();
        try
        {
            var modelPath = WriteModel(root, "craft.onnx", [1, 2, 3, 4]);
            var factory = new RecordingFactory(CraftDescriptor);
            using var runtime = new TextDetectorRuntime(
                [factory, new RecordingFactory(CtdDescriptor)],
                new FixedModelLocator(new Dictionary<string, string>
                {
                    [TextDetectorIds.Craft] = modelPath
                }));
            var selection = await runtime.ResolveAsync(
                TextDetectorIds.Craft,
                requireModel: true,
                CancellationToken.None);

            await runtime.DetectAsync(selection, Input());
            await runtime.DetectAsync(selection, Input());

            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(2, Assert.Single(factory.CreatedDetectors).DetectCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacedModelAfterTaskSnapshotIsRejectedBeforeInference()
    {
        var root = CreateTempRoot();
        try
        {
            var modelPath = WriteModel(root, "craft.onnx", [1, 2, 3, 4]);
            var factory = new RecordingFactory(CraftDescriptor);
            using var runtime = new TextDetectorRuntime(
                [factory, new RecordingFactory(CtdDescriptor)],
                new FixedModelLocator(new Dictionary<string, string>
                {
                    [TextDetectorIds.Craft] = modelPath
                }));
            var selection = await runtime.ResolveAsync(
                TextDetectorIds.Craft,
                requireModel: true,
                CancellationToken.None);
            await File.WriteAllBytesAsync(modelPath, [9, 8, 7, 6]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runtime.DetectAsync(selection, Input()));

            Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, factory.CreateCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescriptorAlgorithmAndOptionsAffectCacheKey()
    {
        var baseline = new TextDetectorSelection(
            TextDetectorIds.Craft,
            CraftDescriptor,
            "craft.onnx",
            new string('a', 64));
        var changedAlgorithm = baseline with
        {
            Descriptor = CraftDescriptor with { AlgorithmVersion = "craft-algorithm-v2" }
        };
        var changedOptions = baseline with
        {
            Descriptor = CraftDescriptor with { OptionsFingerprint = "craft-options-b" }
        };

        Assert.NotEqual(baseline.CacheKey, changedAlgorithm.CacheKey);
        Assert.NotEqual(baseline.CacheKey, changedOptions.CacheKey);
        Assert.NotEqual(changedAlgorithm.CacheKey, changedOptions.CacheKey);
    }

    private static TextDetectorRuntime CreateRuntime(ITextDetectorModelLocator locator) => new(
        [new RecordingFactory(CraftDescriptor), new RecordingFactory(CtdDescriptor)],
        locator);

    private static DetectionInput Input() => new(
        new byte[] { 0x89, 0x50, 0x4e, 0x47 },
        "image/png");

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"emt-detector-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteModel(string root, string fileName, byte[] bytes)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class FixedModelLocator(
        IReadOnlyDictionary<string, string>? modelPaths = null) : ITextDetectorModelLocator
    {
        public string DefaultInstallPath(TextMaskDetectorDescriptor detector) =>
            Path.Combine(Path.GetTempPath(), detector.DefaultModelFileName);

        public string EnvironmentVariableName(TextMaskDetectorDescriptor detector) =>
            $"TEST_{detector.DetectorId}";

        public string? TryResolve(TextMaskDetectorDescriptor detector) =>
            modelPaths?.GetValueOrDefault(detector.DetectorId);
    }

    private sealed class RecordingFactory(TextMaskDetectorDescriptor descriptor) : ITextMaskDetectorFactory
    {
        public TextMaskDetectorDescriptor Descriptor { get; } = descriptor;

        public int CreateCount { get; private set; }

        public List<RecordingDetector> CreatedDetectors { get; } = [];

        public ITextMaskDetector Create(ReadOnlyMemory<byte> model)
        {
            CreateCount++;
            var detector = new RecordingDetector();
            CreatedDetectors.Add(detector);
            return detector;
        }
    }

    private sealed class RecordingDetector : ITextMaskDetector
    {
        public int DetectCount { get; private set; }

        public ValueTask<TextDetectionResult> DetectAsync(
            DetectionInput input,
            CancellationToken cancellationToken = default)
        {
            DetectCount++;
            return ValueTask.FromResult(new TextDetectionResult(
                1,
                1,
                [],
                new MaskImage(1, 1, new byte[] { 0 })));
        }

        public void Dispose()
        {
        }
    }
}
