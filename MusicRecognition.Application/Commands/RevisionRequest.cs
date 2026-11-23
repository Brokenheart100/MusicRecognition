using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.Commands;

public sealed record Correction(Guid NoteId, Pitch Pitch);
public sealed record PlacementChange(Guid NoteId, AnnotationPlacement Placement);
public sealed record StyleChange(Guid NoteId, AnnotationStyle? Style);
public sealed record RevisionRequest(int ExpectedRevision, Correction[] Corrections, NumberingOptions Numbering,
    PlacementChange[]? Placements = null, AnnotationStyle? AnnotationStyle = null, StyleChange[]? StyleChanges = null);
