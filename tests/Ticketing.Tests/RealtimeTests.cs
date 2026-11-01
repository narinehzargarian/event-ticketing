using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Ticketing.Api.Controllers;
using Ticketing.Api.Services;

namespace Ticketing.Tests;

public class RealtimeTests : IClassFixture<TestAppWithWorker>
{
    private readonly TestAppWithWorker _app;

    public RealtimeTests(TestAppWithWorker app) => _app = app;

    private async Task<(HubConnection Connection, Func<string, Task<SeatChange>> NextWithStatus)> WatchAsync(int eventId)
    {
        var received = new List<SeatChange>();
        var signal = new SemaphoreSlim(0);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_app.Server.BaseAddress, "/hubs/seats"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _app.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        connection.On<SeatChange>("SeatsChanged", change =>
        {
            lock (received)
            {
                received.Add(change);
            }
            signal.Release();
        });
        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvent", eventId);

        async Task<SeatChange> NextWithStatus(string status)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (received)
                {
                    var match = received.FirstOrDefault(c => c.Status == status);
                    if (match is not null)
                    {
                        return match;
                    }
                }
                await signal.WaitAsync(TimeSpan.FromMilliseconds(250));
            }
            throw new TimeoutException($"No '{status}' update arrived.");
        }

        return (connection, NextWithStatus);
    }

    [Fact]
    public async Task Watchers_see_holds_and_sales_live()
    {
        var (eventId, seats) = await _app.NewEventAsync();
        var (connection, next) = await WatchAsync(eventId);
        await using var _ = connection;

        var customer = await _app.NewCustomerAsync();
        var response = await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id } });
        var hold = await response.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal([seats[0].Id], (await next("Held")).SeatIds);

        await customer.PostAsync($"/api/holds/{hold!.Id}/purchase", null);
        Assert.Equal([seats[0].Id], (await next("Sold")).SeatIds);
    }

    [Fact]
    public async Task The_worker_releases_expired_holds_and_tells_watchers()
    {
        var (eventId, seats) = await _app.NewEventAsync();
        var (connection, next) = await WatchAsync(eventId);
        await using var _ = connection;

        var customer = await _app.NewCustomerAsync();
        (await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[3].Id } })).EnsureSuccessStatusCode();
        _app.Clock.Advance(TimeSpan.FromSeconds(301));

        Assert.Equal([seats[3].Id], (await next("Available")).SeatIds);
        Assert.Null(await _app.WithDb(db => Task.FromResult(db.Seats.Single(s => s.Id == seats[3].Id).HoldId)));
    }

    [Fact]
    public async Task Watchers_of_other_events_hear_nothing()
    {
        var (eventId, seats) = await _app.NewEventAsync();
        var (otherEventId, _) = await _app.NewEventAsync();
        var (connection, next) = await WatchAsync(otherEventId);
        await using var _ = connection;

        var customer = await _app.NewCustomerAsync();
        (await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id } })).EnsureSuccessStatusCode();
        await Task.Delay(500);

        var timedOut = await Record.ExceptionAsync(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await next("Held").WaitAsync(cancel.Token);
        });
        Assert.NotNull(timedOut);
    }
}
