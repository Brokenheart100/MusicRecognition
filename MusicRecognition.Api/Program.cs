using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using MusicRecognition.Application;
using MusicRecognition.Infrastructure;
using MusicRecognition.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddRecognition(builder.Configuration, worker: false);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
builder.Services.AddProblemDetails(); builder.Services.AddOpenApi();
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var status = error switch { RequestFailure f => f.Status, BadHttpRequestException f => f.StatusCode, ArgumentException => 400, Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => 409, _ => 500 };
    var code = error is RequestFailure request ? request.Code : status == 400 ? "invalid-request" : status == 409 ? "concurrency-conflict" : "internal-error";
    await Results.Problem(statusCode: status, title: code, detail: status < 500 ? error.Message : "An internal error occurred. Use traceId to find server logs.", extensions: new Dictionary<string, object?> { { "traceId", context.TraceIdentifier } }).ExecuteAsync(context);
}));
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
static string Token(HttpRequest request) => request.Headers["X-Score-Token"].ToString();
var scores = app.MapGroup("/api/scores");
// No ambient cookie authentication: the explicit capability header protects every score operation.
scores.MapPost("/", async (IFormFile file, HttpRequest request, IScoreService service, CancellationToken ct) =>
{
    await using var stream = file.OpenReadStream(); var result = await service.ImportAsync(stream, file.FileName, Token(request), request.Headers["Idempotency-Key"].ToString(), ct);
    return Results.Accepted($"/api/scores/{result.Id}", result);
}).DisableAntiforgery();
scores.MapGet("/{id:guid}", async (Guid id, HttpRequest request, IScoreService service, CancellationToken ct) => await service.GetAsync(id, Token(request), ct));
scores.MapGet("/{id:guid}/revisions", async (Guid id, int? version, HttpRequest request, IScoreService service, CancellationToken ct) => await service.RevisionAsync(id, version, Token(request), ct));
scores.MapPost("/{id:guid}/revisions", async (Guid id, RevisionRequest revision, HttpRequest request, IScoreService service, CancellationToken ct) => Results.Accepted($"/api/scores/{id}/revisions", await service.CorrectAsync(id, Token(request), revision, ct)));
scores.MapPost("/{id:guid}/cancel", async (Guid id, HttpRequest request, IScoreService service, CancellationToken ct) => await service.CancelAsync(id, Token(request), ct));
scores.MapPost("/{id:guid}/retry", async (Guid id, HttpRequest request, IScoreService service, CancellationToken ct) => await service.RetryAsync(id, Token(request), ct));
scores.MapGet("/{id:guid}/artifacts/{name}", async (Guid id, string name, int? version, HttpRequest request, IScoreService service, CancellationToken ct) =>
{
    var file = await service.ArtifactAsync(id, version, name, Token(request), ct); return Results.File(file.Path, file.ContentType, file.DownloadName, enableRangeProcessing: true);
});
await app.Services.MigrateRecognitionAsync();
app.Run();
