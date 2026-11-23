using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MusicRecognition.Infrastructure.Persistence.DesignTime;

public sealed class ScoreDbFactory : IDesignTimeDbContextFactory<ScoreDb>
{
    public ScoreDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScoreDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__recognitiondb") ??
            "Host=localhost;Database=recognitiondb;Username=postgres;Password=design-time-only").Options);
}
