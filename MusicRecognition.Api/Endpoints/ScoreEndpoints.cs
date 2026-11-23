using Microsoft.AspNetCore.Mvc;
using MusicRecognition.Api.Contracts.Responses;
using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;

namespace MusicRecognition.Api.Endpoints;

internal static class ScoreEndpoints
{
    public static void MapScoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var scores = endpoints.MapGroup("/api/scores").WithTags("Scores")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500);
        // No ambient cookie authentication: the explicit capability header protects every score operation.
        scores.MapPost("/", async (IFormFile file, [FromForm] string? preprocessing, [FromForm] string? engine, [FromHeader(Name = "X-Score-Token")] string token, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, IScoreService service, CancellationToken ct) =>
        {
            var mode = preprocessing switch
            {
                null or "" or "None" or "none" => PreprocessingMode.None,
                "Adaptive" or "adaptive" => PreprocessingMode.Adaptive,
                _ => throw new RequestFailure(400, "invalid-preprocessing", "Choose None or Adaptive preprocessing.")
            };
            var selectedEngine = engine switch
            {
                null or "" or "Audiveris" or "audiveris" => RecognitionEngineKind.Audiveris,
                "Homr" or "homr" => RecognitionEngineKind.Homr,
                _ => throw new RequestFailure(400, "invalid-engine", "Choose Audiveris or Homr recognition.")
            };
            await using var stream = file.OpenReadStream(); var result = await service.ImportAsync(stream, file.FileName, token, idempotencyKey, ct, mode, selectedEngine);
            return TypedResults.Accepted($"/api/scores/{result.Id}", result);
        }).DisableAntiforgery().WithName("ImportScore").WithSummary("Upload a PDF (1–30 pages) or image; enqueue recognition.");

        scores.MapGet("/{id:guid}", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.GetAsync(id, scoreToken, ct)).WithName("GetScore");

        scores.MapGet("/{id:guid}/revisions/{version:int}", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.RevisionAsync(id, version, scoreToken, ct)).WithName("GetRevision");
        scores.MapPost("/{id:guid}/revisions", async (Guid id, RevisionRequest revision, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.CorrectAsync(id, scoreToken, revision, ct);
            return TypedResults.Accepted($"/api/scores/{id}/revisions/{result.Version}", result);
        }).WithName("CreateRevision");

        scores.MapGet("/{id:guid}/revisions/{version:int}/layout", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var job = await service.GetAsync(id, scoreToken, ct);
            if (job.PageCount > 1) throw new RequestFailure(400, "select-page", "Select an individual page to retrieve its layout.");
            var file = await service.ArtifactAsync(id, version, "annotations.json", scoreToken, ct);
            if (!File.Exists(file.Path)) throw new RequestFailure(404, "layout-not-found", "The requested layout is not available.");
            return TypedResults.Ok(ScoreJson.Read<AnnotationsView>(await File.ReadAllTextAsync(file.Path, ct)));
        }).WithName("GetRevisionLayout").WithSummary("Typed layout for an individual page revision.").Produces<AnnotationsView>(200);

        scores.MapGet("/{id:guid}/revisions/{version:int}/annotated-pdf", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "annotated.pdf", scoreToken, ct))
            .WithName("GetAnnotatedPdf").Produces(200, contentType: "application/octet-stream").Produces(206, contentType: "application/octet-stream");

        scores.MapGet("/{id:guid}/revisions/{version:int}/annotations", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "annotations.json", scoreToken, ct))
            .WithName("GetRevisionAnnotations").Produces(200, contentType: "application/json");

        scores.MapGet("/{id:guid}/revisions/{version:int}/metrics", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "metrics.json", scoreToken, ct))
            .WithName("GetRevisionMetrics").Produces(200, contentType: "application/json");

        scores.MapPost("/{id:guid}/exports", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.ExportAsync(id, scoreToken, ct);
            return TypedResults.Accepted($"/api/scores/{id}/exports/{result.Version}", result);
        }).WithName("CreateDocumentExport");
        scores.MapGet("/{id:guid}/exports/{version:int}", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.ExportStatusAsync(id, version, scoreToken, ct)).WithName("GetDocumentExport");

        scores.MapPost("/{id:guid}/cancellations", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.CancelAsync(id, scoreToken, ct);
            return TypedResults.Accepted($"/api/scores/{id}", result);
        }).WithName("CreateCancellation").Produces<JobView>(202);

        scores.MapPost("/{id:guid}/retries", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.RetryAsync(id, scoreToken, ct);
            return TypedResults.Accepted($"/api/scores/{id}", result);
        }).WithName("CreateRetry").Produces<JobView>(202);

        scores.MapGet("/{id:guid}/source", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, null, "source", scoreToken, ct))
            .WithName("GetScoreSource").Produces(200, contentType: "application/octet-stream").Produces(206, contentType: "application/octet-stream");

        scores.MapGet("/{id:guid}/musicxml", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, null, "score.mxl", scoreToken, ct))
            .WithName("GetScoreMusicXml").Produces(200, contentType: "application/octet-stream");
    }

    private static async Task<IResult> ReadArtifactAsync(IScoreService service, Guid id, int? version, string name, string token, CancellationToken ct)
    {
        var file = await service.ArtifactAsync(id, version, name, token, ct);
        return TypedResults.PhysicalFile(file.Path, file.ContentType, file.DownloadName, enableRangeProcessing: true);
    }
}
