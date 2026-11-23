using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MusicRecognition.ServiceDefaults;

// 所有服务共用的默认配置：健康检查、OpenTelemetry 日志/指标/追踪，一次装好。
// API 和 Worker 在启动时都调用 AddServiceDefaults，保证两边的监控口径一致。
// 有了它，服务不用各自写一遍 OpenTelemetry 样板代码；需要额外监控时再往上加。
// 名字里的 ServiceDefaults 是 .NET Aspire 约定的“服务默认值”。
public static class Extensions
{
    // 给任意宿主（API / Worker）装上默认能力：健康检查 + 日志 + 指标 + 追踪。
    public static T AddServiceDefaults<T>(this T builder) where T : IHostApplicationBuilder
    {
        // 注册健康检查框架，具体检查项由各服务自己添加。
        builder.Services.AddHealthChecks();
        // 日志也走 OpenTelemetry：保留格式化后的消息和日志作用域里的字段（如任务 ID）。
        builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });
        // 指标和追踪统一用 OpenTelemetry 收集，进程内的 Instrumentation 会自动接入。
        var otel = builder.Services.AddOpenTelemetry()
        // 订阅三个指标来源：ASP.NET Core、HTTP 客户端，以及项目自己的 Processing 指标。
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter("Microsoft.AspNetCore.Hosting", "System.Net.Http", "MusicRecognition.Processing"))
        // 追踪只记录业务请求，跳过 /health 和 /alive 探针，否则健康检查会把追踪刷满。
            .WithTracing(t => t.AddSource("MusicRecognition.Processing").AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments("/alive")).AddHttpClientInstrumentation());
        // 只有配置了 OTLP 导出地址才往外发数据；本地开发没有收集器时就只留在进程内。
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
        return builder;
    }
    // 挂上两个默认探针：/health 汇总所有依赖检查，/alive 只证明进程还活着。
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    // Predicate 恒为 false 表示不跑任何检查项，只要 HTTP 能响应就算存活。
    { app.MapHealthChecks("/health"); app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false }); return app; }
}
