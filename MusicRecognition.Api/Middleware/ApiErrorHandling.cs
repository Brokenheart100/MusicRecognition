using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using MusicRecognition.Application.Exceptions;

namespace MusicRecognition.Api.Middleware;

// 统一的错误出口：接口抛异常时，在这里换成同一种 JSON 错误格式（ProblemDetails）。
// 前端只需按状态码和错误码处理，不用关心是哪个接口、哪种异常。
// 每个失败请求都会写日志并附跟踪号（traceId），用户报障时凭它可以查服务端日志。
// 5xx 不把内部异常细节返回给浏览器，只留一句通用提示和 traceId。
internal static class ApiErrorHandling
{
    // 注册两个处理器：一个接异常，一个接没有响应体的错误状态码。
    public static void UseApiErrorHandling(this WebApplication app)
    {
        // 捕获下游所有未处理异常。
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var failure = context.Features.Get<IExceptionHandlerFeature>()!;
            var error = failure.Error;
            // 把异常翻译成 HTTP 状态码：业务异常自带状态码，请求格式错沿用框架状态码，
            // 数据库并发冲突算 409（别人先改了数据），其余一律 500。
            var status = error switch { RequestFailure f => f.Status, BadHttpRequestException f => f.StatusCode, ArgumentException => 400, Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => 409, _ => 500 };
            // 再翻译成稳定的错误码（ProblemDetails 的 title），前端按它区分错误类型。
            var code = error is RequestFailure request ? request.Code : status == 400 ? "invalid-request" : status == 409 ? "concurrency-conflict" : "internal-error";
            // 从路由元数据取接口名（如 ImportScore），日志里能直接看出是哪个操作失败。
            var operation = failure.Endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "unmatched-request";
            var scoreId = Guid.TryParse(failure.RouteValues?["id"]?.ToString(), out var parsedId) ? (Guid?)parsedId : null;
            // 跟踪号优先用分布式追踪 Id，没有就用框架给本次请求的标识。
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            // 5xx 记 Error，4xx 记 Warning：服务端 bug 要报警，客户端写错不必刷屏。
            app.Logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, error,
                "API 请求失败：操作 {Operation}，HTTP 状态 {HttpStatusCode}，错误码 {ErrorCode}，原因 {FailureReason}，任务 {ScoreId}，跟踪 {RequestTraceId}",
                operation, status, code, error.Message, scoreId, traceId);
            // 返回统一错误体：title 是错误码，detail 是给人看的说明。
            // 5xx 时 detail 换成通用文案，真实异常只留在服务端日志里。
            await Results.Problem(statusCode: status, title: code, detail: status < 500 ? error.Message : "An internal error occurred. Use traceId to find server logs.", extensions: new Dictionary<string, object?> { { "traceId", traceId } }).ExecuteAsync(context);
        }));
        // 没有抛异常、直接返回 4xx/5xx 的请求（如路由 404），也补上统一响应体。
        app.UseStatusCodePages(async status =>
        {
            var response = status.HttpContext.Response;
            var traceId = Activity.Current?.Id ?? status.HttpContext.TraceIdentifier;
            app.Logger.LogWarning("API 请求被拒绝：方法 {HttpMethod}，路径 {RequestPath}，HTTP 状态 {HttpStatusCode}，跟踪 {RequestTraceId}",
                status.HttpContext.Request.Method, status.HttpContext.Request.Path.Value, response.StatusCode, traceId);
            // 返回同样的 ProblemDetails 结构，前端错误处理逻辑保持一致。
            await Results.Problem(statusCode: response.StatusCode, title: "invalid-request",
                extensions: new Dictionary<string, object?> { ["traceId"] = traceId }).ExecuteAsync(status.HttpContext);
        });
    }
}
