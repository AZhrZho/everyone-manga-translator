using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Storage;
using MangaTranslator.LocalDetection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MangaTranslator.Api.Tests;

[Collection("Text detector model environment")]
public sealed class TextDetectorModelLocatorTests
{
    private static readonly TextMaskDetectorDescriptor Craft = new(
        TextDetectorIds.Craft,
        "craft-test-v1",
        "craft-options",
        "craft.onnx");

    private static readonly TextMaskDetectorDescriptor Ctd = new(
        TextDetectorIds.Ctd,
        "ctd-test-v1",
        "ctd-options",
        "comictextdetector.pt.onnx");

    [Fact]
    public void ResolvesEngineSpecificConfiguredModelsIndependently()
    {
        using var fixture = new LocatorFixture();
        var craft = fixture.CreateFile("configured", "craft-custom.onnx");
        var ctd = fixture.CreateFile("configured", "ctd-custom.onnx");
        Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_CRAFT_MODEL", craft);
        Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_CTD_MODEL", ctd);

        var locator = fixture.CreateLocator();

        Assert.Equal(Path.GetFullPath(craft), locator.TryResolve(Craft));
        Assert.Equal(Path.GetFullPath(ctd), locator.TryResolve(Ctd));
    }

    [Fact]
    public void RejectsMissingConfiguredModelWithEngineSpecificVariable()
    {
        using var fixture = new LocatorFixture();
        var configured = Path.Combine(fixture.Root, "missing", "craft.onnx");
        Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_CRAFT_MODEL", configured);

        var exception = Assert.Throws<FileNotFoundException>(() =>
            fixture.CreateLocator().TryResolve(Craft));

        Assert.Equal(Path.GetFullPath(configured), exception.FileName);
        Assert.Contains("MANGA_TRANSLATOR_CRAFT_MODEL", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("craft", "craft.onnx")]
    [InlineData("ctd", "comictextdetector.pt.onnx")]
    public void ResolvesEachModelFromPlatformUserDataDirectory(
        string detectorId,
        string fileName)
    {
        using var fixture = new LocatorFixture();
        var descriptor = detectorId == TextDetectorIds.Craft ? Craft : Ctd;
        var expected = fixture.CreateFile("user-data/models", fileName);
        var locator = fixture.CreateLocator();

        Assert.Equal(Path.GetFullPath(expected), locator.TryResolve(descriptor));
        Assert.Equal(Path.GetFullPath(expected), locator.DefaultInstallPath(descriptor));
    }

    [Theory]
    [InlineData("craft", "craft.onnx")]
    [InlineData("ctd", "comictextdetector.pt.onnx")]
    public void ResolvesEachModelFromRepositoryResourcesDirectory(
        string detectorId,
        string fileName)
    {
        using var fixture = new LocatorFixture();
        var descriptor = detectorId == TextDetectorIds.Craft ? Craft : Ctd;
        var expected = fixture.CreateFile("repository/resources/models", fileName);

        Assert.Equal(
            Path.GetFullPath(expected),
            fixture.CreateLocator("repository/apps/api").TryResolve(descriptor));
    }

    [Fact]
    public void LegacyGenericVariableRemainsCtdOnly()
    {
        using var fixture = new LocatorFixture();
        var configured = fixture.CreateFile("configured", "legacy.onnx");
        Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_DETECTOR_MODEL", configured);

        var locator = fixture.CreateLocator();

        Assert.Equal(Path.GetFullPath(configured), locator.TryResolve(Ctd));
        Assert.Null(locator.TryResolve(Craft));
    }

    [Fact]
    public void ReportsEngineSpecificConfigurationHints()
    {
        using var fixture = new LocatorFixture();
        var locator = fixture.CreateLocator();

        Assert.Equal("MANGA_TRANSLATOR_CRAFT_MODEL", locator.EnvironmentVariableName(Craft));
        Assert.Equal("MANGA_TRANSLATOR_CTD_MODEL", locator.EnvironmentVariableName(Ctd));
    }

    private sealed class LocatorFixture : IDisposable
    {
        private static readonly string[] Variables =
        [
            "MANGA_TRANSLATOR_CRAFT_MODEL",
            "MANGA_TRANSLATOR_CTD_MODEL",
            "MANGA_TRANSLATOR_DETECTOR_MODEL"
        ];

        public LocatorFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"emt-detector-locator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            foreach (var variable in Variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        public string Root { get; }

        public TextDetectorModelLocator CreateLocator(string contentRoot = "content")
        {
            var resolvedContentRoot = Path.Combine(Root, contentRoot.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(resolvedContentRoot);
            return new TextDetectorModelLocator(
                new TestHostEnvironment(resolvedContentRoot),
                new AppDataPaths(Path.Combine(Root, "user-data")));
        }

        public string CreateFile(string directory, string fileName)
        {
            var resolvedDirectory = Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(resolvedDirectory);
            var path = Path.Combine(resolvedDirectory, fileName);
            File.WriteAllText(path, "test model");
            return path;
        }

        public void Dispose()
        {
            foreach (var variable in Variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MangaTranslator.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition("Text detector model environment", DisableParallelization = true)]
public sealed class TextDetectorModelEnvironmentFixture;
