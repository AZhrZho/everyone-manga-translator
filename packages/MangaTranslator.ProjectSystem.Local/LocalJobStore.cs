using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalJobStore(
    LocalRecentProjectRegistry recentProjects,
    ILogger<LocalJobStore> logger) : IJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(PersistentJobSnapshot snapshot, CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(snapshot.ProjectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var record = await context.Jobs
                .Include(item => item.Events)
                .SingleOrDefaultAsync(item => item.Id == snapshot.JobId, cancellationToken);
            if (record is null)
            {
                record = new JobRecord
                {
                    Id = snapshot.JobId,
                    ProjectId = snapshot.ProjectId,
                    Kind = snapshot.Kind,
                    Status = snapshot.Status,
                    ExecutionMode = snapshot.ExecutionMode,
                    FixtureRunId = snapshot.FixtureRunId,
                    ProgressPercent = snapshot.ProgressPercent,
                    SettingsJson = snapshot.SettingsJson,
                    CreatedAt = snapshot.CreatedAt,
                    UpdatedAt = snapshot.UpdatedAt
                };
                context.Jobs.Add(record);
            }
            else
            {
                record.Kind = snapshot.Kind;
                record.Status = snapshot.Status;
                record.ExecutionMode = snapshot.ExecutionMode;
                record.FixtureRunId = snapshot.FixtureRunId;
                record.ProgressPercent = snapshot.ProgressPercent;
                record.SettingsJson = snapshot.SettingsJson;
                record.UpdatedAt = snapshot.UpdatedAt;
                context.JobEvents.RemoveRange(record.Events);
                record.Events.Clear();
                // Flush deletions before reusing (JobId, Sequence) values. SQLite
                // may otherwise insert a newly ordered event before deleting the
                // old row that currently owns the same unique sequence.
                await context.SaveChangesAsync(cancellationToken);
            }

            record.Events.AddRange(snapshot.Events.Select((item, index) => new JobEventRecord
            {
                Id = $"{snapshot.JobId}:{item.EventId}",
                EventId = item.EventId,
                JobId = snapshot.JobId,
                Sequence = index,
                Stage = item.Stage,
                Title = item.Title,
                RequestSummary = item.RequestSummary,
                ResponseSummary = item.ResponseSummary,
                Detail = item.Detail,
                Status = item.Status,
                ParallelGroup = item.ParallelGroup,
                PageIndicesJson = JsonSerializer.Serialize(item.PageIndices, JsonOptions),
                UpdatedAt = item.UpdatedAt
            }));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistentJobSnapshot?> GetAsync(
        string projectId,
        string jobId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var record = await context.Jobs
                .AsNoTracking()
                .Include(item => item.Events.OrderBy(eventItem => eventItem.Sequence))
                .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == jobId, cancellationToken);
            return record is null ? null : ToSnapshot(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistentJobSnapshot?> FindAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var entries = await recentProjects.ListAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in entries.Where(item => Directory.Exists(item.ProjectRoot)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var context = ProjectDbContextFactory.Create(entry.ProjectRoot);
                    var record = await context.Jobs
                        .AsNoTracking()
                        .Include(item => item.Events.OrderBy(eventItem => eventItem.Sequence))
                        .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
                    if (record is not null) return ToSnapshot(record);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Unable to inspect jobs from project {ProjectRoot}.", entry.ProjectRoot);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistentJobSnapshot>> ListForProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var records = await context.Jobs
                .AsNoTracking()
                .Include(item => item.Events.OrderBy(eventItem => eventItem.Sequence))
                .Where(item => item.ProjectId == projectId)
                .ToArrayAsync(cancellationToken);
            return records
                .OrderByDescending(item => item.UpdatedAt)
                .Select(ToSnapshot)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistentJobSnapshot>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var entries = await recentProjects.ListAsync(cancellationToken);
        var result = new List<PersistentJobSnapshot>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in entries.Where(item => Directory.Exists(item.ProjectRoot)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var context = ProjectDbContextFactory.Create(entry.ProjectRoot);
                    var records = await context.Jobs
                        .AsNoTracking()
                        .Include(item => item.Events.OrderBy(eventItem => eventItem.Sequence))
                        .Where(item => item.Status == PersistentJobStatus.Pending
                                       || item.Status == PersistentJobStatus.Running
                                       || item.Status == PersistentJobStatus.StopRequested)
                        .ToArrayAsync(cancellationToken);
                    result.AddRange(records.Select(ToSnapshot));
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Unable to restore jobs from project {ProjectRoot}.", entry.ProjectRoot);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return result.OrderBy(item => item.CreatedAt).ToArray();
    }

    public async Task<int> DeleteFinishedForProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var records = await context.Jobs
                .Include(item => item.Events)
                .Where(item => item.ProjectId == projectId
                               && (item.Status == PersistentJobStatus.Completed
                                   || item.Status == PersistentJobStatus.Failed))
                .ToArrayAsync(cancellationToken);
            if (records.Length == 0) return 0;

            context.Jobs.RemoveRange(records);
            await context.SaveChangesAsync(cancellationToken);
            return records.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PersistentJobSnapshot ToSnapshot(JobRecord record) => new(
        record.Id,
        record.ProjectId,
        record.Kind,
        record.Status,
        record.ExecutionMode,
        record.FixtureRunId,
        record.ProgressPercent,
        record.Events.OrderBy(item => item.Sequence).Select(item => new PersistentProcessEvent(
            item.EventId,
            item.Stage,
            item.Title,
            item.RequestSummary,
            item.ResponseSummary,
            item.Detail,
            item.Status,
            item.ParallelGroup,
            JsonSerializer.Deserialize<int[]>(item.PageIndicesJson, JsonOptions) ?? [],
            item.UpdatedAt)).ToArray(),
        record.CreatedAt,
        record.UpdatedAt,
        record.SettingsJson);
}
