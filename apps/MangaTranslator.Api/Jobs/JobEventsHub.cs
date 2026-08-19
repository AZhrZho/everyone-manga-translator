using MangaTranslator.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MangaTranslator.Api.Jobs;

public sealed class JobEventsHub : Hub
{
    public Task Subscribe(string jobId) => Groups.AddToGroupAsync(Context.ConnectionId, jobId);

    public Task Unsubscribe(string jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
}

public interface IJobUpdatePublisher
{
    Task PublishAsync(JobSnapshotResponse snapshot, CancellationToken cancellationToken);
}

public sealed class SignalRJobUpdatePublisher(
    IHubContext<JobEventsHub> hubContext) : IJobUpdatePublisher
{
    public Task PublishAsync(
        JobSnapshotResponse snapshot,
        CancellationToken cancellationToken) =>
        hubContext.Clients.Group(snapshot.JobId)
            .SendAsync("JobUpdated", snapshot, cancellationToken);
}
