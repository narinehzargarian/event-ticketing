using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Controllers;

namespace Ticketing.Tests;

[Collection("app")]
public class ApiTests(TestApp app)
{
    [Fact]
    public async Task Duplicate_email_is_rejected_regardless_of_case()
    {
        var client = app.CreateClient();
        var email = $"{Guid.NewGuid():N}@Test.Example";
        (await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse" })).EnsureSuccessStatusCode();
        var again = await client.PostAsJsonAsync("/api/auth/register", new { email = email.ToUpperInvariant(), password = "correct-horse" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "correct-horse")]
    [InlineData("a@test.example", "short")]
    public async Task Bad_registration_is_rejected(string email, string password)
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_gives_a_working_token_and_rejects_wrong_passwords()
    {
        var client = app.CreateClient();
        var email = $"{Guid.NewGuid():N}@test.example";
        (await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse" })).EnsureSuccessStatusCode();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.example", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var good = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse" });
        var auth = await good.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth!.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders/mine")).StatusCode);
    }

    [Fact]
    public async Task Wrong_organizer_code_is_rejected()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { email = $"{Guid.NewGuid():N}@test.example", password = "correct-horse", organizerCode = "guess" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Only_organizers_can_create_events()
    {
        var body = new { title = "T", venue = "V", startsAt = DateTimeOffset.UtcNow, rows = 1, seatsPerRow = 1, priceCents = 100 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/events", body)).StatusCode);
        var customer = await app.NewCustomerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/events", body)).StatusCode);
    }

    [Fact]
    public async Task Creating_an_event_creates_its_seats()
    {
        var (eventId, seats) = await app.NewEventAsync(rows: 3, seatsPerRow: 4, priceCents: 999);
        Assert.Equal(12, seats.Length);
        Assert.All(seats, s => Assert.Equal("Available", s.Status));
        Assert.All(seats, s => Assert.Equal(999, s.PriceCents));
        Assert.Equal(12, seats.Select(s => (s.Row, s.Number)).Distinct().Count());

        var events = await app.CreateClient().GetFromJsonAsync<EventResponse[]>("/api/events");
        var listed = Assert.Single(events!, e => e.Id == eventId);
        Assert.Equal(12, listed.AvailableSeats);
    }

    [Fact]
    public async Task Event_size_limits_are_enforced()
    {
        var organizer = await app.NewOrganizerAsync();
        var tooBig = await organizer.PostAsJsonAsync("/api/events",
            new { title = "T", venue = "V", startsAt = DateTimeOffset.UtcNow, rows = 101, seatsPerRow = 1, priceCents = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
    }

    [Fact]
    public async Task Holding_requires_login_and_a_real_event()
    {
        var (eventId, seats) = await app.NewEventAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateClient().PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id } })).StatusCode);
        var customer = await app.NewCustomerAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.PostAsJsonAsync("/api/events/999999/holds", new { seatIds = new[] { seats[0].Id } })).StatusCode);
    }

    [Fact]
    public async Task Hold_size_limits_are_enforced()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = Array.Empty<int>() })).StatusCode);
        var nine = seats.Take(9).Select(s => s.Id).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = nine })).StatusCode);
    }

    [Fact]
    public async Task Seat_from_another_event_cannot_be_held()
    {
        var (eventId, _) = await app.NewEventAsync();
        var (_, otherSeats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        var response = await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { otherSeats[0].Id } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_seat_ids_count_once()
    {
        var (eventId, seats) = await app.NewEventAsync();
        var customer = await app.NewCustomerAsync();
        var response = await customer.PostAsJsonAsync($"/api/events/{eventId}/holds", new { seatIds = new[] { seats[0].Id, seats[0].Id } });
        var hold = await response.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal(1, hold!.SeatCount);
    }

    [Fact]
    public async Task Health_endpoint_works()
    {
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/health")).StatusCode);
    }
}
