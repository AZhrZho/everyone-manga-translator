using MangaTranslator.Api.Settings;
using MangaTranslator.Api.Storage;
using MangaTranslator.Contracts;
using MangaTranslator.FontManager;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.AspNetCore.DataProtection;

namespace MangaTranslator.Api.Tests;

public sealed class ApplicationSettingsStoreTests
{
    private static readonly string RepositoryFontLibrary = FindRepositoryFontLibrary();

    [Fact]
    public async Task LocalTextDetectionEngineDefaultsToCtd()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = await CreateStore(root).GetAsync(CancellationToken.None);

            Assert.Equal(
                LocalTextDetectionEngine.Ctd,
                settings.LocalTextDetectionEngine);
            var defaultFontProfile = Assert.Single(settings.FontLanguageProfiles);
            Assert.Equal("zh-Hans", defaultFontProfile.TargetLanguage);
            Assert.NotEmpty(defaultFontProfile.AllowedFontIds);
            Assert.All(
                settings.Fonts,
                font => Assert.Contains(font.FontId, defaultFontProfile.AllowedFontIds));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalTextDetectionEngineCanBeSwitchedAndPersists()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            var updated = await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [],
                    [],
                    null,
                    LocalTextDetectionEngine: LocalTextDetectionEngine.Craft),
                CancellationToken.None);

            Assert.Equal(LocalTextDetectionEngine.Craft, updated.LocalTextDetectionEngine);
            Assert.Equal(
                LocalTextDetectionEngine.Craft,
                (await CreateStore(root).GetAsync(CancellationToken.None)).LocalTextDetectionEngine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserCraftCanBeSelectedAndPersists()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            var updated = await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [],
                    [],
                    null,
                    LocalTextDetectionEngine: LocalTextDetectionEngine.BrowserCraft),
                CancellationToken.None);

            Assert.Equal(LocalTextDetectionEngine.BrowserCraft, updated.LocalTextDetectionEngine);
            Assert.Equal(
                LocalTextDetectionEngine.BrowserCraft,
                (await CreateStore(root).GetAsync(CancellationToken.None)).LocalTextDetectionEngine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TranslationBatchSettingsDefaultAndAreClampedOnSave()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            var defaults = await store.GetAsync(CancellationToken.None);
            Assert.Equal("light", defaults.Theme);
            Assert.Equal("zh-CN", defaults.Locale);
            Assert.Equal(16, defaults.TranslationBatchConcurrency);
            Assert.Equal(1, defaults.TranslationBatchSize);
            Assert.Equal(8, defaults.PrecheckBatchConcurrency);
            Assert.Equal(10, defaults.PrecheckBatchSize);

            var updated = await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [],
                    [],
                    null,
                    TranslationBatchConcurrency: 99,
                    TranslationBatchSize: 99),
                CancellationToken.None);

            Assert.Equal(16, updated.TranslationBatchConcurrency);
            Assert.Equal(16, updated.TranslationBatchSize);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingSettingsWithoutBatchSettingsUseNewDefaults()
    {
        var root = CreateTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"theme":"light","locale":"en-US","providers":[],"stages":[],"defaultFontId":null,"fontLanguageProfiles":[]}""");

            var settings = await CreateStore(root).GetAsync(CancellationToken.None);

            Assert.Equal(16, settings.TranslationBatchConcurrency);
            Assert.Equal(1, settings.TranslationBatchSize);
            Assert.Equal(8, settings.PrecheckBatchConcurrency);
            Assert.Equal(10, settings.PrecheckBatchSize);
            Assert.Equal("light", settings.Theme);
            Assert.Equal(
                LocalTextDetectionEngine.Ctd,
                settings.LocalTextDetectionEngine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyPageConcurrencyMigratesToBatchConcurrency()
    {
        var root = CreateTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"theme":"dark","locale":"zh-CN","providers":[],"stages":[],"defaultFontId":null,"fontLanguageProfiles":[],"translationPageConcurrency":7}""");

            var settings = await CreateStore(root).GetAsync(CancellationToken.None);

            Assert.Equal(7, settings.TranslationBatchConcurrency);
            Assert.Equal(1, settings.TranslationBatchSize);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingProviderWithoutEndpointTypeDefaultsToOpenAIResponses()
    {
        var root = CreateTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"theme":"dark","locale":"zh-CN","providers":[{"providerId":"legacy","name":"Legacy","baseUrl":"https://api.example.com/v1","apiKeyProtected":null,"models":[]}],"stages":[],"defaultFontId":null,"fontLanguageProfiles":[]}""");

            var settings = await CreateStore(root).GetAsync(CancellationToken.None);

            Assert.Equal(ProviderEndpointType.OpenAIResponses, Assert.Single(settings.Providers).EndpointType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ProviderEndpointType.OpenAIResponses, "https://api.example.com", "https://api.example.com/v1/responses")]
    [InlineData(ProviderEndpointType.OpenAIResponses, "https://api.example.com/v1", "https://api.example.com/v1/responses")]
    [InlineData(ProviderEndpointType.OpenAIResponses, "https://api.example.com/custom/responses", "https://api.example.com/custom/responses")]
    [InlineData(ProviderEndpointType.OpenAIChatCompletions, "https://api.example.com/v1", "https://api.example.com/v1/chat/completions")]
    [InlineData(ProviderEndpointType.AnthropicMessages, "https://api.example.com", "https://api.example.com/v1/messages")]
    public async Task ResolvesConfiguredImageModel(
        ProviderEndpointType endpointType,
        string baseUrl,
        string expectedEndpoint)
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [
                        new ProviderSettingsRequest(
                            "provider-1",
                            "Provider One",
                            endpointType,
                            baseUrl,
                            "secret-key",
                            false,
                            [new ModelSettingsRequest("vision-model", "Vision Model", true)])
                    ],
                    [new StageModelSelectionRequest("precheck-analysis", "provider-1", "vision-model")],
                    null),
                CancellationToken.None);

            var resolved = await store.ResolveAsync("precheck-analysis", CancellationToken.None);

            Assert.Equal("provider-1", resolved.ProviderId);
            Assert.Equal(endpointType, resolved.EndpointType);
            Assert.Equal("vision-model", resolved.ModelId);
            Assert.Equal("secret-key", resolved.ApiKey);
            Assert.Equal(expectedEndpoint, resolved.Endpoint.AbsoluteUri.TrimEnd('/'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsImageStageWithoutImageInputModel()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [
                        new ProviderSettingsRequest(
                            "provider-1",
                            "Provider One",
                            ProviderEndpointType.OpenAIResponses,
                            "https://api.example.com/v1",
                            "secret-key",
                            false,
                            [new ModelSettingsRequest("text-model", "Text Model", false)])
                    ],
                    [new StageModelSelectionRequest("precheck-manifest", "provider-1", "text-model")],
                    null),
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ResolveAsync("precheck-manifest", CancellationToken.None));

            Assert.Contains("尚未配置模型", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsTextOnlyModelForBookContextStage()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [
                        new ProviderSettingsRequest(
                            "provider-1",
                            "Provider One",
                            ProviderEndpointType.OpenAIResponses,
                            "https://api.example.com/v1",
                            "secret-key",
                            false,
                            [new ModelSettingsRequest("text-model", "Text Model", false)])
                    ],
                    [new StageModelSelectionRequest("precheck-analysis", "provider-1", "text-model")],
                    null),
                CancellationToken.None);

            var resolved = await store.ResolveAsync("precheck-analysis", CancellationToken.None);

            Assert.Equal("text-model", resolved.ModelId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsProviderWithoutApiKey()
    {
        var root = CreateTempRoot();
        try
        {
            var store = CreateStore(root);
            await store.UpdateAsync(
                new UpdateAppSettingsRequest(
                    "dark",
                    [
                        new ProviderSettingsRequest(
                            "provider-1",
                            "Provider One",
                            ProviderEndpointType.OpenAIResponses,
                            "https://api.example.com/v1",
                            null,
                            false,
                            [new ModelSettingsRequest("vision-model", "Vision Model", true)])
                    ],
                    [new StageModelSelectionRequest("precheck-analysis", "provider-1", "vision-model")],
                    null),
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ResolveAsync("precheck-analysis", CancellationToken.None));

            Assert.Contains("尚未配置 API Key", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FontCatalogSeedsWritableManagedLibraryInsideAppData()
    {
        var root = CreateTempRoot();
        try
        {
            var contentRoot = Path.Combine(root, "content");
            var builtInRoot = Path.Combine(contentRoot, "resources", "font-library");
            new FontLibrary(builtInRoot).Initialize();
            var previous = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY");
            FontCatalogService catalog;
            try
            {
                Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", null);
                catalog = new FontCatalogService(
                    new AppDataPaths(Path.Combine(root, "app-data")),
                    contentRoot,
                    string.Empty);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", previous);
            }

            Assert.StartsWith(
                Path.GetFullPath(Path.Combine(root, "app-data")),
                catalog.LibraryRoot,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(catalog.LibraryRoot, "font_catalog.json")));
            Assert.Empty(catalog.List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FontCatalogInitializesEmptyLibraryWhenNoBuiltInFontsExist()
    {
        var root = CreateTempRoot();
        try
        {
            var appDataRoot = Path.Combine(root, "app-data");
            var contentRoot = Path.Combine(root, "content-without-fonts");
            Directory.CreateDirectory(contentRoot);
            var previous = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY");
            FontCatalogService catalog;
            try
            {
                Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", null);
                catalog = new FontCatalogService(
                    new AppDataPaths(appDataRoot),
                    contentRoot,
                    string.Empty);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", previous);
            }

            Assert.Equal(
                Path.GetFullPath(Path.Combine(appDataRoot, "font-library")),
                catalog.LibraryRoot);
            Assert.True(File.Exists(Path.Combine(catalog.LibraryRoot, "font_catalog.json")));
            Assert.Empty(catalog.List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FontCatalogImportsWindowsYouYuanWhenItIsAvailable()
    {
        var root = CreateTempRoot();
        try
        {
            var windowsFontsRoot = Path.Combine(root, "windows-fonts");
            Directory.CreateDirectory(windowsFontsRoot);
            File.Copy(
                Path.Combine(FindRepositoryFontLibrary(), "fonts", "F0009", "regular.ttf"),
                Path.Combine(windowsFontsRoot, "SIMYOU.TTF"));

            var catalog = new FontCatalogService(
                new AppDataPaths(Path.Combine(root, "app-data")),
                Path.Combine(root, "content-without-fonts"),
                windowsFontsRoot);

            var imported = Assert.Single(catalog.List());
            Assert.Equal("幼圆", imported.DisplayName);
            Assert.Equal("system", imported.Source);
            Assert.Equal("F0001", imported.FontId, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalApplicationSettingsStore CreateStore(string root)
    {
        var paths = new AppDataPaths(root);
        return new LocalApplicationSettingsStore(
            paths,
            new LocalPathGrantRegistry(),
            new EphemeralDataProtectionProvider(),
            CreateFontCatalog(paths));
    }

    private static FontCatalogService CreateFontCatalog(AppDataPaths paths)
    {
        var previous = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY");
        try
        {
            Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", RepositoryFontLibrary);
            return new FontCatalogService(paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY", previous);
        }
    }

    private static string FindRepositoryFontLibrary()
    {
        var configured = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "font_catalog.json")))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var startingPath in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                     typeof(ApplicationSettingsStoreTests).Assembly.Location
                 })
        {
            var directory = File.Exists(startingPath)
                ? Path.GetDirectoryName(startingPath)
                : startingPath;
            for (var current = new DirectoryInfo(directory!);
                 current is not null;
                 current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "resources", "font-library");
                if (File.Exists(Path.Combine(candidate, "font_catalog.json")))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("The repository font-library fixture was not found.");
    }

    private static string FindRepositoryRoot() =>
        Directory.GetParent(Directory.GetParent(RepositoryFontLibrary)!.FullName)!.FullName;

    private sealed class TestHostEnvironment(string contentRootPath) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Development;
        public string ApplicationName { get; set; } = "MangaTranslator.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"emt-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
