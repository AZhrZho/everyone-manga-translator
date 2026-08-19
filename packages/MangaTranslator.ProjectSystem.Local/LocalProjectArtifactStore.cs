using Microsoft.EntityFrameworkCore;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalProjectArtifactStore(LocalRecentProjectRegistry recentProjects) : IProjectArtifactStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(
        string projectId,
        string name,
        string json,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var record = await context.ProjectArtifacts.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Name == name,
                cancellationToken);
            if (record is not null && string.Equals(record.Json, json, StringComparison.Ordinal))
            {
                return;
            }

            if (record is null)
            {
                context.ProjectArtifacts.Add(new ProjectArtifactRecord
                {
                    Id = $"artifact-{Guid.NewGuid():N}",
                    ProjectId = projectId,
                    Name = name,
                    Json = json,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                record.Json = json;
                record.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var records = await context.ProjectArtifacts
                .AsNoTracking()
                .Where(item => item.ProjectId == projectId)
                .ToArrayAsync(cancellationToken);
            return records.Select(item => new ProjectArtifact(item.Name, item.Json, item.UpdatedAt)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> UpdateAsync(
        string projectId,
        string name,
        string json,
        int expectedProjectRevision,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await context.Projects.SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
                ?? throw new ProjectNotFoundException(projectId);
            if (project.Revision != expectedProjectRevision)
            {
                throw new ProjectRevisionConflictException(project.Revision);
            }

            var record = await context.ProjectArtifacts.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Name == name,
                cancellationToken)
                ?? throw new ProjectFormatException($"项目中不存在预处理结果 {name}。");
            record.Json = json;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return project.Revision;
        }
        finally
        {
            _gate.Release();
        }
    }
}
