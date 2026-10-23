using System.Text.Json;
using System.Text.Json.Serialization;
using MusicRecognition.Domain;

namespace MusicRecognition.Application;

public static class ScoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}
public sealed class RequestFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed record Correction(Guid NoteId, Pitch Pitch);
public sealed record PlacementChange(Guid NoteId, AnnotationPlacement Placement);
public sealed record StyleChange(Guid NoteId, AnnotationStyle? Style);
public enum PreprocessingMode { None, Adaptive }
public sealed record RevisionRequest(int ExpectedRevision, Correction[] Corrections, NumberingOptions Numbering,
    PlacementChange[]? Placements = null, AnnotationStyle? AnnotationStyle = null, StyleChange[]? StyleChanges = null);
public sealed record JobView(Guid Id, string FileName, string State, int Attempts, int CurrentRevision,
    bool CancelRequested, string? ErrorCode, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    PreprocessingMode Preprocessing = PreprocessingMode.None, int PageCount = 1, PageJobView[]? Pages = null);
public sealed record PageJobView(int Number, Guid Id, string State, int CurrentRevision, string? ErrorCode, string? ExportState);
public sealed record DocumentPageRef(int Number, Guid Id, int Version);
public sealed record DocumentExportSnapshot(DocumentPageRef[] Pages, string Kind = "document");
public sealed record DocumentExportView(int Version, string State, DocumentPageRef[] Pages, string? ErrorCode);
public sealed record PreparedDocument(int PageCount);
public sealed record RevisionView(int Version, string State, ScoreSnapshot Score, string? ErrorCode);
public sealed record StoredFile(string Path, string ContentType, string DownloadName);
public interface IScoreService
{
    Task<JobView> ImportAsync(Stream stream, string filename, string token, string idempotencyKey, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None);
    Task<JobView> GetAsync(Guid id, string token, CancellationToken ct);
    Task<RevisionView> RevisionAsync(Guid id, int? version, string token, CancellationToken ct);
    Task<RevisionView> CorrectAsync(Guid id, string token, RevisionRequest request, CancellationToken ct);
    Task<JobView> CancelAsync(Guid id, string token, CancellationToken ct);
    Task<JobView> RetryAsync(Guid id, string token, CancellationToken ct);
    Task<StoredFile> ArtifactAsync(Guid id, int? version, string name, string token, CancellationToken ct);
    Task<DocumentExportView> ExportAsync(Guid id, string token, CancellationToken ct);
    Task<DocumentExportView> ExportStatusAsync(Guid id, int version, string token, CancellationToken ct);
}
public interface IScoreStorage
{
    string JobDirectory(Guid id);
    Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct);
    void DiscardSource(Guid id,string extension);
}
public interface IRecognitionEngine
{
    Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None);
    Task RenderAsync(Guid id, int version, string extension, ScoreSnapshot snapshot, CancellationToken ct);
    Task<PreparedDocument> PrepareDocumentAsync(Guid id, string extension, CancellationToken ct);
    Task MergeDocumentAsync(Guid id, int version, DocumentExportSnapshot snapshot, CancellationToken ct);
}
