using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application;

// Application-owned state contracts. EF mapping, xmin and Hangfire are infrastructure concerns.
public sealed class ScoreJob
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public PreprocessingMode Preprocessing { get; set; } = PreprocessingMode.None;
    public RecognitionEngineKind Engine { get; set; } = RecognitionEngineKind.Audiveris;
    public Guid? ParentId { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    public bool PagesPrepared { get; set; }
    public string State { get; set; } = "Queued";
    public int Attempts { get; set; }
    public int CurrentRevision { get; set; }
    public bool CancelRequested { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BackgroundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
}
