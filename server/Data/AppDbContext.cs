using Tergeo.Server.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tergeo.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<BookLogEntry> BookLogs => Set<BookLogEntry>();
    public DbSet<OpdsSource> OpdsSources => Set<OpdsSource>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // SQLite cannot ORDER BY DateTimeOffset; store them as ticks (long).
        var dtoConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDtoConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : (long?)null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset))
                    prop.SetValueConverter(dtoConverter);
                else if (prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(nullableDtoConverter);
            }
        }

        builder.Entity<AppUser>(b =>
        {
            b.HasOne(u => u.Settings)
                .WithOne(s => s.User)
                .HasForeignKey<UserSettings>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(u => new { u.Provider, u.ExternalSubject }).IsUnique(false);
        });

        builder.Entity<Book>(b =>
        {
            b.HasIndex(n => n.UserId);
            b.HasIndex(n => n.Status);
            b.HasMany(n => n.Logs)
                .WithOne(l => l.Book)
                .HasForeignKey(l => l.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookLogEntry>(b =>
        {
            b.HasIndex(l => new { l.BookId, l.Id });
        });

        builder.Entity<OpdsSource>(b =>
        {
            b.HasIndex(s => s.UserId);
            b.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
