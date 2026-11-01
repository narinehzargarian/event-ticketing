using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Controllers;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

[Collection("app")]
public class BookingTests(TestApp app)
{
    private static async Task<HoldResponse> HoldAsync(HttpClient client, int eventId, params int[] seatIds)
    {
        var response = await client.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HoldResponse>())!;
    }

    private async Task<string> StatusOf(int eventId, int seatId) =>
        (await app.GetSeatsAsync(eventId)).Single(s => s.Id == seatId).Status;

    [Fact]
    public async Task Fifty_customers_race_for_one_seat_and_exactly_one_wins()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => app.NewCustomerAsync()));

        var results = await Task.WhenAll(customers.Select(c => c.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id } })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(49, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Overlapping_group_bookings_are_all_or_nothing()
    {
        var (eventId, seats) = await app.NewEventAsync(rows: 2, seatsPerRow: 10);
        var random = new Random(42);
        var customers = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => app.NewCustomerAsync()));
        var requests = customers.Select(_ => seats.OrderBy(_ => random.Next()).Take(random.Next(2, 5)).Select(s => s.Id).ToArray()).ToArray();

        var results = await Task.WhenAll(customers.Select((c, i) => c.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = requests[i] })));
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));

        var winners = results.Select((r, i) => (r, i)).Where(x => x.r.StatusCode == HttpStatusCode.OK).Select(x => requests[x.i]).ToList();
        Assert.NotEmpty(winners);
        var wonSeats = winners.SelectMany(w => w).ToList();
        Assert.Equal(wonSeats.Count, wonSeats.Distinct().Count());  // No seat was given to two customers.

        // Losing bookings left nothing behind: exactly the winners' seats are held.
        var held = (await app.GetSeatsAsync(eventId)).Where(s => s.Status == "Held").Select(s => s.Id).OrderBy(id => id);
        Assert.Equal(wonSeats.OrderBy(id => id), held);
        var rowsPerHold = await app.WithDb(db => db.Seats.Where(s => s.EventId == eventId && s.HoldId != null).GroupBy(s => s.HoldId).Select(g => g.Count()).ToListAsync());
        Assert.Equal(winners.Select(w => w.Length).OrderBy(n => n), rowsPerHold.OrderBy(n => n));
    }

    [Fact]
    public async Task A_customer_cannot_hold_a_seat_someone_else_holds()
    {
        var (eventId, seats) = await app.NewEventAsync();
        await HoldAsync(await app.NewCustomerAsync(), eventId, seats[0].Id);
        var second = await (await app.NewCustomerAsync()).PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id, seats[1].Id } });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Available", await StatusOf(eventId, seats[1].Id));  // The free seat wasn't taken either.
    }

    [Fact]
    public async Task Purchase_sells_the_seats_and_records_the_total()
    {
        var (eventId, seats) = await app.NewEventAsync(priceCents: 2500);
        var customer = await app.NewCustomerAsync();
        var hold = await HoldAsync(customer, eventId, seats[0].Id, seats[1].Id);

        var response = await customer.PostAsync($"/api/holds/{hold.Id}/purchase", null);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.Equal(5000, order!.TotalCents);
        Assert.Equal("Sold", await StatusOf(eventId, seats[0].Id));
        Assert.Contains(await customer.GetFromJsonAsync<OrderResponse[]>("/api/orders/mine") ?? [], o => o.Id == order.Id);
    }

    [Fact]
    public async Task Concurrent_purchase_requests_create_one_order()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        var hold = await HoldAsync(customer, eventId, seats[0].Id);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => customer.PostAsync($"/api/holds/{hold.Id}/purchase", null)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var orderIds = await Task.WhenAll(results.Select(async r => (await r.Content.ReadFromJsonAsync<OrderResponse>())!.Id));
        Assert.Single(orderIds.Distinct());
        Assert.Equal(1, await app.WithDb(db => db.Orders.CountAsync(o => o.HoldId == hold.Id)));
    }

    [Fact]
    public async Task Someone_else_cannot_purchase_or_release_your_hold()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var hold = await HoldAsync(await app.NewCustomerAsync(), eventId, seats[0].Id);
        var stranger = await app.NewCustomerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/holds/{hold.Id}/purchase", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/holds/{hold.Id}")).StatusCode);
        Assert.Equal("Held", await StatusOf(eventId, seats[0].Id));
    }

    [Fact]
    public async Task Releasing_frees_the_seat_and_ends_the_hold()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        var hold = await HoldAsync(customer, eventId, seats[0].Id);

        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"/api/holds/{hold.Id}")).StatusCode);

        Assert.Equal("Available", await StatusOf(eventId, seats[0].Id));
        Assert.Equal(HttpStatusCode.Gone, (await customer.PostAsync($"/api/holds/{hold.Id}/purchase", null)).StatusCode);
        await HoldAsync(await app.NewCustomerAsync(), eventId, seats[0].Id);
    }

    [Fact]
    public async Task Releasing_a_purchased_hold_does_not_free_sold_seats()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        var hold = await HoldAsync(customer, eventId, seats[0].Id);
        await customer.PostAsync($"/api/holds/{hold.Id}/purchase", null);
        await customer.DeleteAsync($"/api/holds/{hold.Id}");
        Assert.Equal("Sold", await StatusOf(eventId, seats[0].Id));
    }
}

/// <summary>Moves the shared clock, so these tests must not run alongside the others.</summary>
[CollectionDefinition("expiry", DisableParallelization = true)]
public class ExpiryCollection : ICollectionFixture<TestApp>;

[Collection("expiry")]
public class ExpiryTests(TestApp app)
{
    private static async Task<HoldResponse> HoldAsync(HttpClient client, int eventId, params int[] seatIds)
    {
        var response = await client.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HoldResponse>())!;
    }

    [Fact]
    public async Task An_expired_hold_frees_the_seat_even_before_the_worker_runs()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var first = await app.NewCustomerAsync();
        var hold = await HoldAsync(first, eventId, seats[0].Id);

        app.Clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Equal(HttpStatusCode.Conflict, (await (await app.NewCustomerAsync()).PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id } })).StatusCode);

        app.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Available", (await app.GetSeatsAsync(eventId)).Single(s => s.Id == seats[0].Id).Status);
        Assert.Equal(HttpStatusCode.Gone, (await first.PostAsync($"/api/holds/{hold.Id}/purchase", null)).StatusCode);

        var second = await HoldAsync(await app.NewCustomerAsync(), eventId, seats[0].Id);
        Assert.NotEqual(hold.Id, second.Id);
    }

    [Fact]
    public async Task An_old_hold_cannot_take_back_a_seat_from_a_newer_one()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var first = await app.NewCustomerAsync();
        var oldHold = await HoldAsync(first, eventId, seats[0].Id);
        app.Clock.Advance(TimeSpan.FromSeconds(301));
        var second = await app.NewCustomerAsync();
        var newHold = await HoldAsync(second, eventId, seats[0].Id);

        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/holds/{oldHold.Id}")).StatusCode);

        Assert.Equal("Held", (await app.GetSeatsAsync(eventId)).Single(s => s.Id == seats[0].Id).Status);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync($"/api/holds/{newHold.Id}/purchase", null)).StatusCode);
    }
}
