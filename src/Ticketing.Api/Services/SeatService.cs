using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Realtime;

namespace Ticketing.Api.Services;

public enum HoldOutcome { Created, SeatsUnavailable }

public enum PurchaseOutcome { Purchased, AlreadyPurchased, NotFound, Expired }

public record SeatChange(int EventId, IReadOnlyList<int> SeatIds, string Status);

/// <summary>
/// Seat state changes. Each one is a single conditional UPDATE, so SQL Server decides the winner:
/// reading seats first and then writing would let two customers both see "available".
/// </summary>
public class SeatService(AppDbContext db, TimeProvider clock, ISeatNotifier notifier, IConfiguration config)
{
    public const int MaxSeatsPerHold = 8;

    private TimeSpan HoldLength => TimeSpan.FromSeconds(config.GetValue("Holds:LengthSeconds", 300));

    public async Task<(HoldOutcome Outcome, Hold? Hold)> HoldSeatsAsync(int eventId, int userId, IReadOnlyList<int> seatIds, CancellationToken ct = default)
    {
        var distinct = seatIds.Distinct().ToArray();
        var now = clock.GetUtcNow();
        var expiresAt = now + HoldLength;

        // Deadlocks between overlapping holds are possible, and the execution strategy retries them.
        var hold = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = new Hold
            {
                Id = Guid.NewGuid(), EventId = eventId, UserId = userId, SeatCount = distinct.Length, ExpiresAt = expiresAt,
            };
            db.Holds.Add(created);
            await db.SaveChangesAsync(ct);

            // A seat whose hold has lapsed counts as free, whether or not the worker has cleaned it up.
            var claimed = await db.Seats
                .Where(s => distinct.Contains(s.Id) && s.EventId == eventId
                    && (s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now)))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Status, SeatStatus.Held)
                    .SetProperty(s => s.HoldId, created.Id)
                    .SetProperty(s => s.HeldUntil, expiresAt), ct);

            // All or nothing. Leaving the block without committing rolls back any seats claimed.
            if (claimed != distinct.Length)
            {
                return null;
            }
            await transaction.CommitAsync(ct);
            return created;
        });

        if (hold is null)
        {
            return (HoldOutcome.SeatsUnavailable, null);
        }
        await notifier.SeatsChangedAsync(new SeatChange(eventId, distinct, "Held"), ct);
        return (HoldOutcome.Created, hold);
    }

    public async Task<bool> ReleaseAsync(Guid holdId, int userId, CancellationToken ct = default)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.UserId == userId, ct);
        if (hold is null)
        {
            return false;
        }
        var released = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.Holds
                .Where(h => h.Id == holdId && h.Status == HoldStatus.Active)
                .ExecuteUpdateAsync(set => set.SetProperty(h => h.Status, HoldStatus.Released), ct);
            if (updated == 0)
            {
                return Array.Empty<int>();
            }
            var seatIds = await FreeSeatsAsync([holdId], ct);
            await transaction.CommitAsync(ct);
            return seatIds;
        });
        if (released.Length > 0)
        {
            await notifier.SeatsChangedAsync(new SeatChange(hold.EventId, released, "Available"), ct);
        }
        return true;
    }

    /// <summary>
    /// Completes a purchase. Payment is simulated: this project never handles real money.
    /// Calling it twice for the same hold returns the same order, so a retried request can't charge twice.
    /// </summary>
    public async Task<(PurchaseOutcome Outcome, Order? Order)> PurchaseAsync(Guid holdId, int userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.UserId == userId, ct);
        if (hold is null)
        {
            return (PurchaseOutcome.NotFound, null);
        }

        var order = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Only one concurrent request can move the hold out of Active.
            var won = await db.Holds
                .Where(h => h.Id == holdId && h.Status == HoldStatus.Active && h.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(h => h.Status, HoldStatus.Purchased), ct);
            if (won == 0)
            {
                return null;
            }
            var sold = await db.Seats
                .Where(s => s.HoldId == holdId && s.Status == SeatStatus.Held)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SeatStatus.Sold).SetProperty(s => s.HeldUntil, (DateTimeOffset?)null), ct);
            if (sold != hold.SeatCount)
            {
                return null;  // A seat was lost to someone else, so nothing is committed.
            }
            var total = await db.Seats.Where(s => s.HoldId == holdId).SumAsync(s => s.PriceCents, ct);
            var created = new Order { HoldId = holdId, EventId = hold.EventId, UserId = userId, TotalCents = total, CreatedAt = now };
            db.Orders.Add(created);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return created;
        });

        if (order is not null)
        {
            var seatIds = await db.Seats.Where(s => s.HoldId == holdId).Select(s => s.Id).ToListAsync(ct);
            await notifier.SeatsChangedAsync(new SeatChange(hold.EventId, seatIds, "Sold"), ct);
            return (PurchaseOutcome.Purchased, order);
        }
        var existing = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.HoldId == holdId, ct);
        return existing is not null ? (PurchaseOutcome.AlreadyPurchased, existing) : (PurchaseOutcome.Expired, null);
    }

    /// <summary>Releases seats from lapsed holds. Returns how many holds expired.</summary>
    public async Task<int> ExpireDueHoldsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Holds.AsNoTracking()
            .Where(h => h.Status == HoldStatus.Active && h.ExpiresAt <= now)
            .OrderBy(h => h.ExpiresAt).Take(500)
            .Select(h => new { h.Id, h.EventId }).ToListAsync(ct);
        foreach (var hold in due)
        {
            var seatIds = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var updated = await db.Holds
                    .Where(h => h.Id == hold.Id && h.Status == HoldStatus.Active)
                    .ExecuteUpdateAsync(set => set.SetProperty(h => h.Status, HoldStatus.Expired), ct);
                var freed = updated == 0 ? [] : await FreeSeatsAsync([hold.Id], ct);
                await transaction.CommitAsync(ct);
                return freed;
            });
            if (seatIds.Length > 0)
            {
                await notifier.SeatsChangedAsync(new SeatChange(hold.EventId, seatIds, "Available"), ct);
            }
        }
        return due.Count;
    }

    private async Task<int[]> FreeSeatsAsync(Guid[] holdIds, CancellationToken ct)
    {
        // Filtered by hold ID, so a seat already claimed by a newer hold is left alone.
        var seatIds = await db.Seats.Where(s => s.HoldId != null && holdIds.Contains(s.HoldId.Value) && s.Status == SeatStatus.Held)
            .Select(s => s.Id).ToArrayAsync(ct);
        await db.Seats.Where(s => s.HoldId != null && holdIds.Contains(s.HoldId.Value) && s.Status == SeatStatus.Held)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.HoldId, (Guid?)null)
                .SetProperty(s => s.HeldUntil, (DateTimeOffset?)null), ct);
        return seatIds;
    }
}
