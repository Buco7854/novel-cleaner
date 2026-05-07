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
    public DbSet<CleanJob> CleanJobs => Set<CleanJob>();
    public DbSet<JobLogEntry> JobLogs => Set<JobLogEntry>();
    public DbSet<OpdsSource> OpdsSources => Set<OpdsSource>();
    public DbSet<ChapterReview> ChapterReviews => Set<ChapterReview>();
    public DbSet<ReviewProposal> ReviewProposals => Set<ReviewProposal>();

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

        builder.Entity<CleanJob>(b =>
        {
            b.HasIndex(j => j.UserId);
            b.HasIndex(j => j.Status);
            b.HasMany(j => j.Logs)
                .WithOne(l => l.Job)
                .HasForeignKey(l => l.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasMany(j => j.Reviews)
                .WithOne(r => r.Job)
                .HasForeignKey(r => r.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JobLogEntry>(b =>
        {
            b.HasIndex(l => new { l.JobId, l.Id });
        });

        builder.Entity<ChapterReview>(b =>
        {
            b.HasIndex(r => r.JobId);
            b.HasMany(r => r.Proposals)
                .WithOne(p => p.Chapter)
                .HasForeignKey(p => p.ChapterReviewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReviewProposal>(b =>
        {
            b.HasIndex(p => p.ChapterReviewId);
        });

        builder.Entity<OpdsSource>(b =>
        {
            b.HasIndex(s => s.UserId);
            b.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
