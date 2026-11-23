using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicRecognition.Application.Handlers;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Infrastructure.BackgroundJobs;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.HealthChecks;
using MusicRecognition.Infrastructure.Persistence.Repositories;
using MusicRecognition.Infrastructure.Recognition;
using MusicRecognition.Infrastructure.Storage;

namespace MusicRecognition.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRecognition(this IServiceCollection services, IConfiguration config, bool worker)
    {
        var options = config.GetSection("Recognition").Get<RecognitionOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.DataRoot) || string.IsNullOrWhiteSpace(options.RepositoryRoot) || options.TimeoutSeconds is < 10 or > 1800 || options.WorkerCount is < 1 or > 4 || options.MinimumFreeSpaceMiB is < 0 or > 1048576)
            throw new InvalidOperationException("Configure Recognition RepositoryRoot, DataRoot and TimeoutSeconds (10..1800).");
        services.AddSingleton(options); services.AddSingleton<IScoreStorage, FileStorage>();
        var connection = config.GetConnectionString("recognitiondb") ?? throw new InvalidOperationException("Missing recognitiondb connection string.");
        services.AddDbContext<ScoreDb>(o => o.UseNpgsql(connection));
        services.AddHealthChecks().AddCheck<DatabaseHealth>("recognitiondb").AddCheck<DataStorageHealth>("score-storage");
        services.AddScoped<IScoreRepository, ScoreRepository>(); services.AddScoped<IScoreService, ScoreService>(); services.AddScoped<IRecognitionEngine, ToolEngine>(); services.AddScoped<ScoreProcessor>();
        services.AddScoped<ToolEngine>();
        services.AddHangfire(h => h.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connection), new PostgreSqlStorageOptions { InvisibilityTimeout = TimeSpan.FromMinutes(1), QueuePollInterval = TimeSpan.FromSeconds(2) }));
        if (worker)
        {
            services.AddSingleton<RecognitionRuntime>();
            services.AddHealthChecks().AddCheck<RecognitionRuntime>("recognition-runtime");
            services.AddHangfireServer(o => { o.WorkerCount = options.WorkerCount; o.Queues = ["recognition", "render"]; o.CancellationCheckInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(10); });
            services.AddHostedService<DispatchService>();
        }
        return services;
    }
    public static async Task MigrateRecognitionAsync(this IServiceProvider provider, CancellationToken ct = default)
    { await using var scope = provider.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.MigrateAsync(ct); }
}
