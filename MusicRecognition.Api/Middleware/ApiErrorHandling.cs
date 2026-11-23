using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using MusicRecognition.Application.Exceptions;

namespace MusicRecognition.Api.Middleware;

internal static class ApiErrorHandling
{
    public static void UseApiErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var failure = context.Features.Get<IExceptionHandlerFeature>()!;
            var error = failure.Error;
            var status = error switch { RequestFailure f => f.Status, BadHttpRequestException f => f.StatusCode, ArgumentException => 400, Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => 409, _ => 500 };
            var code = error is RequestFailure request ? request.Code : status == 400 ? "invalid-request" : status == 409 ? "concurrency-conflict" : "internal-error";
            var operation = failure.Endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "unmatched-request";
            var scoreId = Guid.TryParse(failure.RouteValues?["id"]?.ToString(), out var parsedId) ? (Guid?)parsedId : null;
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            app.Logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, error,
                "API 请求失败：操作 {Operation}，HTTP 状态 {HttpStatusCode}，错误码 {ErrorCode}，原因 {FailureReason}，任务 {ScoreId}，跟踪 {RequestTraceId}",
                operation, status, code, error.Message, scoreId, traceId);
            await Results.Problem(statusCode: status, title: code, detail: status < 500 ? error.Message : "An internal error occurred. Use traceId to find server logs.", extensions: new Dictionary<string, object?> { { "traceId", traceId } }).ExecuteAsync(context);
        }));
        app.UseStatusCodePages(async status =>
        {
            var response = status.HttpContext.Response;
            var traceId = Activity.Current?.Id ?? status.HttpContext.TraceIdentifier;
            app.Logger.LogWarning("API 请求被拒绝：方法 {HttpMethod}，路径 {RequestPath}，HTTP 状态 {HttpStatusCode}，跟踪 {RequestTraceId}",
                status.HttpContext.Request.Method, status.HttpContext.Request.Path.Value, response.StatusCode, traceId);
            await Results.Problem(statusCode: response.StatusCode, title: "invalid-request",
                extensions: new Dictionary<string, object?> { ["traceId"] = traceId }).ExecuteAsync(status.HttpContext);
        });
    }
}
