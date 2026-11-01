using Microsoft.AspNetCore.SignalR;
using Ticketing.Api.Services;

namespace Ticketing.Api.Realtime;

public interface ISeatNotifier
{
    Task SeatsChangedAsync(SeatChange change, CancellationToken ct = default);
}

/// <summary>Live seat updates. Clients join one group per event, and only ever receive from the server.</summary>
public class SeatHub : Hub
{
    public static string GroupName(int eventId) => $"event-{eventId}";

    public Task WatchEvent(int eventId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(eventId));

    public Task StopWatchingEvent(int eventId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(eventId));
}

public class SignalRSeatNotifier(IHubContext<SeatHub> hub, ILogger<SignalRSeatNotifier> logger) : ISeatNotifier
{
    public async Task SeatsChangedAsync(SeatChange change, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(SeatHub.GroupName(change.EventId)).SendAsync("SeatsChanged", change, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The database change is already committed, and a missed update must not fail the request.
            // A client that misses one still sees correct state on its next load.
            logger.LogWarning(error, "Could not send seat update for event {EventId}", change.EventId);
        }
    }
}
