using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ticketing.Api.Controllers;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

/// <summary>Runs the real app against a real SQL Server, in its own throwaway database.</summary>
public class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OrganizerCode = "test-organizer-code";
    private static readonly string SqlServer = Environment.GetEnvironmentVariable("TEST_SQL_SERVER")
        ?? "Server=localhost,1434;User Id=sa;Password=LocalDev_Passw0rd!;TrustServerCertificate=True";

    private readonly string _database = $"ticketing_test_{Guid.NewGuid():N}";

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected virtual bool RunWorker => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"{SqlServer};Database={_database}");
        builder.UseSetting("Jwt:Key", "test-only-key-that-is-long-enough-for-hmac");
        builder.UseSetting("Auth:OrganizerCode", OrganizerCode);
        builder.UseSetting("RateLimit:AuthPerMinute", "100000");
        builder.UseSetting("Holds:RunWorker", RunWorker.ToString());
        builder.UseSetting("Holds:SweepSeconds", "0.2");
        builder.UseSetting("Holds:LengthSeconds", "300");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public Task InitializeAsync()
    {
        _ = CreateClient();  // Starts the app, whose migration creates the database.
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<HttpClient> NewCustomerAsync()
    {
        var client = CreateClient();
        var email = $"{Guid.NewGuid():N}@test.example";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse" });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    public async Task<HttpClient> NewOrganizerAsync()
    {
        var client = CreateClient();
        var email = $"{Guid.NewGuid():N}@test.example";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "correct-horse", organizerCode = OrganizerCode });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    public async Task<(int EventId, SeatResponse[] Seats)> NewEventAsync(int rows = 2, int seatsPerRow = 5, int priceCents = 2500)
    {
        var organizer = await NewOrganizerAsync();
        var response = await organizer.PostAsJsonAsync("/api/events", new
        {
            title = "Test Concert", venue = "Test Hall", startsAt = DateTimeOffset.UtcNow.AddDays(30), rows, seatsPerRow, priceCents,
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EventResponse>();
        return (created!.Id, await GetSeatsAsync(created.Id));
    }

    public async Task<SeatResponse[]> GetSeatsAsync(int eventId)
    {
        var seats = await CreateClient().GetFromJsonAsync<SeatResponse[]>($"/api/events/{eventId}/seats");
        return seats!;
    }
}

public class TestAppWithWorker : TestApp
{
    protected override bool RunWorker => true;
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<TestApp>;
