using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application.Interfaces;

public interface IScoreService
{
    Task<JobView> ImportAsync(Stream stream, string filename, string token, string idempotencyKey, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris);
    Task<JobView> GetAsync(Guid id, string token, CancellationToken ct);
    Task<RevisionView> RevisionAsync(Guid id, int version, string token, CancellationToken ct);
    Task<RevisionView> CorrectAsync(Guid id, string token, RevisionRequest request, CancellationToken ct);
    Task<JobView> CancelAsync(Guid id, string token, CancellationToken ct);
    Task<JobView> RetryAsync(Guid id, string token, CancellationToken ct);
    Task<StoredFile> ArtifactAsync(Guid id, int? version, string name, string token, CancellationToken ct);
    Task<DocumentExportView> ExportAsync(Guid id, string token, CancellationToken ct);
    Task<DocumentExportView> ExportStatusAsync(Guid id, int version, string token, CancellationToken ct);
}
