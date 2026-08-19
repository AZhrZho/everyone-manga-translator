using MangaTranslator.LocalDetection.Craft;

namespace MangaTranslator.LocalDetection.Craft.Tests;

public sealed class CraftTextMaskDetectorFactoryTests
{
    [Fact]
    public void DescriptorFingerprintIsStableAndChangesWithOptions()
    {
        var first = new CraftTextMaskDetectorFactory().Descriptor;
        var second = new CraftTextMaskDetectorFactory().Descriptor;
        var changed = new CraftTextMaskDetectorFactory(
            new CraftTextMaskDetectorOptions { TextThreshold = 0.75f }).Descriptor;

        Assert.Equal(first, second);
        Assert.NotEqual(first.OptionsFingerprint, changed.OptionsFingerprint);
        Assert.Equal(64, first.OptionsFingerprint.Length);
        Assert.Equal("craft.onnx", first.DefaultModelFileName);
    }
}
