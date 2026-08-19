using System.Globalization;
using System.Text.Json;
using MangaTranslator.Api.Fixtures;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Pipeline;

public sealed class FileSystemPipelineArtifactCatalog : IPipelineArtifactCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<FileSystemPipelineArtifactCatalog> logger;
    private readonly IReadOnlyList<PipelineArtifactDescriptor> descriptors;
    private readonly Dictionary<string, PipelineArtifactDescriptor> byId;

    public FileSystemPipelineArtifactCatalog(ILogger<FileSystemPipelineArtifactCatalog> logger)
    {
        this.logger = logger;
        descriptors = Discover();
        byId = descriptors
            .SelectMany(item => new[]
            {
                new KeyValuePair<string, PipelineArtifactDescriptor>(item.RunId, item),
                new KeyValuePair<string, PipelineArtifactDescriptor>(item.BookId, item)
            })
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Value.CompletedAt).First().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PipelineArtifactDescriptor> List() => descriptors;

    public bool TryGet(string runIdOrBookId, out PipelineArtifactDescriptor? descriptor) =>
        byId.TryGetValue(runIdOrBookId, out descriptor);

    public PipelineArtifactDescriptor? MatchProject(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return descriptors
            .Where(item => item.SourcePageCount == project.Pages.Count)
            .Where(item => MatchesProject(item, project))
            .OrderByDescending(item => item.CompletedAt)
            .FirstOrDefault();
    }

    private IReadOnlyList<PipelineArtifactDescriptor> Discover()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("EMT_PIPELINE_CACHE_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            logger.LogInformation("Pipeline artifact cache is not configured.");
            return [];
        }

        var runsRoot = Path.GetFullPath(configuredRoot);
        var configuredCleaningRoot = Environment.GetEnvironmentVariable("EMT_PIPELINE_CLEANING_CACHE_ROOT");
        var cleaningRoot = string.IsNullOrWhiteSpace(configuredCleaningRoot)
            ? null
            : Path.GetFullPath(configuredCleaningRoot);
        if (!PlatformIoPath.DirectoryExists(runsRoot))
        {
            logger.LogInformation("Pipeline artifact cache root does not exist: {RootPath}", runsRoot);
            return [];
        }

        var result = new List<PipelineArtifactDescriptor>();
        foreach (var runDirectory in PlatformIoPath.EnumerateDirectories(runsRoot))
        {
            var booksDirectory = Path.Combine(runDirectory, "books");
            if (!PlatformIoPath.DirectoryExists(booksDirectory)) continue;
            foreach (var bookDirectory in PlatformIoPath.EnumerateDirectories(booksDirectory))
            {
                try
                {
                    var descriptor = TryReadDescriptor(runDirectory, bookDirectory, cleaningRoot);
                    if (descriptor is not null) result.Add(descriptor);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    logger.LogWarning(exception, "Skipping unreadable pipeline cache {BookPath}.", bookDirectory);
                }
            }
        }

        return result
            .OrderByDescending(item => item.CompletedAt)
            .ToArray();
    }

    private static PipelineArtifactDescriptor? TryReadDescriptor(
        string runDirectory,
        string bookDirectory,
        string? cleaningRoot)
    {
        var summaryPath = Path.Combine(bookDirectory, "summary.json");
        var precheckDirectory = Path.Combine(bookDirectory, "precheck");
        var requiredPrecheck = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_manifest.json"] = Path.Combine(precheckDirectory, "source-manifest.json"),
            ["thumbnail_precheck.json"] = Path.Combine(precheckDirectory, "thumbnail-precheck.json"),
            ["batch_plan.json"] = Path.Combine(precheckDirectory, "batch-plan.json")
        };
        if (!PlatformIoPath.FileExists(summaryPath)
            || requiredPrecheck.Values.Any(path => !PlatformIoPath.FileExists(path)))
        {
            return null;
        }

        using var summaryDocument = JsonDocument.Parse(PlatformIoPath.ReadAllText(summaryPath));
        var summary = summaryDocument.RootElement;
        var bookId = ReadString(summary, "bookId") ?? Path.GetFileName(bookDirectory);
        var runId = ReadString(summary, "runId") ?? Path.GetFileName(runDirectory);
        var sourcePageCount = ReadInt(summary, "sourcePageCount") ?? 0;
        var completedAt = ReadDateTimeOffset(summary, "completedAtUtc")
            ?? new DateTimeOffset(PlatformIoPath.GetLastWriteTimeUtc(summaryPath), TimeSpan.Zero);
        var cleaningPagesPath = cleaningRoot is null
            ? Path.Combine(bookDirectory, "clean")
            : Path.Combine(cleaningRoot, bookId, "pages");
        if (!PlatformIoPath.DirectoryExists(cleaningPagesPath)) return null;

        var precheckArtifacts = new Dictionary<string, string>(requiredPrecheck, StringComparer.OrdinalIgnoreCase);
        AddIfExists(precheckArtifacts, "cross_page_relations.json", Path.Combine(precheckDirectory, "cross-page-relations.json"));
        AddIfExists(precheckArtifacts, "book_manifest.json", Path.Combine(precheckDirectory, "book-manifest.json"));
        AddIfExists(precheckArtifacts, "auto_review.json", Path.Combine(precheckDirectory, "automatic-review.json"));
        AddIfExists(precheckArtifacts, "semantic_merge.json", Path.Combine(precheckDirectory, "semantic-merge.json"));
        AddIfExists(precheckArtifacts, "precheck_result.json", Path.Combine(precheckDirectory, "result.json"));

        var pageArtifacts = new List<PipelinePageArtifact>();
        var sourceHashes = ReadSourceHashes(requiredPrecheck["source_manifest.json"]);
        if (summary.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                var pipelinePageId = ReadString(page, "pageId") ?? string.Empty;
                var pageIndex = PageIndex(pipelinePageId);
                var sourceFile = ReadString(page, "sourceFile") ?? $"page_{pageIndex:0000}.png";
                var pageRoot = Path.Combine(bookDirectory, "pages", pipelinePageId);
                var dialoguePath = Path.Combine(pageRoot, "dialogue-translation.json");
                var detectionPath = Path.Combine(pageRoot, "local-detection.json");
                var maskPath = Path.Combine(bookDirectory, "mask", Path.ChangeExtension(sourceFile, ".png"));
                var cleanPath = Path.Combine(cleaningPagesPath, Path.ChangeExtension(sourceFile, ".png"));
                var sourcePath = Path.Combine(bookDirectory, "source", sourceFile);
                if (pageIndex <= 0
                    || !PlatformIoPath.FileExists(dialoguePath)
                    || !PlatformIoPath.FileExists(maskPath)
                    || !PlatformIoPath.FileExists(cleanPath))
                {
                    continue;
                }

                pageArtifacts.Add(new PipelinePageArtifact(
                    pipelinePageId,
                    pageIndex,
                    sourceFile,
                    dialoguePath,
                    maskPath,
                    cleanPath,
                    PlatformIoPath.FileExists(detectionPath) ? detectionPath : null,
                    PlatformIoPath.FileExists(sourcePath)
                        ? ComputeFileSha256(sourcePath)
                        : sourceHashes.GetValueOrDefault(pageIndex),
                    PlatformIoPath.FileExists(detectionPath) ? ComputeFileSha256(detectionPath) : null,
                    ComputeFileSha256(maskPath),
                    ComputeFileSha256(cleanPath)));
            }
        }

        if (sourcePageCount <= 0 || pageArtifacts.Count == 0) return null;
        var batchCount = ReadArrayLength(requiredPrecheck["batch_plan.json"], "batches");
        var fixture = new PrecheckFixtureDescriptor(
            runId,
            precheckDirectory,
            sourcePageCount,
            batchCount,
            precheckArtifacts.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            completedAt,
            precheckArtifacts);
        return new PipelineArtifactDescriptor(
            runId,
            bookId,
            bookDirectory,
            cleaningPagesPath,
            fixture,
            pageArtifacts.OrderBy(item => item.PageIndex).ToArray(),
            sourcePageCount,
            completedAt);
    }

    private static bool MatchesProject(
        PipelineArtifactDescriptor descriptor,
        ProjectSnapshot project)
    {
        var manifestPath = descriptor.PrecheckFixture.GetArtifactPath("source_manifest.json");
        using var document = JsonDocument.Parse(PlatformIoPath.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array
            || pages.GetArrayLength() != project.Pages.Count)
        {
            return false;
        }

        var projectPages = project.Pages.ToDictionary(item => item.Index);
        foreach (var page in pages.EnumerateArray())
        {
            var index = ReadInt(page, "sourceIndex") ?? PageIndex(ReadString(page, "pageId") ?? string.Empty);
            if (!projectPages.TryGetValue(index, out var projectPage)) return false;
            var fileName = ReadString(page, "fileName");
            var width = ReadInt(page, "width");
            var height = ReadInt(page, "height");
            if (!string.Equals(fileName, projectPage.FileName, StringComparison.OrdinalIgnoreCase)
                || width != projectPage.Width
                || height != projectPage.Height)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIfExists(IDictionary<string, string> artifacts, string name, string path)
    {
        if (PlatformIoPath.FileExists(path)) artifacts[name] = path;
    }

    private static int ReadArrayLength(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(PlatformIoPath.ReadAllText(path));
        return document.RootElement.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static IReadOnlyDictionary<int, string> ReadSourceHashes(string path)
    {
        using var document = JsonDocument.Parse(PlatformIoPath.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<int, string>();
        }

        return pages.EnumerateArray()
            .Select(page => new
            {
                Index = ReadInt(page, "sourceIndex") ?? PageIndex(ReadString(page, "pageId") ?? string.Empty),
                Hash = ReadString(page, "contentHash")
            })
            .Where(item => item.Index > 0 && !string.IsNullOrWhiteSpace(item.Hash))
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.Last().Hash!, EqualityComparer<int>.Default);
    }

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var result)
            ? result
            : null;

    private static string ComputeFileSha256(string path)
    {
        using var stream = PlatformIoPath.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int PageIndex(string pageId)
    {
        var digits = new string(pageId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }
}
