# event-ticketing

A ticketing platform that lets customers hold and buy seats without double-booking, even when many people want the same seat at once. Built with C#, ASP.NET Core, Entity Framework Core, SQL Server, SignalR, and React.

**Demo only:** payment is simulated, so no real money or card data is involved. It isn't deployed anywhere.

## How it works

```
 React + TypeScript ──REST──> ASP.NET Core Web API ──EF Core──> SQL Server
        ▲                            │
        └──── SignalR live updates ──┤
                                     └── background worker releases lapsed holds
```

1. A customer picks up to 8 seats and places a **hold**, which lasts 5 minutes.
2. Everyone viewing that event sees those seats change instantly, with no reload.
3. The customer completes the purchase, or the hold ends and the seats free up on their own.

| Part | What it does |
|---|---|
| `SeatService` | All seat state changes: hold, release, purchase, expire |
| `HoldExpiryWorker` | Background service that releases lapsed holds and tells watching clients |
| `SeatHub` | SignalR hub with one group per event |
| `Controllers/` | REST API with JWT login and role-based access for customers and organizers |
| `Data/` | EF Core models and migrations, including the constraints |
| `web/` | React and TypeScript seat map |

## Design decisions

**The database decides who gets a seat.** The obvious code reads the seat, checks it's free, then writes. Two customers can both read "free" and both succeed. Here, claiming a seat is one conditional `UPDATE` that only matches free seats, so SQL Server's row locks pick exactly one winner.

**Group bookings are all or nothing.** If a customer asks for 4 seats and only 3 can be claimed, the transaction rolls back and all 4 stay available. No one is left with half a booking.

**Expiry is correct even if the worker is down.** A seat whose hold has lapsed already counts as free when someone tries to hold it. The worker only cleans up and sends live updates, so its failure can't block sales.

**An old hold can't take a seat back.** Releasing an expired hold only touches seats still linked to that hold, so it can't free a seat that a newer customer now holds.

**A retried purchase can't charge twice.** Purchasing again returns the same order. There are layers here: the hold can only leave `Active` once, the seats must still be `Held`, and a unique index allows one order per hold.

**Deadlocks are retried.** Overlapping group bookings can deadlock in SQL Server. EF Core's execution strategy retries them, with each attempt inside its own transaction.

**Live updates can't break a sale.** Notifications are sent after the commit, and a failed one is only logged. The client reloads seats when it reconnects, in case it missed anything.

## Testing

- **26 integration tests (xUnit):** run the real app against real SQL Server, through `WebApplicationFactory`.
- **4 browser tests (Playwright):** real Chromium, including two customers at once.
- **CI (GitHub Actions):** runs everything against SQL Server, and fails on high-severity vulnerable packages or compiler warnings.

The main concurrency tests:

| Test | What it proves |
|---|---|
| 50 customers race for one seat | Exactly 1 wins and 49 get a conflict |
| 40 overlapping group bookings | No seat goes to two customers, and losers leave nothing behind |
| 20 simultaneous purchases of one hold | Exactly 1 order is created |
| Seat map in a second browser | It updates live, without a reload |

**Checking the tests themselves.** I planted bugs on throwaway branches to confirm the tests catch them:

| Planted bug | Result |
|---|---|
| Hold ignores seat availability | Caught, 4 tests failed, including the 50-customer race |
| Partial group bookings allowed | Caught, 2 tests failed |
| Purchase ignores hold expiry | Caught, 1 test failed |
| Purchase ignores hold status | Not caught, because the seat check still blocks it |
| Both purchase checks removed | Caught, including the simultaneous-purchase test |

The fourth result is redundant protection rather than a gap in the tests, which the fifth confirms.

## Running it

Requires .NET 10, Docker, and Node 20.

```sh
docker compose up -d --wait            # SQL Server
dotnet test                            # All integration tests
dotnet run --project src/Ticketing.Api --urls http://localhost:5080

cd web && npm install && npm run dev   # http://localhost:5174
```

Migrations run on startup. To create an event, register with `organizerCode` set to the value in `appsettings.Development.json`, then `POST /api/events`. There's no organizer screen yet. The passwords and keys in the repo are only for local development.

On Apple Silicon, SQL Server runs under emulation, because Microsoft only ships an x86 image. The test results above come from CI on Linux, not from a Mac.

## Known limits

- It runs as one instance. Booking stays correct with several, because the database enforces it, but SignalR would need a backplane such as Azure SignalR or Redis to reach clients on other instances.
- Holds aren't limited per customer, so someone could tie up many seats by placing repeated holds.
- There's no load testing, so I don't have throughput numbers.
- There's no organizer screen, refunds, or real payment.
