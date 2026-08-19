using System.Collections.Concurrent;

namespace MangaTranslator.Api.Jobs;

public sealed class ProjectOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates =
        new(StringComparer.Ordinal);

    public async ValueTask<IDisposable> EnterAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var gate = gates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
