namespace Ticketing.Api.Data;

public enum SeatStatus { Available, Held, Sold }

public enum HoldStatus { Active, Purchased, Released, Expired }

public static class Roles
{
    public const string Customer = "Customer";
    public const string Organizer = "Organizer";
}

public class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public required string Role { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Venue { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int OrganizerId { get; set; }
    public List<Seat> Seats { get; set; } = [];
}

public class Seat
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int Row { get; set; }
    public int Number { get; set; }
    public int PriceCents { get; set; }
    public SeatStatus Status { get; set; } = SeatStatus.Available;
    public Guid? HoldId { get; set; }
    public DateTimeOffset? HeldUntil { get; set; }
}

public class Hold
{
    public Guid Id { get; set; }
    public int EventId { get; set; }
    public int UserId { get; set; }
    public int SeatCount { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public HoldStatus Status { get; set; } = HoldStatus.Active;
}

public class Order
{
    public int Id { get; set; }
    public Guid HoldId { get; set; }
    public int EventId { get; set; }
    public int UserId { get; set; }
    public int TotalCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
