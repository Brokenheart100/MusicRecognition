namespace MusicRecognition.Application.Interfaces;

public interface IScoreStorage
{
    string JobDirectory(Guid id);
    Task<(string Extension, string Hash)> SaveSourceAsync(Guid id, Stream stream, string filename, CancellationToken ct);
    void DiscardSource(Guid id,string extension);
}
