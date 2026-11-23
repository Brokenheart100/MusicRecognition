namespace MusicRecognition.Application.DTOs;

public sealed record DocumentPageRef(int Number, Guid Id, int Version);
public sealed record DocumentExportSnapshot(DocumentPageRef[] Pages, string Kind = "document");
public sealed record DocumentExportView(int Version, string State, DocumentPageRef[] Pages, string? ErrorCode);
