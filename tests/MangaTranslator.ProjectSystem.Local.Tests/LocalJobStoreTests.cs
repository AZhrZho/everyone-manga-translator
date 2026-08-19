using MangaTranslator.ProjectSystem;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class LocalJobStoreTests
{
    [Fact]
    public async Task ReplacingEventsCanInsertBeforeExistingSequences()
    {
        using var harness = await JobStoreHarness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await harness.Jobs.SaveAsync(Snapshot(
            harness.ProjectId,
            now,
            Event("manifest", "manifest", now),
            Event("page-routing", "page-routing", now),
            Event("translation-context", "translation-context", now)), CancellationToken.None);

        await harness.Jobs.SaveAsync(Snapshot(
            harness.ProjectId,
            now.AddSeconds(1),
            Event("manifest", "manifest", now),
            Event("scan-001", "page-scan", now.AddSeconds(1)),
            Event("page-routing", "page-routing", now),
            Event("translation-context", "translation-context", now)), CancellationToken.None);

        var restored = await harness.Jobs.GetAsync(harness.ProjectId, "job-1", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(
            ["manifest", "scan-001", "page-routing", "translation-context"],
            restored.Events.Select(item => item.EventId));
    }

    [Fact]
    public async Task DeleteFinishedForProjectKeepsActiveAndStoppedJobs()
    {
        using var harness = await JobStoreHarness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var (jobId, status) in new[]
                 {
                     ("completed", PersistentJobStatus.Completed),
                     ("failed", PersistentJobStatus.Failed),
                     ("running", PersistentJobStatus.Running),
                     ("stopped", PersistentJobStatus.Stopped)
                 })
        {
            await harness.Jobs.SaveAsync(Snapshot(
                harness.ProjectId,
                now,
                Event("event", "stage", now)) with
            {
                JobId = jobId,
                Status = status
            }, CancellationToken.None);
        }

        var deleted = await harness.Jobs.DeleteFinishedForProjectAsync(
            harness.ProjectId,
            CancellationToken.None);
        var remaining = await harness.Jobs.ListForProjectAsync(
            harness.ProjectId,
            CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(
            ["running", "stopped"],
            remaining.Select(item => item.JobId).Order(StringComparer.Ordinal));
    }

    private static PersistentJobSnapshot Snapshot(
        string projectId,
        DateTimeOffset updatedAt,
        params PersistentProcessEvent[] events) => new(
        "job-1",
        projectId,
        "Precheck",
        PersistentJobStatus.Running,
        "Live",
        "run-1",
        50,
        events,
        updatedAt.AddMinutes(-1),
        updatedAt);

    private static PersistentProcessEvent Event(
        string eventId,
        string stage,
        DateTimeOffset updatedAt) => new(
        eventId,
        stage,
        eventId,
        $"request:{eventId}",
        $"response:{eventId}",
        null,
        PersistentProcessEventStatus.Completed,
        stage == "page-scan" ? "page-scan" : null,
        [],
        updatedAt);

    private sealed class JobStoreHarness : IDisposable
    {
        private readonly string root;

        private JobStoreHarness(string root, string projectId, LocalJobStore jobs)
        {
            this.root = root;
            ProjectId = projectId;
            Jobs = jobs;
        }

        public string ProjectId { get; }

        public LocalJobStore Jobs { get; }

        public static async Task<JobStoreHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"emt-job-store-tests-{Guid.NewGuid():N}");
            var userDataRoot = Path.Combine(root, "user-data");
            var projectParent = Path.Combine(root, "projects");
            Directory.CreateDirectory(userDataRoot);
            Directory.CreateDirectory(projectParent);
            var grants = new LocalPathGrantRegistry();
            var recent = new LocalRecentProjectRegistry(new LocalProjectSystemOptions(userDataRoot));
            var projects = new LocalProjectStore(grants, recent, NullLogger<LocalProjectStore>.Instance);
            var destination = grants.Register(projectParent, ProjectLocationKind.ProjectParent);
            var project = await projects.CreateAsync(
                new CreateProjectCommand("Job store test", destination.Token),
                CancellationToken.None);
            var jobs = new LocalJobStore(recent, NullLogger<LocalJobStore>.Instance);
            return new JobStoreHarness(root, project.ProjectId, jobs);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
