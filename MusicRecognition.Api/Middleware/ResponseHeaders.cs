namespace MusicRecognition.Api.Middleware;

// 给所有响应加安全响应头，浏览器看到这些头会采取更保守的默认行为。
// nosniff 禁止浏览器猜文件类型，防止把上传的内容当脚本执行。
// Referrer-Policy 让跳转到其他网站时不泄露本站地址。
// /api 响应禁止缓存，因为里面是用户的乐谱和任务状态，不能留在共享缓存里。
internal static class ResponseHeaders
{
    public static void UseResponseHeaders(this WebApplication app)
    {
        // 放在管线最外层：先设好头再放行，后面产生的错误响应也会带上这些头。
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            // 只有 /api 下的接口禁止缓存，静态资源仍可被浏览器缓存。
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "private, no-store";
            await next();
        });
    }
}
