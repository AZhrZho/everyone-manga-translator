using MangaTranslator.Api.Jobs;

namespace MangaTranslator.Api.Tests;

public sealed class ProjectOperationGateTests
{
    [Fact]
    public async Task SerializesSameProjectAndAllowsDifferentProjects()
    {
        var gate = new ProjectOperationGate();
        using var first = await gate.EnterAsync("project-a", CancellationToken.None);
        var sameProject = gate.EnterAsync("project-a", CancellationToken.None).AsTask();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var differentProject = await gate.EnterAsync("project-b", timeout.Token);
        Assert.False(sameProject.IsCompleted);

        first.Dispose();
        using var second = await sameProject.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
