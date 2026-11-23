using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application.DTOs;

// 任务视图（job view）：接口返回给前端的任务状态，前端靠它轮询并显示识别进度。
// 一个任务可能是一整份多页文档，也可能被拆成逐页的子任务；Pages 就是子任务列表。
// State 取值见 OpenAPI：Queued、Processing、Cancelling、Cancelled、Ready、Failed、Partial。
public sealed record JobView(Guid Id, string FileName, string State, int Attempts, int CurrentRevision,
    bool CancelRequested, string? ErrorCode, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    PreprocessingMode Preprocessing = PreprocessingMode.None, int PageCount = 1, PageJobView[]? Pages = null,
    RecognitionEngineKind Engine = RecognitionEngineKind.Audiveris);
// 单页视图：页码、子任务 ID、状态、当前修订版本、错误码和该页的导出状态（ExportState）。
public sealed record PageJobView(int Number, Guid Id, string State, int CurrentRevision, string? ErrorCode, string? ExportState);
