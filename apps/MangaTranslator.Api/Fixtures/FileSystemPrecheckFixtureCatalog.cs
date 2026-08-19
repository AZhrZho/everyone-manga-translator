using System.Text.Json;

namespace MangaTranslator.Api.Fixtures;

public sealed class FileSystemPrecheckFixtureCatalog : IPrecheckFixtureCatalog
{
    private static readonly string[] RequiredArtifacts =
    [
        "source_manifest.json",
        "thumbnail_precheck.json",
        "batch_plan.json"
    ];

    private static readonly string[] KnownArtifacts =
    [
        .. RequiredArtifacts,
        "characters.json",
        "glossary.json",
        "cross_page_relations.json",
        "precheck_conflicts.json",
        "story_bible.json",
        "auto_review.json"
    ];

    private readonly ILogger<FileSystemPrecheckFixtureCatalog> _logger;
    private readonly IReadOnlyList<PrecheckFixtureDescriptor> _fixtures;
    private readonly Dictionary<string, PrecheckFixtureDescriptor> _fixturesById;
    private readonly string? _configuredDefaultRunId;

    public FileSystemPrecheckFixtureCatalog(
        IHostEnvironment environment,
        ILogger<FileSystemPrecheckFixtureCatalog> logger)
    {
        _logger = logger;
        RootPath = ResolveRoot(environment.ContentRootPath);
        _configuredDefaultRunId = Environment.GetEnvironmentVariable("EMT_FIXTURE_RUN_ID");
        _fixtures = DiscoverFixtures();
        _fixturesById = _fixtures.ToDictionary(item => item.RunId, StringComparer.OrdinalIgnoreCase);
    }

    public string RootPath { get; }

    public IReadOnlyList<PrecheckFixtureDescriptor> List() => _fixtures;

    public PrecheckFixtureDescriptor GetDefault()
    {
        if (!string.IsNullOrWhiteSpace(_configuredDefaultRunId)
            && TryGet(_configuredDefaultRunId, out var configured))
        {
            return configured!;
        }

        return _fixtures.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"在 {RootPath} 中没有找到可回放的预处理结果。至少需要 {string.Join("、", RequiredArtifacts)}。");
    }

    public bool TryGet(string runId, out PrecheckFixtureDescriptor? fixture) =>
        _fixturesById.TryGetValue(runId, out fixture);

    private IReadOnlyList<PrecheckFixtureDescriptor> DiscoverFixtures()
    {
        if (!Directory.Exists(RootPath))
        {
            _logger.LogWarning("Precheck fixture root does not exist: {RootPath}", RootPath);
            return [];
        }

        var fixtures = new List<PrecheckFixtureDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            try
            {
                var descriptor = ReadDescriptor(directory);
                if (descriptor is not null)
                {
                    fixtures.Add(descriptor);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(
                    exception,
                    "Skipping unreadable precheck fixture {FixturePath}.",
                    directory);
            }
        }

        return fixtures
            .OrderBy(item => item.PageCount)
            .ThenBy(item => item.BatchCount)
            .ThenBy(item => item.RunId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PrecheckFixtureDescriptor? ReadDescriptor(string directory)
    {
        if (RequiredArtifacts.Any(fileName => !File.Exists(Path.Combine(directory, fileName))))
        {
            return null;
        }

        var manifestPath = Path.Combine(directory, "source_manifest.json");
        var batchPlanPath = Path.Combine(directory, "batch_plan.json");
        var pageCount = ReadArrayLength(manifestPath, "pages");
        var batchCount = ReadArrayLength(batchPlanPath, "batches");
        if (pageCount <= 0 || batchCount <= 0)
        {
            return null;
        }

        var artifacts = KnownArtifacts
            .Where(fileName => File.Exists(Path.Combine(directory, fileName)))
            .ToArray();
        var lastModifiedAt = artifacts
            .Select(fileName => File.GetLastWriteTimeUtc(Path.Combine(directory, fileName)))
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(directory))
            .Max();

        return new PrecheckFixtureDescriptor(
            Path.GetFileName(directory),
            Path.GetFullPath(directory),
            pageCount,
            batchCount,
            artifacts,
            new DateTimeOffset(lastModifiedAt, TimeSpan.Zero));
    }

    private static int ReadArrayLength(string path, string propertyName)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty(propertyName, out var value)
               && value.ValueKind is JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static string ResolveRoot(string contentRootPath)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("EMT_FIXTURE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        for (var current = new DirectoryInfo(contentRootPath); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "precheck-runs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "precheck-runs"));
    }
}
