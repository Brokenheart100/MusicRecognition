using Microsoft.AspNetCore.Mvc;
using MusicRecognition.Api.Contracts.Responses;
using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;

namespace MusicRecognition.Api.Endpoints;

// 本文件定义乐谱的全部 HTTP 接口：上传、查状态、逐页校对、下载产物、取消和重试。
// 每个接口都要带访问令牌（X-Score-Token）：系统没有登录密码，令牌就是查看权限。
// 上传还要求幂等键（Idempotency-Key），同一次操作重复提交只会创建一个任务。
// 识别和导出都在后台排队，接口只负责收请求、查状态、把产物文件发回浏览器。
internal static class ScoreEndpoints
{
    // 把乐谱相关路由统一挂在 /api/scores 下，并集中声明可能出现的错误状态码。
    public static void MapScoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 统一声明这些接口可能返回的错误码，前端可以按状态码统一处理；
        // 依次是参数错、未授权、找不到、冲突、文件太大、格式不支持、限流、服务器错。
        var scores = endpoints.MapGroup("/api/scores").WithTags("Scores")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500);
        // No ambient cookie authentication: the explicit capability header protects every score operation.
        // 上传乐谱：multipart 表单（文件 + 可选预处理/引擎），返回 202 和任务地址。
        // 文件先落盘，再交给后台 Worker 排队识别，请求本身很快返回。
        // 接口没有 Cookie 登录态，所以关闭防伪令牌校验，改用下面的令牌头证明身份。
        scores.MapPost("/", async (IFormFile file, [FromForm] string? preprocessing, [FromForm] string? engine, [FromHeader(Name = "X-Score-Token")] string token, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, IScoreService service, CancellationToken ct) =>
        {
            // preprocessing 决定识别前要不要先做图像预处理：默认不处理，Adaptive 由程序自动判断。
            // 同时接受大小写写法，空值按 None 处理，方便前端直接传下拉框的值。
            var mode = preprocessing switch
            {
                null or "" or "None" or "none" => PreprocessingMode.None,
                "Adaptive" or "adaptive" => PreprocessingMode.Adaptive,
                _ => throw new RequestFailure(400, "invalid-preprocessing", "Choose None or Adaptive preprocessing.")
            };
            // engine 选择识别引擎：默认 Audiveris，可选 Homr；同样兼容大小写与空值。
            var selectedEngine = engine switch
            {
                null or "" or "Audiveris" or "audiveris" => RecognitionEngineKind.Audiveris,
                "Homr" or "homr" => RecognitionEngineKind.Homr,
                _ => throw new RequestFailure(400, "invalid-engine", "Choose Audiveris or Homr recognition.")
            };
            // 打开上传文件流交给业务层保存并排队；结果里带新乐谱 id 和初始修订号。
            // 202 表示“已接受、还在处理”，Location 头指向可以轮询的任务地址。
            await using var stream = file.OpenReadStream(); var result = await service.ImportAsync(stream, file.FileName, token, idempotencyKey, ct, mode, selectedEngine);
            return TypedResults.Accepted($"/api/scores/{result.Id}", result);
        }).DisableAntiforgery().WithName("ImportScore").WithSummary("Upload a PDF (1–30 pages) or image; enqueue recognition.");

        // 查任务当前状态（排队/识别中/取消中/完成/失败）和最近修订号。
        scores.MapGet("/{id:guid}", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.GetAsync(id, scoreToken, ct)).WithName("GetScore");

        // 取某个历史修订的乐谱快照，用于回看历史或对比不同版本。
        scores.MapGet("/{id:guid}/revisions/{version:int}", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.RevisionAsync(id, version, scoreToken, ct)).WithName("GetRevision");
        // 提交人工校对：改音高、标注位置或样式，生成一个新修订（版本号加一）。
        // ExpectedRevision 是客户端看到的版本号；对不上返回 409，防止两人同时改互相覆盖。
        scores.MapPost("/{id:guid}/revisions", async (Guid id, RevisionRequest revision, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.CorrectAsync(id, scoreToken, revision, ct);
            return TypedResults.Accepted($"/api/scores/{id}/revisions/{result.Version}", result);
        }).WithName("CreateRevision");

        // 取单个页面的排版结果（annotations.json），前端据此在画布上画简谱数字。
        // 如果 id 指向的是多页文档，要先定位到某一页再来取，否则返回 400。
        scores.MapGet("/{id:guid}/revisions/{version:int}/layout", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var job = await service.GetAsync(id, scoreToken, ct);
            if (job.PageCount > 1) throw new RequestFailure(400, "select-page", "Select an individual page to retrieve its layout.");
            var file = await service.ArtifactAsync(id, version, "annotations.json", scoreToken, ct);
            // 排版文件还不存在（没识别完或识别失败）时返回 404，而不是空内容。
            if (!File.Exists(file.Path)) throw new RequestFailure(404, "layout-not-found", "The requested layout is not available.");
            return TypedResults.Ok(ScoreJson.Read<AnnotationsView>(await File.ReadAllTextAsync(file.Path, ct)));
        }).WithName("GetRevisionLayout").WithSummary("Typed layout for an individual page revision.").Produces<AnnotationsView>(200);

        // 下载已标注好的 PDF 成品（原谱上已经画好简谱数字），支持断点续传。
        // 用 PhysicalFile 直接发磁盘文件，避免把整份 PDF 读进内存。
        scores.MapGet("/{id:guid}/revisions/{version:int}/annotated-pdf", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "annotated.pdf", scoreToken, ct))
            .WithName("GetAnnotatedPdf").Produces(200, contentType: "application/octet-stream").Produces(206, contentType: "application/octet-stream");

        // 下载原始标注数据（annotations.json），给需要二次处理的调用方。
        scores.MapGet("/{id:guid}/revisions/{version:int}/annotations", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "annotations.json", scoreToken, ct))
            .WithName("GetRevisionAnnotations").Produces(200, contentType: "application/json");

        // 下载识别指标（metrics.json），用来评估这一版的识别质量。
        scores.MapGet("/{id:guid}/revisions/{version:int}/metrics", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, version, "metrics.json", scoreToken, ct))
            .WithName("GetRevisionMetrics").Produces(200, contentType: "application/json");

        // 导出整份文档：把各页修订合成一份 PDF，后台做完后返回 202 和查询地址。
        // 和识别一样是异步任务，前端轮询下面的 GetDocumentExport 看进度。
        scores.MapPost("/{id:guid}/exports", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.ExportAsync(id, scoreToken, ct);
            return TypedResults.Accepted($"/api/scores/{id}/exports/{result.Version}", result);
        }).WithName("CreateDocumentExport");
        // 查导出任务状态：还在排队、正在生成，还是已经可以下载。
        scores.MapGet("/{id:guid}/exports/{version:int}", async (Guid id, int version, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) => await service.ExportStatusAsync(id, version, scoreToken, ct)).WithName("GetDocumentExport");

        // 取消还没做完的任务：排队中的直接取消，正在识别的会尽快停下。
        // 返回 202 和最新状态，前端继续轮询就能看到“已取消”。
        scores.MapPost("/{id:guid}/cancellations", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.CancelAsync(id, scoreToken, ct);
            return TypedResults.Accepted($"/api/scores/{id}", result);
        }).WithName("CreateCancellation").Produces<JobView>(202);

        // 重试失败的任务：把同一份原谱重新排队识别。
        // 失败原因保留在任务状态里，方便先排查再决定要不要重试。
        scores.MapPost("/{id:guid}/retries", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, [FromBody] RetryRequest? request, IScoreService service, CancellationToken ct) =>
        {
            var result = await service.RetryAsync(id, scoreToken, ct, request);
            return TypedResults.Accepted($"/api/scores/{id}", result);
        }).WithName("CreateRetry").Produces<JobView>(202);

        // 下载用户上传的原谱文件（source），前端可用来重新预览。
        scores.MapGet("/{id:guid}/source", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, null, "source", scoreToken, ct))
            .WithName("GetScoreSource").Produces(200, contentType: "application/octet-stream").Produces(206, contentType: "application/octet-stream");

        // 下载识别结果转成的 MusicXML（score.mxl），可导入其他打谱软件编辑。
        scores.MapGet("/{id:guid}/musicxml", async (Guid id, [FromHeader(Name = "X-Score-Token")] string scoreToken, IScoreService service, CancellationToken ct) =>
            await ReadArtifactAsync(service, id, null, "score.mxl", scoreToken, ct))
            .WithName("GetScoreMusicXml").Produces(200, contentType: "application/octet-stream");
    }

    // 产物下载的公共方法：向业务层要文件信息，再交给框架把文件流发出去。
    // enableRangeProcessing 允许按字节范围读取（206），大文件可以断点续传。
    private static async Task<IResult> ReadArtifactAsync(IScoreService service, Guid id, int? version, string name, string token, CancellationToken ct)
    {
        var file = await service.ArtifactAsync(id, version, name, token, ct);
        return TypedResults.PhysicalFile(file.Path, file.ContentType, file.DownloadName, enableRangeProcessing: true);
    }
}
