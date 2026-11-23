using MusicRecognition.Application.DTOs;

namespace MusicRecognition.Application.Interfaces;

public interface IScoreRepository
{
    Task<ScoreJob?> FindJobAsync(Guid id,CancellationToken ct);
    Task<ScoreJob?> FindRequestAsync(string hash,CancellationToken ct);
    Task<ScoreRevision?> FindRevisionAsync(Guid id,int version,CancellationToken ct);
    Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct);
    Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct);
    void Add(ScoreJob job);
    void Add(ScoreRevision revision);
    Task SaveAsync(CancellationToken ct);
    void Clear();
}
