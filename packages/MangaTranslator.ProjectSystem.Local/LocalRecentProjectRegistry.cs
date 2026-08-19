using System.Text.Json;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalRecentProjectRegistry(LocalProjectSystemOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<IReadOnlyList<RecentProjectEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).Projects
                .OrderByDescending(item => item.LastOpenedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<string> GetRootAsync(string projectId, CancellationToken cancellationToken)
    {
        var entries = await ListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(item => string.Equals(item.ProjectId, projectId, StringComparison.Ordinal));
        if (entry is null || !Directory.Exists(entry.ProjectRoot))
        {
            throw new ProjectNotFoundException(projectId);
        }

        return Path.GetFullPath(entry.ProjectRoot);
    }

    internal async Task<RecentProjectEntry?> FindAsync(string projectId, CancellationToken cancellationToken)
    {
        var entries = await ListAsync(cancellationToken);
        return entries.FirstOrDefault(item => string.Equals(item.ProjectId, projectId, StringComparison.Ordinal));
    }

    internal async Task RemoveAsync(IEnumerable<string> projectIds, CancellationToken cancellationToken)
    {
        var ids = projectIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            document.Projects.RemoveAll(item => ids.Contains(item.ProjectId));
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task UpsertAsync(
        string projectRoot,
        ProjectRecord project,
        int pageCount,
        bool markOpened,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            var existing = document.Projects.FirstOrDefault(item =>
                string.Equals(item.ProjectId, project.Id, StringComparison.Ordinal));
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                document.Projects.Add(new RecentProjectEntry
                {
                    ProjectId = project.Id,
                    ProjectRoot = Path.GetFullPath(projectRoot),
                    Name = project.Name,
                    SourceKind = project.SourceKind,
                    PageCount = pageCount,
                    CreatedAt = project.CreatedAt,
                    UpdatedAt = project.UpdatedAt,
                    LastOpenedAt = now
                });
            }
            else
            {
                existing.ProjectRoot = Path.GetFullPath(projectRoot);
                existing.Name = project.Name;
                existing.SourceKind = project.SourceKind;
                existing.PageCount = pageCount;
                existing.CreatedAt = project.CreatedAt;
                existing.UpdatedAt = project.UpdatedAt;
                if (markOpened)
                {
                    existing.LastOpenedAt = now;
                }
            }

            document.Projects = document.Projects
                .OrderByDescending(item => item.LastOpenedAt)
                .Take(50)
                .ToList();
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecentProjectsDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.RecentProjectsPath))
        {
            return new RecentProjectsDocument();
        }

        try
        {
            await using var stream = File.OpenRead(options.RecentProjectsPath);
            return await JsonSerializer.DeserializeAsync<RecentProjectsDocument>(stream, JsonOptions, cancellationToken)
                   ?? new RecentProjectsDocument();
        }
        catch (JsonException)
        {
            return new RecentProjectsDocument();
        }
    }

    private async Task WriteAsync(RecentProjectsDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.NormalizedUserDataRoot);
        var temporaryPath = options.RecentProjectsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, options.RecentProjectsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class RecentProjectsDocument
    {
        public string SchemaVersion { get; set; } = "1.0.0";
        public List<RecentProjectEntry> Projects { get; set; } = [];
    }
}

internal sealed class RecentProjectEntry
{
    public required string ProjectId { get; set; }
    public required string ProjectRoot { get; set; }
    public required string Name { get; set; }
    public ProjectSourceKind SourceKind { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastOpenedAt { get; set; }

    public ProjectListItem ToListItem(bool isAvailable) => new(
        ProjectId,
        Name,
        SourceKind,
        PageCount,
        ProjectRoot,
        isAvailable,
        CreatedAt,
        UpdatedAt,
        LastOpenedAt);
}
