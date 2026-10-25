using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using Npgsql;

namespace MusicRecognition.Infrastructure;

public sealed class ScoreRepository(ScoreDb db, ILogger<ScoreRepository> logger) : IScoreRepository
{
    public Task<ScoreJob?> FindJobAsync(Guid id, CancellationToken ct) => db.Jobs.SingleOrDefaultAsync(j => j.Id == id, ct);
    public Task<ScoreJob?> FindRequestAsync(string hash, CancellationToken ct) => db.Jobs.SingleOrDefaultAsync(j => j.RequestHash == hash, ct);
    public async Task<ScoreRevision?> FindRevisionAsync(Guid id, int version, CancellationToken ct) => await db.Revisions.FindAsync([id, version], ct);
    public Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct) => db.Jobs.Where(j => j.ParentId == parentId).OrderBy(j => j.PageNumber).ToArrayAsync(ct);
    public Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct) => db.Jobs.AsNoTracking().Where(j => j.ParentId == parentId).OrderBy(j => j.PageNumber)
        .Select(j => new PageJobView(j.PageNumber, j.Id, j.State, j.CurrentRevision, j.ErrorCode,
            db.Revisions.Where(r => r.JobId == j.Id && r.Version == j.CurrentRevision).Select(r => r.State).FirstOrDefault())).ToArrayAsync(ct);
    public void Add(ScoreJob job) => db.Add(job);
    public void Add(ScoreRevision revision) => db.Add(revision);
    public void Clear() => db.ChangeTracker.Clear();
    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("💾 乐谱任务状态已保存");
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("⚠️ 保存乐谱状态时发生并发冲突，请重新加载后重试");
            throw new RequestFailure(409, "concurrency-conflict", "State changed. Reload before retrying.");
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } p)
        {
            logger.LogWarning(e, "⚠️ 保存乐谱状态时遇到重复记录：约束 {Constraint}", p.ConstraintName);
            throw new RequestFailure(409, p.ConstraintName == "IX_Jobs_RequestHash" ? "duplicate-request" : "revision-conflict", "This request or revision was already saved.");
        }
    }
}
