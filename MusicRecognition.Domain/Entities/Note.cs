using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Domain.Entities;

public sealed record Note(Guid Id, int InterId, int Measure, int Part, int Staff, int Voice,
    Pitch Pitch, Rect Bounds, NumberedLabel Label, AnnotationPlacement? Placement = null, AnnotationStyle? Style = null);
