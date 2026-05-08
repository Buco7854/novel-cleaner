using NovelCleaner.Server.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NovelCleaner.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Novel> Novels => Set<Novel>();
    public DbSet<NovelLogEntry> NovelLogs => Set<NovelLogEntry>();
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

        builder.Entity<Novel>(b =>
        {
            b.HasIndex(n => n.UserId);
            b.HasIndex(n => n.Status);
            b.HasMany(n => n.Logs)
                .WithOne(l => l.Novel)
                .HasForeignKey(l => l.NovelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NovelLogEntry>(b =>
        {
            b.HasIndex(l => new { l.NovelId, l.Id });
        });

        builder.Entity<OpdsSource>(b =>
        {
            b.HasIndex(s => s.UserId);
            b.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
