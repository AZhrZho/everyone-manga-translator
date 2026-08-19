using System.Collections.Concurrent;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalProjectEditLock : IProjectEditLock
{
    private readonly ConcurrentDictionary<string, byte> lockedProjects = new(StringComparer.Ordinal);

    public bool IsLocked(string projectId) => lockedProjects.ContainsKey(projectId);

    public IDisposable AcquireExport(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!lockedProjects.TryAdd(projectId, 0))
        {
            throw new InvalidOperationException("该项目已有导出任务正在运行。");
        }

        return new Lease(lockedProjects, projectId);
    }

    private sealed class Lease(
        ConcurrentDictionary<string, byte> locks,
        string projectId) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                locks.TryRemove(projectId, out _);
            }
        }
    }
}
