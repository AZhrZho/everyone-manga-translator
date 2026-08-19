using MangaTranslator.Api.Jobs;
using MangaTranslator.Contracts;

namespace MangaTranslator.Api.Tests;

public sealed class LivePrecheckExecutionSourceTests
{
    [Fact]
    public async Task ValidatesCreatesPlanAndDelegatesExecution()
    {
        var workflow = new RecordingWorkflow();
        var source = new LivePrecheckExecutionSource(workflow);

        var plan = await source.CreateAsync(
            "project-1",
            "live-run-existing",
            CancellationToken.None);
        var reported = new List<PrecheckExecutionEvent>();
        await source.ExecuteAsync(
            new PrecheckExecutionContext(
                "job-1",
                "project-1",
                plan.FixtureRunId!,
                (item, _) =>
                {
                    reported.Add(item);
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.True(source.DrivesExecution);
        Assert.Equal(ExecutionMode.Live, plan.ExecutionMode);
        Assert.Equal("live-run-existing", plan.FixtureRunId);
        Assert.Equal("project-1", workflow.ValidatedProjectId);
        Assert.Equal("project-1", workflow.ExecutedProjectId);
        Assert.Equal("live-run-existing", workflow.ExecutedRunId);
        Assert.Single(reported);
        Assert.Equal("manifest", reported[0].EventId);
    }

    private sealed class RecordingWorkflow : ILivePrecheckWorkflow
    {
        public string? ValidatedProjectId { get; private set; }
        public string? ExecutedProjectId { get; private set; }
        public string? ExecutedRunId { get; private set; }

        public Task ValidateAsync(string projectId, CancellationToken cancellationToken)
        {
            ValidatedProjectId = projectId;
            return Task.CompletedTask;
        }

        public async Task ExecuteAsync(
            string projectId,
            string runId,
            Func<PrecheckExecutionEvent, CancellationToken, Task> reportAsync,
            CancellationToken cancellationToken)
        {
            ExecutedProjectId = projectId;
            ExecutedRunId = runId;
            await reportAsync(
                new PrecheckExecutionEvent(
                    "manifest",
                    "manifest",
                    "Manifest",
                    "request",
                    "response",
                    "detail",
                    null,
                    [],
                    ProcessEventStatus.Completed),
                cancellationToken);
        }
    }
}
