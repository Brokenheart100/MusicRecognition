using Microsoft.EntityFrameworkCore;
using MusicRecognition.Application;
using MusicRecognition.Infrastructure.Persistence.Configurations;

namespace MusicRecognition.Infrastructure;

public sealed class ScoreDb(DbContextOptions<ScoreDb> options) : DbContext(options)
{
    public DbSet<ScoreJob> Jobs => Set<ScoreJob>();
    public DbSet<ScoreRevision> Revisions => Set<ScoreRevision>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfiguration(new ScoreJobConfiguration());
        model.ApplyConfiguration(new ScoreRevisionConfiguration());
    }
}
