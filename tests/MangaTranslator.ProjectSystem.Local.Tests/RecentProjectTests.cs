using MangaTranslator.ProjectSystem;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class RecentProjectTests
{
    [Fact]
    public async Task MissingProjectIsRemovedFromRecentRegistry()
    {
        using var harness = new Harness();
        var project = await harness.CreateProjectAsync("Missing project");
        SqliteConnection.ClearAllPools();
        Directory.Delete(project.LocationLabel, recursive: true);

        var recent = await harness.Store.ListRecentAsync(CancellationToken.None);

        Assert.Empty(recent);
        Assert.Empty(await harness.Registry.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemovingRecentEntryCanKeepOrDeleteProjectFiles()
    {
        using var harness = new Harness();
        var kept = await harness.CreateProjectAsync("Keep files");

        await harness.Store.RemoveRecentAsync(kept.ProjectId, false, CancellationToken.None);

        Assert.True(Directory.Exists(kept.LocationLabel));
        Assert.Empty(await harness.Store.ListRecentAsync(CancellationToken.None));

        var deleted = await harness.CreateProjectAsync("Delete files");
        await harness.Store.RemoveRecentAsync(deleted.ProjectId, true, CancellationToken.None);

        Assert.False(Directory.Exists(deleted.LocationLabel));
        Assert.Empty(await harness.Store.ListRecentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposedProjectAccessDoesNotKeepDatabaseFileLocked()
    {
        using var harness = new Harness();
        var project = await harness.CreateProjectAsync("Disposable database access");

        _ = await harness.Store.GetAsync(project.ProjectId, CancellationToken.None);

        File.Delete(Path.Combine(project.LocationLabel, "project.db"));

        Assert.False(File.Exists(Path.Combine(project.LocationLabel, "project.db")));
    }

    [Fact]
    public async Task PhysicalDeleteRetriesATransientDatabaseFileLock()
    {
        using var harness = new Harness();
        var project = await harness.CreateProjectAsync("Transient database lock");
        var databasePath = Path.Combine(project.LocationLabel, "project.db");
        var blocker = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(400);
            await blocker.DisposeAsync();
        });

        await harness.Store.RemoveRecentAsync(project.ProjectId, true, CancellationToken.None);
        await releaseTask;

        Assert.False(Directory.Exists(project.LocationLabel));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "emt-recent-tests",
            Guid.NewGuid().ToString("N"));
        private readonly LocalPathGrantRegistry grants = new();

        public Harness()
        {
            Directory.CreateDirectory(root);
            Registry = new LocalRecentProjectRegistry(
                new LocalProjectSystemOptions(Path.Combine(root, "user-data")));
            Store = new LocalProjectStore(
                grants,
                Registry,
                NullLogger<LocalProjectStore>.Instance);
        }

        public LocalRecentProjectRegistry Registry { get; }

        public LocalProjectStore Store { get; }

        public Task<ProjectSnapshot> CreateProjectAsync(string name)
        {
            var parent = Path.Combine(root, "projects");
            Directory.CreateDirectory(parent);
            var grant = grants.Register(parent, ProjectLocationKind.ProjectParent);
            return Store.CreateAsync(
                new CreateProjectCommand(name, grant.Token),
                CancellationToken.None);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
