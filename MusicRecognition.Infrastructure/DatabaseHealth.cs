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
        if (!reachable) logger.LogInformation("🩺 识谱数据库健康检查失败：无法连接");
        return reachable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("识谱数据库不可用。");
    }
}
