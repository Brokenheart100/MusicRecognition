using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace MusicRecognition.Api.Extensions;

public static class RequestProtection
{
    public static IServiceCollection AddRequestProtection(this IServiceCollection services, IConfiguration config)
    {
        var permits = config.GetValue("Operations:WritesPerMinute", 20);
        var concurrency = config.GetValue("Operations:ConcurrentWrites", 4);
        if (permits is < 1 or > 1000 || concurrency is < 1 or > 32)
            throw new InvalidOperationException("Operations 写入限额应为 1–1000 次/分钟，并发应为 1–32。");
        // One API instance is the deployment boundary. Do not key by an untrusted
        // token/X-Forwarded-For: clients could rotate them to bypass the budget.
        bool Write(HttpContext c) => c.Request.Path.StartsWithSegments("/api") && HttpMethods.IsPost(c.Request.Method);
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(c => Write(c)
                    ? RateLimitPartition.GetConcurrencyLimiter("writes", _ => new() { PermitLimit = concurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("reads")),
                PartitionedRateLimiter.Create<HttpContext, string>(c => Write(c)
                    ? RateLimitPartition.GetFixedWindowLimiter("writes", _ => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("reads")));
            options.OnRejected = async (context, ct) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Ceiling(retry.TotalSeconds) : 2;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, title: "write-rate-limited", detail: "提交过于频繁，请稍后重试。读取任务和下载不受此写入限额影响。")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }
}
