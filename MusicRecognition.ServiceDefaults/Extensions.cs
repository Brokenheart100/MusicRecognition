using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MusicRecognition.ServiceDefaults;

public static class Extensions
{
    public static T AddServiceDefaults<T>(this T builder) where T:IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks();
        builder.Logging.AddOpenTelemetry(o=>{o.IncludeFormattedMessage=true;o.IncludeScopes=true;});
        var otel=builder.Services.AddOpenTelemetry()
            .WithMetrics(m=>m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter("Microsoft.AspNetCore.Hosting","System.Net.Http"))
            .WithTracing(t=>t.AddAspNetCoreInstrumentation(o=>o.Filter=context=>!context.Request.Path.StartsWithSegments("/health")).AddHttpClientInstrumentation());
        if(!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
        return builder;
    }
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {app.MapHealthChecks("/health");app.MapHealthChecks("/alive",new HealthCheckOptions {Predicate=_=>false});return app;}
}
