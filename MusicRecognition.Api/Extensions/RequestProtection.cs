using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace MusicRecognition.Api.Extensions;

// 写请求限流（rate limiting）：限制同时能处理多少个写操作、每分钟总共多少次。
// 单独保护写操作是因为上传、校对、导出都要跑识别引擎，很吃 CPU。
// 读请求（查状态、下载文件）不设上限，用户刷新页面、下载成品不该被拦。
// 限额来自配置 Operations:WritesPerMinute 与 ConcurrentWrites，
// 两者默认 20 次/分钟和 4 个并发。
public static class RequestProtection
{
    // 注册限流服务：读配置、检查范围是否合理，并定义被限流时返回什么。
    public static IServiceCollection AddRequestProtection(this IServiceCollection services, IConfiguration config)
    {
        // 每分钟允许的写请求次数，配置缺失时用 20。
        var permits = config.GetValue("Operations:WritesPerMinute", 20);
        // 允许同时处理的写请求数，配置缺失时用 4。
        var concurrency = config.GetValue("Operations:ConcurrentWrites", 4);
        // 配置值超出 1–1000 / 1–32 时启动直接失败，避免把限额配成 0 或无穷大。
        if (permits is < 1 or > 1000 || concurrency is < 1 or > 32)
            throw new InvalidOperationException("Operations 写入限额应为 1–1000 次/分钟，并发应为 1–32。");
        // One API instance is the deployment boundary. Do not key by an untrusted
        // token/X-Forwarded-For: clients could rotate them to bypass the budget.
        // 判定“写请求”：只有 /api 下用 POST 发起的请求才计数，读取和下载不算。
        bool Write(HttpContext c) => c.Request.Path.StartsWithSegments("/api") && HttpMethods.IsPost(c.Request.Method);
        services.AddRateLimiter(options =>
        {
            // 两道路闸串联：先限制同时处理数（并发），再限制每分钟总量。
            // 两道都只对写请求生效，读请求走不限额的分支。
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(c => Write(c)
                    ? RateLimitPartition.GetConcurrencyLimiter("writes", _ => new() { PermitLimit = concurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("reads")),
                PartitionedRateLimiter.Create<HttpContext, string>(c => Write(c)
                    ? RateLimitPartition.GetFixedWindowLimiter("writes", _ => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("reads")));
            // 被限流时的处理：告诉客户端多久后重试，并返回 429。
            options.OnRejected = async (context, ct) =>
            {
                // RetryAfter 是限流器给出的建议等待秒数，取不到就默认 2 秒。
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Ceiling(retry.TotalSeconds) : 2;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                // 返回标准 ProblemDetails：标题 write-rate-limited，附带中文说明。
                await Results.Problem(statusCode: 429, title: "write-rate-limited", detail: "提交过于频繁，请稍后重试。读取任务和下载不受此写入限额影响。")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }
}
