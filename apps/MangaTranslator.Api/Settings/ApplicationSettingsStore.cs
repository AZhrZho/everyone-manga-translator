using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaTranslator.Api.Storage;
using MangaTranslator.Contracts;
using MangaTranslator.FontManager;
using MangaTranslator.ProjectSystem;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.AspNetCore.DataProtection;
using SkiaSharp;

namespace MangaTranslator.Api.Settings;

public interface IApplicationSettingsStore
{
    Task<AppSettingsResponse> GetAsync(CancellationToken cancellationToken);

    Task<AppSettingsResponse> UpdateAsync(
        UpdateAppSettingsRequest request,
        CancellationToken cancellationToken);

    Task<AppSettingsResponse> ImportFontsAsync(
        ImportFontsRequest request,
        CancellationToken cancellationToken);

    Task<AppSettingsResponse> RemoveFontAsync(
        string fontId,
        CancellationToken cancellationToken);
}

public interface IWorkflowModelSettingsResolver
{
    Task<WorkflowModelSettings> ResolveAsync(
        string stageId,
        CancellationToken cancellationToken);
}

public sealed record WorkflowModelSettings(
    string StageId,
    string ProviderId,
    ProviderEndpointType EndpointType,
    Uri Endpoint,
    string ApiKey,
    string ModelId);

public sealed class LocalApplicationSettingsStore(
    AppDataPaths paths,
    LocalPathGrantRegistry grants,
    IDataProtectionProvider protection,
    FontCatalogService fonts) : IApplicationSettingsStore, IWorkflowModelSettingsResolver
{
    public const int DefaultTranslationBatchConcurrency = 16;
    public const int MaximumTranslationBatchConcurrency = 16;
    public const int DefaultTranslationBatchSize = 1;
    public const int MaximumTranslationBatchSize = 16;
    public const int DefaultPrecheckBatchConcurrency = 8;
    public const int MaximumPrecheckBatchConcurrency = 8;
    public const int DefaultPrecheckBatchSize = 10;
    public const int MaximumPrecheckBatchSize = 24;
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDataProtector protector = protection.CreateProtector("emt.provider.api-key.v1");
    private string SettingsPath => Path.Combine(paths.UserDataRoot, "settings.json");

    public async Task<AppSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return ToResponse(await LoadAsync(cancellationToken), fonts.List());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsResponse> UpdateAsync(
        UpdateAppSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            var providers = (request.Providers ?? [])
                .Where(provider => !string.IsNullOrWhiteSpace(provider.ProviderId))
                .Select(provider =>
                {
                    var existing = current.Providers.FirstOrDefault(item => item.ProviderId == provider.ProviderId);
                    var protectedKey = existing?.ApiKeyProtected;
                    if (provider.ClearApiKey)
                    {
                        protectedKey = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                    {
                        protectedKey = protector.Protect(provider.ApiKey.Trim());
                    }

                    return new StoredProvider(
                        provider.ProviderId.Trim(),
                        string.IsNullOrWhiteSpace(provider.Name) ? provider.ProviderId.Trim() : provider.Name.Trim(),
                        provider.EndpointType,
                        NormalizeBaseUrl(provider.BaseUrl),
                        protectedKey,
                        provider.Models
                            .Where(model => !string.IsNullOrWhiteSpace(model.ModelId))
                            .Select(model => new StoredModel(model.ModelId.Trim(), string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId.Trim() : model.DisplayName.Trim(), model.SupportsImageInput))
                            .DistinctBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                            .ToList());
                })
                .DistinctBy(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var stageIds = FontCatalogService.StageDefinitions.Select(item => item.StageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stages = FontCatalogService.StageDefinitions
                .Select(definition =>
                {
                    var incoming = (request.Stages ?? []).FirstOrDefault(item => string.Equals(item.StageId, definition.StageId, StringComparison.OrdinalIgnoreCase));
                    var provider = providers.FirstOrDefault(item => item.ProviderId == incoming?.ProviderId);
                    var model = provider?.Models.FirstOrDefault(item => item.ModelId == incoming?.ModelId);
                    if (provider is null || model is null || (RequiresImageInput(definition) && !model.SupportsImageInput)) return new StoredStage(definition.StageId, null, null);
                    return new StoredStage(definition.StageId, provider.ProviderId, model.ModelId);
                })
                .ToList();
            var availableFontIds = fonts.List().Select(font => font.FontId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fontLanguageProfiles = (request.FontLanguageProfiles ?? [])
                .Where(profile => !string.IsNullOrWhiteSpace(profile.TargetLanguage))
                .Select(profile =>
                {
                    var allowed = (profile.AllowedFontIds ?? [])
                        .Where(availableFontIds.Contains)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var defaultFontId = allowed.Contains(profile.DefaultFontId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        ? profile.DefaultFontId
                        : null;
                    return new StoredFontLanguageProfile(profile.TargetLanguage.Trim(), allowed, defaultFontId);
                })
                .DistinctBy(profile => profile.TargetLanguage, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var updated = new StoredSettings(
                request.Theme is "light" or "dark" ? request.Theme : "dark",
                request.Locale is "zh-CN" or "en-US" ? request.Locale : "zh-CN",
                providers,
                stages,
                availableFontIds.Contains(request.DefaultFontId ?? string.Empty) ? request.DefaultFontId : null,
                availableFontIds.Contains(request.FallbackFontId ?? string.Empty) ? request.FallbackFontId : null,
                fontLanguageProfiles,
                NormalizeTranslationBatchConcurrency(request.TranslationBatchConcurrency),
                NormalizeTranslationBatchSize(request.TranslationBatchSize),
                TranslationInputResolution.Original,
                null,
                NormalizeLocalTextDetectionEngine(request.LocalTextDetectionEngine),
                NormalizePrecheckBatchConcurrency(request.PrecheckBatchConcurrency),
                NormalizePrecheckBatchSize(request.PrecheckBatchSize));
            await SaveAsync(updated, cancellationToken);
            return ToResponse(updated, fonts.List());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsResponse> ImportFontsAsync(
        ImportFontsRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            var importedFontIds = new List<string>();
            foreach (var token in request.SourceTokens ?? [])
            {
                var sourcePath = grants.Resolve(token, MangaTranslator.ProjectSystem.ProjectLocationKind.ImportFont);
                if (!FontCatalogService.IsSupportedFontPath(sourcePath))
                {
                    throw new ArgumentException("仅支持 TTF、OTF、TTC 和 OTC 字体文件。");
                }
                importedFontIds.Add(fonts.AddImported(sourcePath).FontId);
            }

            if (string.IsNullOrWhiteSpace(request.TargetLanguage) || importedFontIds.Count == 0)
            {
                return ToResponse(current, fonts.List());
            }

            var fontList = fonts.List();
            var profiles = current.FontLanguageProfiles.ToList();
            if (profiles.Count == 0)
            {
                var initialFontIds = fontList.Select(font => font.FontId).ToList();
                profiles.Add(new StoredFontLanguageProfile(
                    "zh-Hans",
                    initialFontIds,
                    initialFontIds.FirstOrDefault()));
            }

            var targetLanguage = request.TargetLanguage.Trim();
            var existingIndex = profiles.FindIndex(profile =>
                string.Equals(profile.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = profiles[existingIndex];
                profiles[existingIndex] = existing with
                {
                    AllowedFontIds = existing.AllowedFontIds
                        .Concat(importedFontIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
            else
            {
                profiles.Add(new StoredFontLanguageProfile(
                    targetLanguage,
                    importedFontIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    importedFontIds[0]));
            }

            var updated = current with { FontLanguageProfiles = profiles };
            await SaveAsync(updated, cancellationToken);
            return ToResponse(updated, fontList);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsResponse> RemoveFontAsync(
        string fontId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (!fonts.RemoveCustomFont(fontId))
            {
                throw new ArgumentException("只能删除已导入的自定义字体。");
            }

            var updated = current with
            {
                DefaultFontId = string.Equals(current.DefaultFontId, fontId, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : current.DefaultFontId,
                FallbackFontId = string.Equals(current.FallbackFontId, fontId, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : current.FallbackFontId,
                FontLanguageProfiles = current.FontLanguageProfiles.Select(profile => profile with
                {
                    AllowedFontIds = profile.AllowedFontIds
                        .Where(id => !string.Equals(id, fontId, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    DefaultFontId = string.Equals(profile.DefaultFontId, fontId, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : profile.DefaultFontId
                }).ToList()
            };
            await SaveAsync(updated, cancellationToken);
            return ToResponse(updated, fonts.List());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkflowModelSettings> ResolveAsync(
        string stageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadAsync(cancellationToken);
            var stage = settings.Stages.FirstOrDefault(item =>
                string.Equals(item.StageId, stageId, StringComparison.OrdinalIgnoreCase));
            if (stage?.ProviderId is null || stage.ModelId is null)
            {
                throw new InvalidOperationException($"阶段 {stageId} 尚未配置模型。");
            }

            var provider = settings.Providers.FirstOrDefault(item =>
                string.Equals(item.ProviderId, stage.ProviderId, StringComparison.OrdinalIgnoreCase));
            var model = provider?.Models.FirstOrDefault(item =>
                string.Equals(item.ModelId, stage.ModelId, StringComparison.OrdinalIgnoreCase));
            var definition = FontCatalogService.StageDefinitions.FirstOrDefault(item =>
                string.Equals(item.StageId, stageId, StringComparison.OrdinalIgnoreCase));
            if (provider is null || model is null || definition is null || (RequiresImageInput(definition) && !model.SupportsImageInput))
            {
                throw new InvalidOperationException($"阶段 {stageId} 的 Provider 或模型配置无效。");
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKeyProtected))
            {
                throw new InvalidOperationException($"Provider {provider.Name} 尚未配置 API Key。");
            }

            string apiKey;
            try
            {
                apiKey = protector.Unprotect(provider.ApiKeyProtected);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidOperationException(
                    $"Provider {provider.Name} 的 API Key 无法解密，请重新保存该 Provider。",
                    exception);
            }

            return new WorkflowModelSettings(
                stage.StageId,
                provider.ProviderId,
                provider.EndpointType ?? ProviderEndpointType.OpenAIResponses,
                NormalizeEndpoint(provider.BaseUrl, provider.EndpointType ?? ProviderEndpointType.OpenAIResponses),
                apiKey,
                model.ModelId);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool RequiresImageInput(FontCatalogService.StageDefinition definition) =>
        string.Equals(definition.RequiredModelType, "image-input", StringComparison.OrdinalIgnoreCase);

    private async Task<StoredSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath)) return StoredSettings.Default;
        await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(
            stream,
            SettingsJsonOptions,
            cancellationToken);
        return stored is null
            ? StoredSettings.Default
            : stored with
            {
                Providers = (stored.Providers ?? []).Select(provider => provider with
                {
                    EndpointType = provider.EndpointType ?? ProviderEndpointType.OpenAIResponses
                }).ToList(),
                Stages = stored.Stages ?? FontCatalogService.StageDefinitions
                    .Select(item => new StoredStage(item.StageId, null, null))
                    .ToList(),
                FontLanguageProfiles = stored.FontLanguageProfiles ?? []
            };
    }

    private async Task SaveAsync(StoredSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SettingsJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private AppSettingsResponse ToResponse(StoredSettings settings, IReadOnlyList<FontInfoResponse> fontList) => new(
        settings.Theme,
        settings.Locale is "en-US" ? "en-US" : "zh-CN",
        settings.Providers.Select(provider => new ProviderSettingsResponse(
            provider.ProviderId,
            provider.Name,
            provider.EndpointType ?? ProviderEndpointType.OpenAIResponses,
            provider.BaseUrl,
            !string.IsNullOrWhiteSpace(provider.ApiKeyProtected),
            provider.Models.Select(model => new ModelSettingsResponse(model.ModelId, model.DisplayName, model.SupportsImageInput)).ToArray())).ToArray(),
        FontCatalogService.StageDefinitions.Select(definition =>
        {
            var selected = settings.Stages.FirstOrDefault(stage => stage.StageId == definition.StageId);
            return new StageModelSelectionResponse(definition.StageId, definition.DisplayName, definition.RequiredModelType, selected?.ProviderId, selected?.ModelId);
        }).ToArray(),
        settings.DefaultFontId,
        settings.FallbackFontId ?? fonts.ResolvePreferredFallbackFontId(),
        ToFontLanguageProfiles(settings, fontList),
        fontList,
        NormalizeTranslationBatchConcurrency(
            settings.TranslationBatchConcurrency ?? settings.TranslationPageConcurrency),
        NormalizeTranslationBatchSize(settings.TranslationBatchSize),
        NormalizeLocalTextDetectionEngine(settings.LocalTextDetectionEngine),
        NormalizePrecheckBatchConcurrency(settings.PrecheckBatchConcurrency),
        NormalizePrecheckBatchSize(settings.PrecheckBatchSize));

    private static IReadOnlyList<FontLanguageProfileResponse> ToFontLanguageProfiles(
        StoredSettings settings,
        IReadOnlyList<FontInfoResponse> fontList)
    {
        if (settings.FontLanguageProfiles.Count > 0)
        {
            return settings.FontLanguageProfiles.Select(profile => new FontLanguageProfileResponse(
                profile.TargetLanguage,
                profile.AllowedFontIds,
                profile.DefaultFontId)).ToArray();
        }

        var fontIds = fontList.Select(font => font.FontId).ToArray();
        return
        [
            new FontLanguageProfileResponse("zh-Hans", fontIds, fontIds.FirstOrDefault())
        ];
    }

    private static int NormalizeTranslationBatchConcurrency(int? value) =>
        Math.Clamp(value ?? DefaultTranslationBatchConcurrency, 1, MaximumTranslationBatchConcurrency);

    private static int NormalizeTranslationBatchSize(int? value) =>
        Math.Clamp(value ?? DefaultTranslationBatchSize, 1, MaximumTranslationBatchSize);

    private static int NormalizePrecheckBatchConcurrency(int? value) =>
        Math.Clamp(value ?? DefaultPrecheckBatchConcurrency, 1, MaximumPrecheckBatchConcurrency);

    private static int NormalizePrecheckBatchSize(int? value) =>
        Math.Clamp(value ?? DefaultPrecheckBatchSize, 1, MaximumPrecheckBatchSize);

    private static LocalTextDetectionEngine NormalizeLocalTextDetectionEngine(
        LocalTextDetectionEngine? value) => value switch
        {
            null => LocalTextDetectionEngine.Ctd,
            LocalTextDetectionEngine.Ctd => LocalTextDetectionEngine.Ctd,
            LocalTextDetectionEngine.Craft => LocalTextDetectionEngine.Craft,
            LocalTextDetectionEngine.BrowserCraft => LocalTextDetectionEngine.BrowserCraft,
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown local text detection engine.")
        };

    private static string NormalizeBaseUrl(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Provider Base URL 必须是 http 或 https 地址。");
        }
        return candidate.TrimEnd('/');
    }

    private static Uri NormalizeEndpoint(string baseUrl, ProviderEndpointType endpointType)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        var suffix = endpointType switch
        {
            ProviderEndpointType.OpenAIResponses => "/responses",
            ProviderEndpointType.OpenAIChatCompletions => "/chat/completions",
            ProviderEndpointType.AnthropicMessages => "/messages",
            _ => throw new ArgumentOutOfRangeException(nameof(endpointType), endpointType, null)
        };
        if (uri.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var normalized = baseUrl.TrimEnd('/');
        return uri.AbsolutePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(normalized + suffix)
            : new Uri(normalized + "/v1" + suffix);
    }

    private sealed record StoredSettings(
        string Theme,
        string? Locale,
        List<StoredProvider> Providers,
        List<StoredStage> Stages,
        string? DefaultFontId,
        string? FallbackFontId,
        List<StoredFontLanguageProfile> FontLanguageProfiles,
        int? TranslationBatchConcurrency,
        int? TranslationBatchSize,
        TranslationInputResolution? TranslationInputResolution,
        // Read-only migration slot for settings written before batch translation was introduced.
        int? TranslationPageConcurrency,
        LocalTextDetectionEngine? LocalTextDetectionEngine,
        int? PrecheckBatchConcurrency,
        int? PrecheckBatchSize)
    {
        public static StoredSettings Default => new(
            "light",
            "zh-CN",
            [],
            FontCatalogService.StageDefinitions.Select(item => new StoredStage(item.StageId, null, null)).ToList(),
            null,
            null,
            [],
            DefaultTranslationBatchConcurrency,
            DefaultTranslationBatchSize,
            MangaTranslator.Contracts.TranslationInputResolution.Original,
            null,
            MangaTranslator.Contracts.LocalTextDetectionEngine.Ctd,
            DefaultPrecheckBatchConcurrency,
            DefaultPrecheckBatchSize);
    }

    private sealed record StoredProvider(
        string ProviderId,
        string Name,
        ProviderEndpointType? EndpointType,
        string BaseUrl,
        string? ApiKeyProtected,
        List<StoredModel> Models);

    private sealed record StoredModel(string ModelId, string DisplayName, bool SupportsImageInput);

    private sealed record StoredStage(string StageId, string? ProviderId, string? ModelId);

    private sealed record StoredFontLanguageProfile(
        string TargetLanguage,
        List<string> AllowedFontIds,
        string? DefaultFontId);
}

public sealed class FontCatalogService
{
    private static readonly string[] PreferredFallbackNames =
    [
        "NotoSansSC-VF",
        "Noto Sans CJK SC",
        "msyh",
        "Microsoft YaHei",
        "YouYuan",
        "SIMYOU",
        "simhei",
        "Arial",
        "arial",
        "DejaVuSans"
    ];

    public string? ResolvePreferredFallbackFontId()
    {
        var fonts = List();
        foreach (var preferred in PreferredFallbackNames)
        {
            var match = fonts.FirstOrDefault(font =>
                font.DisplayName.Contains(preferred, StringComparison.OrdinalIgnoreCase)
                || font.FileName.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.FontId;
        }

        return null;
    }

    public static IReadOnlyList<StageDefinition> StageDefinitions { get; } =
    [
        new("precheck-manifest", "扫描", "image-input"),
        new("precheck-analysis", "预处理", "text-input"),
        new("translation", "翻译", "image-input")
    ];

    private readonly FontLibrary library;

    public FontCatalogService(IHostEnvironment environment, AppDataPaths paths)
        : this(
            ResolveManagedFontLibraryRoot(paths, environment.ContentRootPath),
            ResolveWindowsFontsRoot())
    {
    }

    public FontCatalogService(AppDataPaths paths)
        : this(
            ResolveManagedFontLibraryRoot(paths, AppContext.BaseDirectory),
            ResolveWindowsFontsRoot())
    {
    }

    internal FontCatalogService(AppDataPaths paths, string contentRootPath, string windowsFontsRoot)
        : this(ResolveManagedFontLibraryRoot(paths, contentRootPath), windowsFontsRoot)
    {
    }

    private FontCatalogService(string libraryRoot)
        : this(libraryRoot, null)
    {
    }

    private FontCatalogService(string libraryRoot, string? windowsFontsRoot)
    {
        library = new FontLibrary(libraryRoot);
        TryImportWindowsYouYuan(windowsFontsRoot);
    }

    public string LibraryRoot => library.RootDirectory;

    public int LibraryRevision => library.ReadCatalog().LibraryRevision;

    public IReadOnlyList<FontInfoResponse> List() => library.ReadCatalog().Fonts
        .Select(font => new FontInfoResponse(
            font.FontId,
            font.DisplayName,
            font.Regular?.OriginalFileName ?? font.DisplayName,
            font.Source.Kind,
            string.Equals(font.Source.Kind, "user-import", StringComparison.OrdinalIgnoreCase)))
        .Take(500)
        .ToArray();

    public FontEntry? Find(string fontId) => library.ReadCatalog().Fonts.FirstOrDefault(font =>
        string.Equals(font.FontId, fontId, StringComparison.OrdinalIgnoreCase));

    public FontEntry AddImported(string sourcePath) => library.Add(new AddFontRequest
    {
        DisplayName = Path.GetFileNameWithoutExtension(sourcePath),
        SourceKind = "user-import",
        SourceReference = sourcePath,
        Regular = new FontStyleInput { SourcePath = sourcePath }
    });

    public bool RemoveCustomFont(string fontId)
    {
        var font = Find(fontId);
        return font is not null
            && string.Equals(font.Source.Kind, "user-import", StringComparison.OrdinalIgnoreCase)
            && library.Remove(font.FontId);
    }

    public FontFileReference Resolve(
        string fontId,
        string? defaultFontId,
        int requestedWeight,
        bool italic)
    {
        var fonts = library.ReadCatalog().Fonts;
        var requestedId = string.IsNullOrWhiteSpace(fontId) ? "default" : fontId.Trim();
        var match = !string.Equals(requestedId, "default", StringComparison.OrdinalIgnoreCase)
            ? fonts.FirstOrDefault(font => string.Equals(font.FontId, requestedId, StringComparison.OrdinalIgnoreCase))
            : null;
        match ??= fonts.FirstOrDefault(font =>
            !string.IsNullOrWhiteSpace(defaultFontId)
            && string.Equals(font.FontId, defaultFontId, StringComparison.OrdinalIgnoreCase));
        match ??= fonts.FirstOrDefault();
        if (match is null)
        {
            throw new InvalidOperationException("No managed font is available for result rendering. Add a font to the font library first.");
        }

        var asset = italic && match.Italic is not null
            ? match.Italic
            : requestedWeight >= 600 && match.Bold is not null ? match.Bold : match.Regular;
        if (asset is null)
        {
            throw new InvalidOperationException($"Managed font '{match.DisplayName}' has no regular face.");
        }
        var nativeWeight = asset.DetectedWeight ?? requestedWeight;
        var syntheticItalic = italic && !string.Equals(asset.DetectedSlant, "italic", StringComparison.OrdinalIgnoreCase);
        return new FontFileReference(
            library.ResolveManagedPath(asset.RelativePath),
            asset.FaceIndex,
            nativeWeight > 0 ? nativeWeight : Math.Clamp(requestedWeight, 1, 1000),
            syntheticItalic);
    }

    public static bool IsSupportedFontPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";

    public sealed record StageDefinition(string StageId, string DisplayName, string RequiredModelType);

    public sealed record FontFileReference(
        string FilePath,
        int FaceIndex,
        int NativeWeight,
        bool SyntheticItalic);

    private void TryImportWindowsYouYuan(string? windowsFontsRoot)
    {
        if (string.IsNullOrWhiteSpace(windowsFontsRoot)) return;

        try
        {
            var catalog = library.ReadCatalog();
            if (catalog.Fonts.Any(font =>
                    string.Equals(font.FamilyName, "YouYuan", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(font.Regular?.OriginalFileName, "SIMYOU.TTF", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var sourcePath = Path.Combine(windowsFontsRoot, "SIMYOU.TTF");
            if (!File.Exists(sourcePath)) return;

            library.Add(new AddFontRequest
            {
                FontId = catalog.Fonts.Any(font => string.Equals(font.FontId, "F0001", StringComparison.OrdinalIgnoreCase))
                    ? null
                    : "F0001",
                DisplayName = "幼圆",
                Category = "简体中文圆体",
                Tags = ["zh-Hans", "rounded", "system"],
                SourceKind = "system",
                SourceReference = "Windows Fonts",
                LicenseNote = "Copied locally from the user's existing Windows installation; not distributed by EMT Studio.",
                Regular = new FontStyleInput { SourcePath = sourcePath }
            });
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or FontLibraryException
            or InvalidDataException)
        {
            // Optional system-font discovery must never prevent the service from starting.
        }
    }

    private static string? ResolveWindowsFontsRoot()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY")))
        {
            return null;
        }

        var configured = Environment.GetEnvironmentVariable("EMT_WINDOWS_FONTS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts)
            : null;
    }

    private static string ResolveManagedFontLibraryRoot(
        AppDataPaths paths,
        string contentRootPath)
    {
        var configured = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_FONT_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var managedRoot = Path.Combine(paths.UserDataRoot, "font-library");
        if (File.Exists(Path.Combine(managedRoot, "font_catalog.json"))) return managedRoot;

        var builtInRoot = TryResolveBuiltInFontLibraryRoot(contentRootPath);
        if (builtInRoot is not null)
        {
            SeedManagedLibrary(builtInRoot, managedRoot);
        }
        else
        {
            // A clean open-source installation does not bundle fonts. Start with
            // a writable empty library and let the user import licensed fonts.
            new FontLibrary(managedRoot).Initialize();
        }

        return managedRoot;
    }

    private static string? TryResolveBuiltInFontLibraryRoot(string contentRootPath)
    {
        var candidates = new[]
        {
            Path.Combine(contentRootPath, "font-library"),
            Path.Combine(contentRootPath, "resources", "font-library"),
            Path.Combine(AppContext.BaseDirectory, "font-library")
        };

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "font_catalog.json")));
    }

    private static void SeedManagedLibrary(string sourceRoot, string managedRoot)
    {
        var catalogPath = Path.Combine(managedRoot, "font_catalog.json");
        if (File.Exists(catalogPath)) return;

        Directory.CreateDirectory(managedRoot);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            if (string.Equals(relativePath, "font_catalog.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relativePath, ".font-library.lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.Combine(managedRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another app process seeded the same immutable built-in asset first.
            }
        }

        var temporaryCatalog = Path.Combine(managedRoot, $".font_catalog.seed-{Guid.NewGuid():N}.json");
        try
        {
            File.Copy(Path.Combine(sourceRoot, "font_catalog.json"), temporaryCatalog, overwrite: true);
            try
            {
                File.Move(temporaryCatalog, catalogPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(catalogPath))
            {
                // Another app process completed the seed atomically.
            }
        }
        finally
        {
            if (File.Exists(temporaryCatalog)) File.Delete(temporaryCatalog);
        }
    }
}
