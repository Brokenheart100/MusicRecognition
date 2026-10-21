using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure;

public sealed class ScoreDb(DbContextOptions<ScoreDb> options) : DbContext(options)
{
    public DbSet<ScoreJob> Jobs => Set<ScoreJob>();
    public DbSet<ScoreRevision> Revisions => Set<ScoreRevision>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ScoreJob>().HasKey(j => j.Id);
        model.Entity<ScoreJob>().HasIndex(j => j.RequestHash).IsUnique();
        model.Entity<ScoreJob>().Property(j => j.Version).IsRowVersion();
        model.Entity<ScoreRevision>().HasKey(r => new { r.JobId, r.Version });
        model.Entity<ScoreRevision>().Property(r => r.Snapshot).HasColumnType("jsonb");
        model.Entity<ScoreRevision>().HasOne<ScoreJob>().WithMany().HasForeignKey(r => r.JobId);
    }
}
public sealed class ScoreDbFactory : IDesignTimeDbContextFactory<ScoreDb>
{
    public ScoreDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScoreDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__recognitiondb") ??
            "Host=localhost;Database=recognitiondb;Username=postgres;Password=design-time-only").Options);
}
