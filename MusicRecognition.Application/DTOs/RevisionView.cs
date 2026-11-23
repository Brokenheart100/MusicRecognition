using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.DTOs;

public sealed record RevisionView(int Version, string State, ScoreSnapshot Score, string? ErrorCode);
