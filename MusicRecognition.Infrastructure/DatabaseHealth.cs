using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure;

public sealed class DatabaseHealth(IServiceScopeFactory scopes, ILogger<DatabaseHealth> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reachable = await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.CanConnectAsync(cancellationToken);
        if (!reachable) logger.LogDebug("🩺 Recognition database health check failed: cannot connect");
        return reachable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Recognition database is unavailable.");
    }
}
