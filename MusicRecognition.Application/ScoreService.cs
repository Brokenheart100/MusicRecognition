using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Application;

public sealed class ScoreService(IScoreRepository db, IScoreStorage files) : IScoreService
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static JobView View(ScoreJob j) => new(j.Id,j.FileName,j.State,j.Attempts,j.CurrentRevision,j.CancelRequested,j.ErrorCode,j.ErrorMessage,j.CreatedAt,j.UpdatedAt);
    private static string Token(string token)
    {
        if (token.Length is < 32 or > 200) throw new RequestFailure(401,"invalid-token","Supply X-Score-Token with at least 32 random characters.");
        return Hash(token);
    }
    private async Task<ScoreJob> Authorized(Guid id, string token, CancellationToken ct)
    {
        var hash = Token(token);
        var j = await db.FindJobAsync(id,ct);
        if (j is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash),Encoding.ASCII.GetBytes(j.TokenHash)))
            throw new RequestFailure(404,"not-found","Score not found.");
        return j;
    }
    public async Task<JobView> ImportAsync(Stream stream,string filename,string token,string idempotencyKey,CancellationToken ct)
    {
        var hash=Token(token);
        if (idempotencyKey.Length is < 8 or > 100) throw new RequestFailure(400,"invalid-idempotency-key","Supply Idempotency-Key (8..100 characters).");
        var requestHash=Hash(hash+":"+idempotencyKey); var id=Guid.NewGuid();
        var saved=await files.SaveSourceAsync(id,stream,filename,ct);
        var existing=await db.FindRequestAsync(requestHash,ct);
        if (existing is not null) { files.DiscardSource(id,saved.Extension); return Match(existing,saved.Hash); }
        var job=new ScoreJob { Id=id,TokenHash=hash,RequestHash=requestHash,FileHash=saved.Hash,Extension=saved.Extension,FileName=Path.GetFileName(filename)[..Math.Min(Path.GetFileName(filename).Length,200)] };
        db.Add(job);
        try { await db.SaveAsync(ct); }
        catch (RequestFailure e) when (e.Code=="duplicate-request")
        {
            db.Clear(); files.DiscardSource(id,saved.Extension);
            return Match((await db.FindRequestAsync(requestHash,ct))!,saved.Hash);
        }
        return View(job);
    }
    private static JobView Match(ScoreJob job,string fileHash) => job.FileHash==fileHash ? View(job) : throw new RequestFailure(409,"idempotency-conflict","This key already belongs to a different file.");
    public async Task<JobView> GetAsync(Guid id,string token,CancellationToken ct) => View(await Authorized(id,token,ct));
    public async Task<RevisionView> RevisionAsync(Guid id,int? version,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        var r=await db.FindRevisionAsync(id,version??j.CurrentRevision,ct) ?? throw new RequestFailure(409,"result-not-ready","No revision is available yet.");
        return new(r.Version,r.State,ScoreJson.Read<ScoreSnapshot>(r.Snapshot),r.ErrorCode);
    }
    public async Task<RevisionView> CorrectAsync(Guid id,string token,RevisionRequest request,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if(j.State!="Ready" || j.CancelRequested) throw new RequestFailure(409,"score-not-ready","Wait for recognition before correcting.");
        if(request.ExpectedRevision!=j.CurrentRevision) throw new RequestFailure(409,"revision-conflict","Reload the current revision before saving.");
        if(request.Numbering is null || request.Corrections is null || request.Corrections.Length>10000) throw new ArgumentException("Missing or excessive corrections.");
        request.Numbering.Validate();
        var old=await db.FindRevisionAsync(id,j.CurrentRevision,ct) ?? throw new InvalidOperationException("Missing revision.");
        var snapshot=ScoreJson.Read<ScoreSnapshot>(old.Snapshot);
        if(request.Corrections.Any(c=>c is null || c.Pitch is null) || request.Corrections.Select(c=>c.NoteId).Distinct().Count()!=request.Corrections.Length ||
           request.Corrections.Any(c=>!snapshot.Notes.Any(n=>n.Id==c.NoteId))) throw new ArgumentException("Corrections contain duplicate or unknown note IDs.");
        var changes=request.Corrections.ToDictionary(c=>c.NoteId,c=>c.Pitch);
        var notes=snapshot.Notes.Select(n=> {var pitch=changes.GetValueOrDefault(n.Id,n.Pitch); return n with {Pitch=pitch,Label=Numbering.Convert(pitch,n.Measure,request.Numbering)};}).ToArray();
        var updated=snapshot with {Numbering=request.Numbering,Notes=notes};
        j.CurrentRevision++; j.UpdatedAt=DateTimeOffset.UtcNow;
        var r=new ScoreRevision {JobId=id,Version=j.CurrentRevision,Snapshot=ScoreJson.Write(updated)}; db.Add(r);
        try {await db.SaveAsync(ct);} catch(RequestFailure e) when(e.Status==409) {throw new RequestFailure(409,"revision-conflict","Another revision was saved. Reload before retrying.");}
        return new(r.Version,r.State,updated,null);
    }
    public async Task<JobView> CancelAsync(Guid id,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if(j.State is "Ready" or "Failed" or "Cancelled") throw new RequestFailure(409,"terminal-job","Only queued or processing recognition jobs can be cancelled.");
        j.CancelRequested=true; j.State=j.State is "Processing" or "Cancelling"?"Cancelling":"Cancelled"; j.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    public async Task<JobView> RetryAsync(Guid id,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if(j.State is not ("Failed" or "Cancelled")) throw new RequestFailure(409,"not-retryable","Only failed or cancelled jobs can be retried.");
        j.State="Queued"; j.CancelRequested=false; j.BackgroundId=null; j.ErrorCode=null; j.ErrorMessage=null; j.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    public async Task<StoredFile> ArtifactAsync(Guid id,int? version,string name,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if(name=="source") return new(Path.Combine(files.JobDirectory(id),"source"+j.Extension),j.Extension==".pdf"?"application/pdf":j.Extension==".png"?"image/png":"image/jpeg","source"+j.Extension);
        if(name is not ("annotated.pdf" or "annotations.json" or "metrics.json")) throw new RequestFailure(404,"not-found","Artifact not found.");
        var r=await db.FindRevisionAsync(id,version??j.CurrentRevision,ct);
        if(r?.State!="Ready") throw new RequestFailure(409,"export-not-ready","The requested revision export is not ready.");
        return new(Path.Combine(files.JobDirectory(id),"revisions",r.Version.ToString(),name),name.EndsWith("pdf")?"application/pdf":"application/json",$"score-{id}-r{r.Version}-{name}");
    }
}
