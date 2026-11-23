using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.Interfaces;

public interface IRecognitionEngine
{
    Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris);
    Task RenderAsync(Guid id, int version, string extension, ScoreSnapshot snapshot, CancellationToken ct);
    Task<PreparedDocument> PrepareDocumentAsync(Guid id, string extension, CancellationToken ct);
    Task MergeDocumentAsync(Guid id, int version, DocumentExportSnapshot snapshot, CancellationToken ct);
}
