using System.Text.Json.Serialization;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MusicRecognition.Api;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using MusicRecognition.Application;
using MusicRecognition.Infrastructure;
using MusicRecognition.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddRecognition(builder.Configuration, worker: false);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict; });
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddRequestProtection(builder.Configuration);
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<ContractSchemaTransformer>();
    options.AddOperationTransformer((operation, context, ct) =>
    {
        foreach (var parameter in operation.Parameters ?? [])
            if (parameter.In == Microsoft.OpenApi.ParameterLocation.Header && parameter.Schema is Microsoft.OpenApi.OpenApiSchema header)
            {
                if (parameter.Name == "X-Score-Token") { header.MinLength = 32; header.MaxLength = 200; }
                if (parameter.Name == "Idempotency-Key") { header.MinLength = 8; header.MaxLength = 100; }
            }
        if (operation.OperationId == "ImportScore" && operation.RequestBody?.Content is { } multipart)
            foreach (var media in multipart.Values)
                if (media.Schema is Microsoft.OpenApi.OpenApiSchema form)
                    foreach (var part in form.AllOf ?? [])
                        if (part.Properties?.TryGetValue("preprocessing", out var property) == true && property is Microsoft.OpenApi.OpenApiSchema preprocessing)
                            preprocessing.Enum = new[] { "", "None", "Adaptive", "none", "adaptive" }.Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(x)!).ToList();
        if (operation.OperationId is "GetScoreArtifact" or "GetFragmentArtifact" or "GetFragmentImage")
            foreach (var status in operation.OperationId=="GetFragmentImage" ? new[]{"200"} : new[] { "200", "206" })
                operation.Responses![status] = new Microsoft.OpenApi.OpenApiResponse
                {
                    Description = status == "200" ? "Artifact bytes; Content-Type reflects PDF, image or JSON." : "Partial artifact bytes.",
                    Content = new Dictionary<string, Microsoft.OpenApi.OpenApiMediaType>
                    {
                        ["application/octet-stream"] = new() { Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, Format = "binary" } }
                    }
                };
        return Task.CompletedTask;
    });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "private, no-store";
    await next();
});
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
app.UseRateLimiter();
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
var scores = app.MapGroup("/api/scores").WithTags("Scores")
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500);
// No ambient cookie authentication: the explicit capability header protects every score operation.
scores.MapPost("/", async (IFormFile file, [FromForm] string? preprocessing, [FromHeader(Name = "X-Score-Token")] string token, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, IScoreService service, CancellationToken ct) =>
{
    var mode = preprocessing switch
    {
        null or "" or "None" or "none" => PreprocessingMode.None,
        "Adaptive" or "adaptive" => PreprocessingMode.Adaptive,
        _ => throw new RequestFailure(400, "invalid-preprocessing", "Choose None or Adaptive preprocessing.")
    };
    await using var stream = file.OpenReadStream(); var result = await service.ImportAsync(stream, file.FileName, token, idempotencyKey, ct, mode);
    return TypedResults.Accepted($"/api/scores/{result.Id}", result);
}).DisableAntiforgery().WithName("ImportScore").WithSummary("Upload a PDF (1–30 pages) or image; enqueue recognition.");
scores.MapGet("/{id:guid}", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.GetAsync(id, scoreToken, ct)).WithName("GetScore");
scores.MapGet("/{id:guid}/revisions", async (Guid id, int? version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.RevisionAsync(id, version, scoreToken, ct)).WithName("GetRevision");
scores.MapPost("/{id:guid}/exports", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => TypedResults.Accepted($"/api/scores/{id}/exports", await service.ExportAsync(id, scoreToken, ct))).WithName("CreateDocumentExport");
scores.MapGet("/{id:guid}/exports/{version:int}", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.ExportStatusAsync(id, version, scoreToken, ct)).WithName("GetDocumentExport");
scores.MapPost("/{id:guid}/revisions", async (Guid id, RevisionRequest revision, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => TypedResults.Accepted($"/api/scores/{id}/revisions", await service.CorrectAsync(id, scoreToken, revision, ct))).WithName("CreateRevision");
scores.MapPost("/{id:guid}/cancel", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.CancelAsync(id, scoreToken, ct)).WithName("CancelScore");
scores.MapPost("/{id:guid}/retry", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.RetryAsync(id, scoreToken, ct)).WithName("RetryScore");
scores.MapGet("/{id:guid}/artifacts/{name}", async (Guid id, string name, int? version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
{
    var file = await service.ArtifactAsync(id, version, name, scoreToken, ct); return TypedResults.PhysicalFile(file.Path, file.ContentType, file.DownloadName, enableRangeProcessing: true);
}).WithName("GetScoreArtifact").Produces(200, contentType: "application/octet-stream").Produces(206, contentType: "application/octet-stream");
scores.MapGet("/{id:guid}/layout", async (Guid id, int? version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
{
    var job = await service.GetAsync(id, scoreToken, ct);
    if (job.PageCount > 1) throw new RequestFailure(409, "page-required", "Select an individual page to retrieve its layout.");
    var file = await service.ArtifactAsync(id, version, "annotations.json", scoreToken, ct);
    return ScoreJson.Read<AnnotationsView>(await File.ReadAllTextAsync(file.Path, ct));
}).WithName("GetScoreLayout").WithSummary("Typed layout for an individual page revision.");
scores.MapGet("/{id:guid}/reflow", async (Guid id, int? version, [FromHeader(Name = "X-Score-Token")] string scoreToken, ReflowService service, CancellationToken ct) =>
    await service.GetAsync(id, version, scoreToken, ct)).WithName("GetScoreReflow").WithSummary("Revision-bound MusicXML and verified note identities for re-engraving.");
var fragments = app.MapGroup("/api/fragments").WithTags("Fragments")
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500);
fragments.MapPost("/", async (IFormFileCollection files, [FromForm] string plan, [FromHeader(Name="X-Score-Token")] string token,
    [FromHeader(Name="Idempotency-Key")] string idempotencyKey, FragmentService service, CancellationToken ct) =>
{
    if (files.Count is < 1 or > 12 || files.Sum(f=>f.Length)>20*1024*1024) throw new ArgumentException("请选择 1–12 张图片，合计不超过 20 MiB。");
    MusicRecognition.Domain.FragmentPlan options;
    try { options=ScoreJson.Read<MusicRecognition.Domain.FragmentPlan>(plan) ?? throw new ArgumentException("缺少拼谱设置。"); }
    catch (System.Text.Json.JsonException) { throw new ArgumentException("拼谱设置格式无效。"); }
    var uploads=files.Select(f=>new FragmentUpload(f.FileName,f.OpenReadStream())).ToArray();
    try { var value=await service.ImportAsync(uploads,options,token,idempotencyKey,ct); return TypedResults.Accepted($"/api/fragments/{value.Id}",value); }
    finally { foreach (var upload in uploads) await upload.Content.DisposeAsync(); }
}).DisableAntiforgery().WithName("ImportFragments").WithSummary("Create an ordered screenshot composition project (1–12 images, 20 MiB total).");
fragments.MapGet("/{id:guid}", async (Guid id,int? version,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) =>
    await service.GetAsync(id,scoreToken,version,ct)).WithName("GetFragmentProject");
fragments.MapPost("/{id:guid}/revisions", async (Guid id,FragmentRevisionRequest request,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) =>
    TypedResults.Accepted($"/api/fragments/{id}",await service.ReviseAsync(id,scoreToken,request,ct))).WithName("CreateFragmentRevision");
fragments.MapGet("/{id:guid}/artifacts/{name}", async (Guid id,int version,string name,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) =>
{ var file=await service.ArtifactAsync(id,scoreToken,version,name,ct); return TypedResults.PhysicalFile(file.Path,file.ContentType,file.DownloadName,enableRangeProcessing:true); }).WithName("GetFragmentArtifact").Produces(200,contentType:"application/octet-stream");
fragments.MapGet("/{id:guid}/images/{index:int}", async (Guid id,int index,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) =>
{ var file=await service.ImageAsync(id,scoreToken,index,ct); return TypedResults.PhysicalFile(file.Path,file.ContentType,file.DownloadName); }).WithName("GetFragmentImage").Produces(200,contentType:"application/octet-stream");
fragments.MapPost("/{id:guid}/retry", async (Guid id,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) => await service.CommandAsync(id,scoreToken,true,ct)).WithName("RetryFragmentProject");
fragments.MapPost("/{id:guid}/cancel", async (Guid id,[FromHeader(Name="X-Score-Token")] string scoreToken,FragmentService service,CancellationToken ct) => await service.CommandAsync(id,scoreToken,false,ct)).WithName("CancelFragmentProject");
await app.Services.MigrateRecognitionAsync();
app.Run();
