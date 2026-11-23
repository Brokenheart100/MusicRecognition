namespace MusicRecognition.Application.DTOs;

public sealed record StoredFile(string Path, string ContentType, string DownloadName);
