using Microsoft.EntityFrameworkCore;

namespace Ticketing.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Hold> Holds => Set<Hold>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.Property(u => u.Email).HasMaxLength(254);
            user.Property(u => u.Role).HasMaxLength(20);
            user.HasIndex(u => u.Email).IsUnique();
        });

        model.Entity<Event>(entity =>
        {
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Venue).HasMaxLength(200);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Seat>(seat =>
        {
            seat.HasIndex(s => new { s.EventId, s.Row, s.Number }).IsUnique();
            seat.HasIndex(s => s.HoldId);
            seat.ToTable(t => t.HasCheckConstraint("CK_Seat_Price", "[PriceCents] >= 0"));
        });

        model.Entity<Hold>(hold =>
        {
            hold.HasOne<Event>().WithMany().HasForeignKey(h => h.EventId).OnDelete(DeleteBehavior.Restrict);
            hold.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Restrict);
            hold.HasIndex(h => new { h.Status, h.ExpiresAt });
        });

        model.Entity<Order>(order =>
        {
            // The database's own last line of defense: one hold can only ever become one order.
            order.HasIndex(o => o.HoldId).IsUnique();
            order.HasIndex(o => o.UserId);
            order.HasOne<Hold>().WithMany().HasForeignKey(o => o.HoldId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<Event>().WithMany().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Restrict);
            order.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
