namespace MusicRecognition.Application;

public sealed class ScoreRevision
{
    public Guid JobId { get; set; }
    public int Version { get; set; }
    public string Snapshot { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string? BackgroundId { get; set; }
    public string? ErrorCode { get; set; }
}
