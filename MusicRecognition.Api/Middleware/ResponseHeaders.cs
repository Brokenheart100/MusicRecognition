namespace MusicRecognition.Api.Middleware;

internal static class ResponseHeaders
{
    public static void UseResponseHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "private, no-store";
            await next();
        });
    }
}
