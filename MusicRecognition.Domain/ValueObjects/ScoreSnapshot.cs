using MusicRecognition.Domain.Entities;

namespace MusicRecognition.Domain.ValueObjects;

public sealed record ScoreSnapshot(int Width, int Height, int Interline, string Engine,
    NumberingOptions Numbering, Note[] Notes, string[] Warnings, AnnotationStyle? AnnotationStyle = null);
