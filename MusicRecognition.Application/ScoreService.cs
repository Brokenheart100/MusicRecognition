using System.Security.Cryptography;
using System.Text;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Application;

public sealed class ScoreService(IScoreRepository db, IScoreStorage files) : IScoreService
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static JobView View(ScoreJob j) => new(j.Id,j.FileName,j.State,j.Attempts,j.CurrentRevision,j.CancelRequested,j.ErrorCode,j.ErrorMessage,j.CreatedAt,j.UpdatedAt,j.Preprocessing,j.PageCount);
    private async Task<JobView> WithPages(ScoreJob job, CancellationToken ct)
    {
        var view = View(job);
        if (job.PageCount <= 1) return view;
        var pages = await db.PageViewsAsync(job.Id, ct);
        var running = pages.Any(p => p.State is "Queued" or "Processing" or "Cancelling");
        var state = job.CancelRequested ? running ? "Cancelling" : "Cancelled" : running ? "Processing" :
            pages.All(p => p.State == "Ready") ? "Ready" : pages.Any(p => p.State == "Ready") ? "Partial" : "Failed";
        return view with { Pages = pages, State = state, ErrorCode = state is "Partial" or "Failed" ? "page-recognition-failed" : null };
    }
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
    public async Task<JobView> ImportAsync(Stream stream,string filename,string token,string idempotencyKey,CancellationToken ct,PreprocessingMode preprocessing = PreprocessingMode.None)
    {
        if (!Enum.IsDefined(preprocessing)) throw new RequestFailure(400,"invalid-preprocessing","Choose None or Adaptive preprocessing.");
        var hash=Token(token);
        if (idempotencyKey.Length is < 8 or > 100) throw new RequestFailure(400,"invalid-idempotency-key","Supply Idempotency-Key (8..100 characters).");
        var requestHash=Hash(hash+":"+idempotencyKey); var id=Guid.NewGuid();
        var saved=await files.SaveSourceAsync(id,stream,filename,ct);
        var existing=await db.FindRequestAsync(requestHash,ct);
        if (existing is not null) { files.DiscardSource(id,saved.Extension); return Match(existing,saved.Hash,preprocessing); }
        var job=new ScoreJob { Id=id,TokenHash=hash,RequestHash=requestHash,FileHash=saved.Hash,Extension=saved.Extension,Preprocessing=preprocessing,FileName=Path.GetFileName(filename)[..Math.Min(Path.GetFileName(filename).Length,200)] };
        db.Add(job);
        try { await db.SaveAsync(ct); }
        catch (RequestFailure e) when (e.Code=="duplicate-request")
        {
            db.Clear(); files.DiscardSource(id,saved.Extension);
            return Match((await db.FindRequestAsync(requestHash,ct))!,saved.Hash,preprocessing);
        }
        return View(job);
    }
    private static JobView Match(ScoreJob job,string fileHash,PreprocessingMode preprocessing) => job.FileHash==fileHash && job.Preprocessing==preprocessing ? View(job) : throw new RequestFailure(409,"idempotency-conflict","This key already belongs to a different file or preprocessing mode.");
    public async Task<JobView> GetAsync(Guid id,string token,CancellationToken ct) => await WithPages(await Authorized(id,token,ct),ct);
    public async Task<RevisionView> RevisionAsync(Guid id,int? version,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if (j.Extension==FragmentService.Extension) throw new RequestFailure(409,"fragment-project","请在片段拼谱界面读取此项目。");
        if (j.PageCount > 1) throw new RequestFailure(400,"select-page","Select a page task to read its revisions.");
        var r=await db.FindRevisionAsync(id,version??j.CurrentRevision,ct) ?? throw new RequestFailure(409,"result-not-ready","No revision is available yet.");
        return new(r.Version,r.State,ScoreJson.Read<ScoreSnapshot>(r.Snapshot),r.ErrorCode);
    }
    public async Task<RevisionView> CorrectAsync(Guid id,string token,RevisionRequest request,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if (j.Extension==FragmentService.Extension) throw new RequestFailure(409,"fragment-project","请在片段拼谱界面编辑此项目。");
        if (j.PageCount > 1) throw new RequestFailure(400,"select-page","Select a page task before correcting.");
        if(j.State!="Ready" || j.CancelRequested) throw new RequestFailure(409,"score-not-ready","Wait for recognition before correcting.");
        if(request.ExpectedRevision!=j.CurrentRevision) throw new RequestFailure(409,"revision-conflict","Reload the current revision before saving.");
        if(request.Numbering is null || request.Corrections is null || request.Corrections.Length>10000) throw new ArgumentException("Missing or excessive corrections.");
        request.Numbering.Validate();
        var old=await db.FindRevisionAsync(id,j.CurrentRevision,ct) ?? throw new InvalidOperationException("Missing revision.");
        var snapshot=ScoreJson.Read<ScoreSnapshot>(old.Snapshot);
        if(request.Corrections.Any(c=>c is null || c.Pitch is null) || request.Corrections.Select(c=>c.NoteId).Distinct().Count()!=request.Corrections.Length ||
           request.Corrections.Any(c=>!snapshot.Notes.Any(n=>n.Id==c.NoteId))) throw new ArgumentException("Corrections contain duplicate or unknown note IDs.");
        var changes=request.Corrections.ToDictionary(c=>c.NoteId,c=>c.Pitch);
        var placements=request.Placements ?? [];
        if (placements.Length>10000 || placements.Any(c=>c is null || c.Placement is null) ||
            placements.Select(c=>c.NoteId).Distinct().Count()!=placements.Length ||
            placements.Any(c=>!snapshot.Notes.Any(n=>n.Id==c.NoteId)))
            throw new ArgumentException("Placements contain duplicate or unknown note IDs.");
        foreach (var change in placements) change.Placement.Validate(snapshot.Width,snapshot.Height);
        var positions=placements.ToDictionary(c=>c.NoteId,c=>c.Placement);
        var styles=request.StyleChanges ?? [];
        if (styles.Length>10000 || styles.Any(c=>c is null) ||
            styles.Select(c=>c.NoteId).Distinct().Count()!=styles.Length ||
            styles.Any(c=>!snapshot.Notes.Any(n=>n.Id==c.NoteId)))
            throw new ArgumentException("Style changes contain duplicate or unknown note IDs.");
        foreach (var change in styles) change.Style?.Validate();
        var overrides=styles.ToDictionary(c=>c.NoteId,c=>c.Style);
        var notes=snapshot.Notes.Select(n=> {var pitch=changes.GetValueOrDefault(n.Id,n.Pitch); return n with {Pitch=pitch,Label=Numbering.Convert(pitch,n.Measure,request.Numbering),Placement=positions.TryGetValue(n.Id,out var position)?position:n.Placement,Style=overrides.TryGetValue(n.Id,out var noteStyle)?noteStyle:n.Style};}).ToArray();
        var style = request.AnnotationStyle ?? snapshot.AnnotationStyle ?? AnnotationStyle.Default;
        style.Validate();
        var updated=snapshot with {Numbering=request.Numbering,Notes=notes,AnnotationStyle=style};
        j.CurrentRevision++; j.UpdatedAt=DateTimeOffset.UtcNow;
        var r=new ScoreRevision {JobId=id,Version=j.CurrentRevision,Snapshot=ScoreJson.Write(updated)}; db.Add(r);
        try {await db.SaveAsync(ct);} catch(RequestFailure e) when(e.Status==409) {throw new RequestFailure(409,"revision-conflict","Another revision was saved. Reload before retrying.");}
        return new(r.Version,r.State,updated,null);
    }
    public async Task<JobView> CancelAsync(Guid id,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if (j.PageCount > 1)
        {
            var view = await WithPages(j, ct);
            if (view.State is "Ready" or "Failed" or "Partial" or "Cancelled") throw new RequestFailure(409,"terminal-job","No active recognition remains.");
            foreach (var page in await db.FindPagesAsync(id, ct))
                if (page.State is "Queued" or "Processing" or "Cancelling") { page.CancelRequested = true; page.State = page.State == "Queued" ? "Cancelled" : "Cancelling"; page.UpdatedAt = DateTimeOffset.UtcNow; }
            j.CancelRequested = true; j.State = "Cancelling"; j.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveAsync(ct); return await WithPages(j, ct);
        }
        if(j.State is "Ready" or "Failed" or "Cancelled") throw new RequestFailure(409,"terminal-job","Only queued or processing recognition jobs can be cancelled.");
        j.CancelRequested=true; j.State=j.State is "Processing" or "Cancelling"?"Cancelling":"Cancelled"; j.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    public async Task<JobView> RetryAsync(Guid id,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if (j.PageCount > 1)
        {
            var pages = await db.FindPagesAsync(id, ct);
            var retryable = pages.Where(p => p.State is "Failed" or "Cancelled").ToArray();
            if (retryable.Length == 0) throw new RequestFailure(409,"not-retryable","No failed or cancelled page can be retried.");
            foreach (var page in retryable) { page.State = "Queued"; page.CancelRequested = false; page.BackgroundId = null; page.ErrorCode = null; page.ErrorMessage = null; page.UpdatedAt = DateTimeOffset.UtcNow; }
            j.State = "Processing"; j.CancelRequested = false; j.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveAsync(ct); return await WithPages(j, ct);
        }
        if(j.State is not ("Failed" or "Cancelled")) throw new RequestFailure(409,"not-retryable","Only failed or cancelled jobs can be retried.");
        j.State="Queued"; j.CancelRequested=false; j.BackgroundId=null; j.ErrorCode=null; j.ErrorMessage=null; j.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    public async Task<StoredFile> ArtifactAsync(Guid id,int? version,string name,string token,CancellationToken ct)
    {
        var j=await Authorized(id,token,ct);
        if (j.Extension==FragmentService.Extension) throw new RequestFailure(409,"fragment-project","请从片段拼谱界面下载此项目。");
        if(name=="source") return new(Path.Combine(files.JobDirectory(id),"source"+j.Extension),j.Extension==".pdf"?"application/pdf":j.Extension==".png"?"image/png":"image/jpeg","source"+j.Extension);
        if(name=="score.mxl")
        {
            if(j.PageCount > 1) throw new RequestFailure(409,"page-required","Select an individual page for MusicXML preview.");
            var path=Path.Combine(files.JobDirectory(id),"raw","score.mxl");
            if(j.State!="Ready" || !File.Exists(path)) throw new RequestFailure(409,"recognition-not-ready","MusicXML is available after successful recognition.");
            return new(path,"application/vnd.recordare.musicxml",$"score-{id}.mxl");
        }
        if(name is not ("annotated.pdf" or "annotations.json" or "metrics.json")) throw new RequestFailure(404,"not-found","Artifact not found.");
        var r=await db.FindRevisionAsync(id,version??j.CurrentRevision,ct);
        if(r?.State!="Ready") throw new RequestFailure(409,"export-not-ready","The requested revision export is not ready.");
        return new(Path.Combine(files.JobDirectory(id),"revisions",r.Version.ToString(),name),name.EndsWith("pdf")?"application/pdf":"application/json",$"score-{id}-r{r.Version}-{name}");
    }
    public async Task<DocumentExportView> ExportAsync(Guid id, string token, CancellationToken ct)
    {
        var job = await Authorized(id, token, ct);
        if (job.PageCount <= 1) throw new RequestFailure(400,"single-page-export","Use the page revision download for a single page.");
        var pages = await db.PageViewsAsync(id, ct);
        if (pages.Length != job.PageCount || pages.Any(p => p.State != "Ready" || p.ExportState != "Ready"))
            throw new RequestFailure(409,"pages-not-ready","Every page must be recognized and its current revision exported before exporting the document.");
        var snapshot = new DocumentExportSnapshot(pages.Select(p => new DocumentPageRef(p.Number, p.Id, p.CurrentRevision)).ToArray());
        var json = ScoreJson.Write(snapshot);
        var previous = await db.FindRevisionAsync(id, job.CurrentRevision, ct);
        if (previous is not null && previous.Snapshot == json && previous.State != "Failed")
            return new(previous.Version, previous.State, snapshot.Pages, previous.ErrorCode);
        job.CurrentRevision++; job.UpdatedAt = DateTimeOffset.UtcNow;
        var revision = new ScoreRevision { JobId = id, Version = job.CurrentRevision, Snapshot = json };
        db.Add(revision); await db.SaveAsync(ct);
        return new(revision.Version, revision.State, snapshot.Pages, null);
    }
    public async Task<DocumentExportView> ExportStatusAsync(Guid id, int version, string token, CancellationToken ct)
    {
        var job = await Authorized(id, token, ct);
        if (job.PageCount <= 1) throw new RequestFailure(400,"single-page-export","This score is a single page.");
        var revision = await db.FindRevisionAsync(id, version, ct) ?? throw new RequestFailure(404,"not-found","Export not found.");
        return new(revision.Version, revision.State, ScoreJson.Read<DocumentExportSnapshot>(revision.Snapshot).Pages, revision.ErrorCode);
    }
}
