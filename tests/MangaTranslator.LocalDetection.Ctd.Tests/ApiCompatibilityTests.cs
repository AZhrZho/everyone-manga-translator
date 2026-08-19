using System.Reflection;
using Latest = MangaTranslator.LocalDetection.Ctd;
using Legacy = MangaTranslator.LocalDetection;

namespace MangaTranslator.LocalDetection.Ctd.Tests;

public sealed class ApiCompatibilityTests
{
    [Fact]
    public void Latest_detector_implements_the_shared_contract()
    {
        Assert.Contains(
            typeof(ITextMaskDetector),
            typeof(Latest.OnnxTextMaskDetector).GetInterfaces());
    }

    [Fact]
    public void Legacy_assembly_forwards_the_shared_contract_types()
    {
        var forwarded = Type.GetType(
            "MangaTranslator.LocalDetection.DetectionInput, MangaTranslator.LocalDetection",
            throwOnError: true);

        Assert.Same(typeof(DetectionInput), forwarded);
        Assert.Equal(
            "MangaTranslator.LocalDetection.Abstractions",
            forwarded!.Assembly.GetName().Name);
    }

    [Fact]
    public void Latest_options_keep_the_legacy_option_names()
    {
        var legacyNames = PublicInitPropertyNames(typeof(Legacy.OnnxTextMaskDetectorOptions));
        var latestNames = PublicInitPropertyNames(typeof(Latest.OnnxTextMaskDetectorOptions));

        Assert.Subset(latestNames, legacyNames);
    }

    [Fact]
    public void Latest_defaults_match_the_current_upstream_ctd_path()
    {
        var options = new Latest.OnnxTextMaskDetectorOptions();

        Assert.Equal(1024, options.InputSize);
        Assert.Equal(0.3f, options.LineBitmapThreshold);
        Assert.Equal(0.6f, options.LineScoreThreshold);
        Assert.Equal(1000, options.MaxDetections);
        Assert.True(options.IncludeUnknownLanguageBlocks);
        Assert.False(options.KeepUndetectedMask);
        Assert.Equal(400, options.MinimumImageSize);
        Assert.True(options.EnableRearrangedInference);
    }

    [Fact]
    public void Implementation_assembly_does_not_expose_ocr_or_filesystem_services()
    {
        var exportedTypeNames = typeof(Latest.OnnxTextMaskDetector).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.DoesNotContain(exportedTypeNames, name => name.Contains("Ocr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exportedTypeNames, name => name.Contains("File", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> PublicInitPropertyNames(Type type) => type
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.SetMethod is not null)
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
}
