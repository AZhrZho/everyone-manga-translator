using MangaTranslator.LocalDetection.Craft;

namespace MangaTranslator.LocalDetection.Craft.Tests;

public sealed class CraftTextMaskDetectorProviderTests
{
    [Fact]
    public async Task ReusesDetectorForSameIdentityAndReloadsWhenHashChanges()
    {
        var modelPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(modelPath, [1, 2, 3]);
            var factory = new RecordingFactory();
            using var provider = new CraftTextMaskDetectorProvider(factory);
            var input = new DetectionInput(new byte[] { 1 }, "image/png");

            await provider.DetectAsync(modelPath, "hash-a", input);
            await provider.DetectAsync(modelPath, "hash-a", input);
            await provider.DetectAsync(modelPath, "hash-b", input);

            Assert.Equal(2, factory.Created.Count);
            Assert.Equal(2, factory.Created[0].DetectionCount);
            Assert.True(factory.Created[0].Disposed);
            Assert.Equal(1, factory.Created[1].DetectionCount);

            provider.Dispose();
            Assert.True(factory.Created[1].Disposed);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    private sealed class RecordingFactory : ITextMaskDetectorFactory
    {
        public List<RecordingDetector> Created { get; } = [];

        public TextMaskDetectorDescriptor Descriptor { get; } = new(
            "test",
            "test-v1",
            "options",
            "test.onnx");

        public ITextMaskDetector Create(ReadOnlyMemory<byte> model)
        {
            var detector = new RecordingDetector();
            Created.Add(detector);
            return detector;
        }
    }

    private sealed class RecordingDetector : ITextMaskDetector
    {
        public int DetectionCount { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<TextDetectionResult> DetectAsync(
            DetectionInput input,
            CancellationToken cancellationToken = default)
        {
            DetectionCount++;
            return ValueTask.FromResult(new TextDetectionResult(
                1,
                1,
                [],
                new MaskImage(1, 1, new byte[1])));
        }

        public void Dispose() => Disposed = true;
    }
}
