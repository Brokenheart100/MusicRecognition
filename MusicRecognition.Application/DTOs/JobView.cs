using MusicRecognition.Application.Enums;

namespace MusicRecognition.Application.DTOs;

public sealed record JobView(Guid Id, string FileName, string State, int Attempts, int CurrentRevision,
    bool CancelRequested, string? ErrorCode, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    PreprocessingMode Preprocessing = PreprocessingMode.None, int PageCount = 1, PageJobView[]? Pages = null,
    RecognitionEngineKind Engine = RecognitionEngineKind.Audiveris);
public sealed record PageJobView(int Number, Guid Id, string State, int CurrentRevision, string? ErrorCode, string? ExportState);
