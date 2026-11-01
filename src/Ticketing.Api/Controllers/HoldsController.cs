using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class HoldsController(AppDbContext db, SeatService seats) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpDelete("holds/{id:guid}")]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct) =>
        await seats.ReleaseAsync(id, UserId, ct) ? NoContent() : NotFound();

    [HttpPost("holds/{id:guid}/purchase")]
    public async Task<ActionResult<OrderResponse>> Purchase(Guid id, CancellationToken ct)
    {
        var (outcome, order) = await seats.PurchaseAsync(id, UserId, ct);
        return outcome switch
        {
            PurchaseOutcome.NotFound => NotFound(),
            PurchaseOutcome.Expired => Problem(statusCode: StatusCodes.Status410Gone, detail: "This hold has ended. Please choose your seats again."),
            _ => Ok(ToResponse(order!)),
        };
    }

    [HttpGet("orders/mine")]
    public async Task<IEnumerable<OrderResponse>> MyOrders(CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Where(o => o.UserId == UserId).OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        return orders.Select(ToResponse);
    }

    private static OrderResponse ToResponse(Order order) => new(order.Id, order.HoldId, order.EventId, order.TotalCents, order.CreatedAt);
}
