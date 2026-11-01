using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController(AppDbContext db, TimeProvider clock, SeatService seats) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<EventResponse>> List(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Events.AsNoTracking().OrderBy(e => e.StartsAt)
            .Select(e => new EventResponse(e.Id, e.Title, e.Venue, e.StartsAt, e.Seats.Count,
                e.Seats.Count(s => s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now))))
            .ToListAsync(ct);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Organizer)]
    public async Task<ActionResult<EventResponse>> Create(CreateEventRequest request, CancellationToken ct)
    {
        var entity = new Event
        {
            Title = request.Title, Venue = request.Venue, StartsAt = request.StartsAt,
            OrganizerId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        };
        for (var row = 1; row <= request.Rows; row++)
        {
            for (var number = 1; number <= request.SeatsPerRow; number++)
            {
                entity.Seats.Add(new Seat { Row = row, Number = number, PriceCents = request.PriceCents });
            }
        }
        db.Events.Add(entity);
        await db.SaveChangesAsync(ct);
        var total = request.Rows * request.SeatsPerRow;
        return CreatedAtAction(nameof(GetSeats), new { id = entity.Id },
            new EventResponse(entity.Id, entity.Title, entity.Venue, entity.StartsAt, total, total));
    }

    [HttpGet("{id:int}/seats")]
    public async Task<ActionResult<IEnumerable<SeatResponse>>> GetSeats(int id, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, ct))
        {
            return NotFound();
        }
        var now = clock.GetUtcNow();
        var result = await db.Seats.AsNoTracking().Where(s => s.EventId == id).OrderBy(s => s.Row).ThenBy(s => s.Number)
            .Select(s => new { s.Id, s.Row, s.Number, s.PriceCents, s.Status, s.HeldUntil }).ToListAsync(ct);
        return result.Select(s => new SeatResponse(s.Id, s.Row, s.Number, s.PriceCents,
            (s.Status == SeatStatus.Held && s.HeldUntil <= now ? SeatStatus.Available : s.Status).ToString())).ToList();
    }

    [HttpPost("{id:int}/holds")]
    [Authorize]
    public async Task<ActionResult<HoldResponse>> Hold(int id, HoldRequest request, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, ct))
        {
            return NotFound();
        }
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var (outcome, hold) = await seats.HoldSeatsAsync(id, userId, request.SeatIds, ct);
        if (outcome == HoldOutcome.SeatsUnavailable)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: "One or more of those seats are no longer available.");
        }
        return Ok(new HoldResponse(hold!.Id, hold.EventId, hold.SeatCount, hold.ExpiresAt));
    }
}
