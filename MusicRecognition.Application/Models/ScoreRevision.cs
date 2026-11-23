namespace MusicRecognition.Application;

// 修订（revision）：一次可回看的谱面版本。识别完成是第 1 版，用户每保存一次校对就多一版。
// Snapshot 是整份谱面的 JSON 文本（ScoreSnapshot），数据库不拆字段，读取时反序列化。
// State 表示这个版本的渲染进度：刚保存是 Queued，后台渲染成功变 Ready，失败变 Failed。
// BackgroundId 是对应的后台作业号，用于避免重复渲染以及排查卡住的任务。
public sealed class ScoreRevision
{
    public Guid JobId { get; set; }
    public int Version { get; set; }
    public string Snapshot { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string? BackgroundId { get; set; }
    public string? ErrorCode { get; set; }
}
