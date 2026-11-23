using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure.HealthChecks;

// 识谱数据库的健康检查：只问一句“现在能连上 PostgreSQL 吗”。
// 健康检查可能被并发调用，所以这里不直接拿数据库上下文，而是每次临时开一个作用域。
// 连不上就报 Unhealthy，让 /health 接口把服务标成异常。
public sealed class DatabaseHealth(IServiceScopeFactory scopes, ILogger<DatabaseHealth> logger) : IHealthCheck
{
    // 用 CanConnectAsync 做一次轻量连接测试，不查业务表，开销很小。
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // 健康检查对象是单例，数据库上下文是作用域对象，必须通过作用域工厂现取。
        await using var scope = scopes.CreateAsyncScope();
        var reachable = await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.CanConnectAsync(cancellationToken);
        if (!reachable) logger.LogInformation("🩺 识谱数据库健康检查失败：无法连接");
        return reachable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("识谱数据库不可用。");
    }
}
