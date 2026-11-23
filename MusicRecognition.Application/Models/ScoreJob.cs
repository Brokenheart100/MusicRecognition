using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application;

// 识别任务（job）在应用层的状态记录，对应数据库里的任务表。
// 一份多页 PDF 先生成一个父任务（ParentId 为空、PagesPrepared 表示是否已拆页），
// 再拆出每页一个子任务（ParentId 指向父任务、PageNumber 是页码）；单页文件只有父任务。
// 前后端都靠 State 判断进度；BackgroundId 是后台队列（Hangfire）的作业号，为空表示还没调度。
// Application-owned state contracts. EF mapping, xmin and Hangfire are infrastructure concerns.
public sealed class ScoreJob
{
    public Guid Id { get; set; }
    // 令牌的哈希值，数据库不存明文；每次请求用它比对。
    public string TokenHash { get; set; } = "";
    // 幂等键的哈希：同一用户重复提交同一文件同一个键时，返回同一个任务。
    public string RequestHash { get; set; } = "";
    // 原稿内容哈希，用来确认幂等请求是不是同一份文件。
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public PreprocessingMode Preprocessing { get; set; } = PreprocessingMode.None;
    public RecognitionEngineKind Engine { get; set; } = RecognitionEngineKind.Audiveris;
    public Guid? ParentId { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    // 多页 PDF 是否已拆成逐页子任务；拆页成功一次后不再重复拆。
    public bool PagesPrepared { get; set; }
    // 任务状态：Queued 排队、Processing 识别中、Ready 可校对、Failed 失败等。
    public string State { get; set; } = "Queued";
    // 识别尝试次数，重试时加一，方便排查反复失败的任务。
    public int Attempts { get; set; }
    // 当前最新修订号；用户每保存一次校对就加一。
    public int CurrentRevision { get; set; }
    // 用户点了取消；后台任务发现后尽快停下，状态随后变成 Cancelled。
    public bool CancelRequested { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    // 后台队列的作业号；为空时调度器会把它加入队列，避免重复投递。
    public string? BackgroundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    // 区域重识别的选区（识别图像素坐标）；为空表示普通整页识别。
    public double? RegionX { get; set; }
    public double? RegionY { get; set; }
    public double? RegionWidth { get; set; }
    public double? RegionHeight { get; set; }
    // 选区合并的基准修订号：Worker 从这一版快照里替换区域内音符。
    public int? RegionBaseRevision { get; set; }
    // PostgreSQL 的行版本号（xmin），用于发现两个请求同时改同一行。
    public uint Version { get; set; }
}
