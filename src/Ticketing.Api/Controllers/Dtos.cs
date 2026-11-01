using System.ComponentModel.DataAnnotations;

namespace Ticketing.Api.Controllers;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    string? OrganizerCode);

public record LoginRequest([Required] string Email, [Required] string Password);

public record AuthResponse(string Token, string Email, string Role);

public record CreateEventRequest(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(200)] string Venue,
    DateTimeOffset StartsAt,
    [Range(1, 100)] int Rows,
    [Range(1, 100)] int SeatsPerRow,
    [Range(0, 10_000_000)] int PriceCents);

public record EventResponse(int Id, string Title, string Venue, DateTimeOffset StartsAt, int TotalSeats, int AvailableSeats);

public record SeatResponse(int Id, int Row, int Number, int PriceCents, string Status);

public record HoldRequest([Required, MinLength(1), MaxLength(8)] int[] SeatIds);

public record HoldResponse(Guid Id, int EventId, int SeatCount, DateTimeOffset ExpiresAt);

public record OrderResponse(int Id, Guid HoldId, int EventId, int TotalCents, DateTimeOffset CreatedAt);
