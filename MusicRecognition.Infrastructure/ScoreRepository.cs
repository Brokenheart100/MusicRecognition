using Microsoft.EntityFrameworkCore;
using MusicRecognition.Application;
using Npgsql;

namespace MusicRecognition.Infrastructure;

public sealed class ScoreRepository(ScoreDb db) : IScoreRepository
{
    public Task<ScoreJob?> FindJobAsync(Guid id,CancellationToken ct)=>db.Jobs.SingleOrDefaultAsync(j=>j.Id==id,ct);
    public Task<ScoreJob?> FindRequestAsync(string hash,CancellationToken ct)=>db.Jobs.SingleOrDefaultAsync(j=>j.RequestHash==hash,ct);
    public async Task<ScoreRevision?> FindRevisionAsync(Guid id,int version,CancellationToken ct)=>await db.Revisions.FindAsync([id,version],ct);
    public void Add(ScoreJob job)=>db.Add(job);
    public void Add(ScoreRevision revision)=>db.Add(revision);
    public void Clear()=>db.ChangeTracker.Clear();
    public async Task SaveAsync(CancellationToken ct)
    {
        try {await db.SaveChangesAsync(ct);}
        catch(DbUpdateConcurrencyException) {throw new RequestFailure(409,"concurrency-conflict","State changed. Reload before retrying.");}
        catch(DbUpdateException e) when(e.InnerException is PostgresException {SqlState:"23505"} p)
        {throw new RequestFailure(409,p.ConstraintName=="IX_Jobs_RequestHash"?"duplicate-request":"revision-conflict","This request or revision was already saved.");}
    }
}
