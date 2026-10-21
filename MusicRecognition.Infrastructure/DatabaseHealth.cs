using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MusicRecognition.Infrastructure;

public sealed class DatabaseHealth(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,CancellationToken cancellationToken=default)
    {
        await using var scope=scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Recognition database is unavailable.");
    }
}
