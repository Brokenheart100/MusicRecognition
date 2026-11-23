using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Interfaces;

internal sealed class DiagnosticScoreRepository(params ScoreJob[] jobs) : IScoreRepository
{
    public Task<ScoreJob?> FindJobAsync(Guid id, CancellationToken ct) => Task.FromResult(jobs.SingleOrDefault(j => j.Id == id));
    public Task<ScoreJob?> FindRequestAsync(string hash, CancellationToken ct) => throw new NotSupportedException();
    public Task<ScoreRevision?> FindRevisionAsync(Guid id, int version, CancellationToken ct) => throw new NotSupportedException();
    public Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct) => throw new NotSupportedException();
    public Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct) => throw new NotSupportedException();
    public void Add(ScoreJob job) => throw new NotSupportedException();
    public void Add(ScoreRevision revision) => throw new NotSupportedException();
    public Task SaveAsync(CancellationToken ct) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
}
