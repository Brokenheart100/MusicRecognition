using MusicRecognition.Domain;

namespace MusicRecognition.Application;

public sealed record FragmentUpload(string FileName, Stream Content);
public sealed record FragmentSource(int Index, string FileName, string RelativePath, string Sha256);
public sealed record FragmentInput(FragmentSource[] Sources, FragmentPlan Plan);
public sealed record FragmentImage(int Index, string FileName);
public sealed record FragmentSummary(int Index, string FileName, int MeasureCount, int StartMeasure, int EndMeasure, int KeptMeasureCount);
public sealed record FragmentResult(int MeasureCount, int NoteCount, int PdfPages, string[] Warnings, FragmentSummary[] Fragments,
    string Music21Version, string RecognitionKey, bool SelectedEventsPreserved, bool ReviewConfirmed);
public sealed record FragmentView(Guid Id, string State, int Version, string Stage, string? ErrorCode, string? ErrorMessage,
    FragmentImage[] Sources, FragmentPlan Plan, FragmentResult? Result);
public sealed record FragmentRevisionRequest(int ExpectedRevision, FragmentPlan Plan);
public interface IFragmentStorage
{
    Task<string> CreateAsync(Guid id, FragmentUpload[] uploads, FragmentPlan plan, CancellationToken ct);
    Task<FragmentInput> InputAsync(Guid id, CancellationToken ct);
    Task<string> StageAsync(Guid id, CancellationToken ct);
    Task<FragmentResult?> ResultAsync(Guid id, int version, CancellationToken ct);
    StoredFile Artifact(Guid id, int version, string name);
    Task<StoredFile> ImageAsync(Guid id, int index, CancellationToken ct);
    void DiscardUnpublished(Guid id);
}
public interface IFragmentEngine { Task ComposeAsync(Guid id, int version, FragmentPlan plan, CancellationToken ct); }

public sealed class FragmentService(IScoreRepository db, IFragmentStorage storage, IScoreService scores)
{
    public const string Extension = ".fragments";
    private async Task<ScoreJob> Authorized(Guid id, string token, CancellationToken ct)
    {
        await scores.GetAsync(id, token, ct);
        var job = (await db.FindJobAsync(id,ct))!;
        if (job.Extension != Extension) throw new RequestFailure(404,"not-found","拼谱项目不存在。");
        return job;
    }
    public async Task<FragmentView> ImportAsync(FragmentUpload[] uploads, FragmentPlan plan, string token, string key, CancellationToken ct)
    {
        if (token.Length is < 32 or > 200) throw new RequestFailure(401,"invalid-token","需要有效的项目访问凭据。");
        if (key.Length is < 8 or > 100) throw new ArgumentException("请求标识需要 8–100 个字符。");
        if (uploads.Length is < 1 or > 12) throw new ArgumentException("每个项目支持 1–12 张图片。");
        plan.Validate(uploads.Length);
        var tokenHash = ScoreService.Hash(token);
        var requestHash = ScoreService.Hash(tokenHash+":fragments:"+key);
        var existing = await db.FindRequestAsync(requestHash,ct);
        // An interrupted upload can retry its original idempotency key; content
        // comparison is performed after receiving all bytes, as in score uploads.
        var id = Guid.NewGuid();
        var hash = await storage.CreateAsync(id,uploads,plan,ct);
        if (existing is not null)
        {
            storage.DiscardUnpublished(id);
            if (existing.FileHash != hash) throw new RequestFailure(409,"idempotency-conflict","相同请求标识不能提交不同片段。");
            return await GetAsync(existing.Id,token,null,ct);
        }
        var job = new ScoreJob { Id=id, TokenHash=tokenHash, RequestHash=requestHash, FileHash=hash, FileName=plan.Title+" · 片段拼谱", Extension=Extension, PagesPrepared=true };
        db.Add(job);
        try { await db.SaveAsync(ct); }
        catch (RequestFailure e) when (e.Code=="duplicate-request")
        {
            storage.DiscardUnpublished(id);
            db.Clear(); existing=await db.FindRequestAsync(requestHash,ct);
            if (existing is null || existing.FileHash!=hash) throw new RequestFailure(409,"idempotency-conflict","相同请求标识不能提交不同片段。");
            return await GetAsync(existing.Id,token,null,ct);
        }
        return await GetAsync(id,token,null,ct);
    }
    public async Task<FragmentView> GetAsync(Guid id, string token, int? version, CancellationToken ct)
    {
        var job=await Authorized(id,token,ct);
        var input=await storage.InputAsync(id,ct);
        var selected=version ?? job.CurrentRevision;
        var revision=selected > 0 ? await db.FindRevisionAsync(id,selected,ct) : null;
        if (selected>0 && revision is null) throw new RequestFailure(404,"not-found","拼谱版本不存在。");
        var plan=revision is null ? input.Plan : ScoreJson.Read<FragmentPlan>(revision.Snapshot);
        return new(id, revision?.State ?? job.State, selected, await storage.StageAsync(id,ct), revision?.ErrorCode ?? job.ErrorCode,
            job.ErrorMessage, input.Sources.Select(s=>new FragmentImage(s.Index,s.FileName)).ToArray(), plan,
            selected>0 ? await storage.ResultAsync(id,selected,ct) : null);
    }
    public async Task<FragmentView> ReviseAsync(Guid id, string token, FragmentRevisionRequest request, CancellationToken ct)
    {
        if (request.Plan is null) throw new ArgumentException("缺少拼谱设置。");
        var job=await Authorized(id,token,ct);
        var recovering=job.CurrentRevision==0 && job.State is "Failed" or "Cancelled";
        if ((!recovering && job.State!="Ready") || request.ExpectedRevision!=job.CurrentRevision) throw new RequestFailure(409,"revision-conflict","请等待当前拼谱完成或刷新到最新版本。");
        var current=await db.FindRevisionAsync(id,job.CurrentRevision,ct);
        if (current?.State is "Queued" or "Processing") throw new RequestFailure(409,"export-not-ready","当前版本仍在生成。");
        var input=await storage.InputAsync(id,ct); request.Plan.Validate(input.Sources.Length);
        job.CurrentRevision++; job.State="Ready"; job.CancelRequested=false; job.ErrorCode=null; job.ErrorMessage=null; job.UpdatedAt=DateTimeOffset.UtcNow;
        db.Add(new ScoreRevision { JobId=id, Version=job.CurrentRevision, Snapshot=ScoreJson.Write(request.Plan) });
        await db.SaveAsync(ct);
        return await GetAsync(id,token,null,ct);
    }
    public async Task<StoredFile> ArtifactAsync(Guid id, string token, int version, string name, CancellationToken ct)
    {
        var view=await GetAsync(id,token,version,ct);
        if (view.State!="Ready") throw new RequestFailure(409,"export-not-ready","请等待当前版本完成。");
        if (name=="score.pdf" && !view.Plan.ReviewConfirmed) throw new RequestFailure(409,"review-required","请先确认拼接顺序及校对提示，再生成 PDF 下载版本。");
        return storage.Artifact(id,version,name);
    }
    public async Task<StoredFile> ImageAsync(Guid id,string token,int index,CancellationToken ct)
    { await Authorized(id,token,ct); return await storage.ImageAsync(id,index,ct); }
    public async Task<FragmentView> CommandAsync(Guid id,string token,bool retry,CancellationToken ct)
    {
        var job=await Authorized(id,token,ct);
        if (retry && job.CurrentRevision>0)
        {
            var revision=await db.FindRevisionAsync(id,job.CurrentRevision,ct);
            if (revision?.State!="Failed") throw new RequestFailure(409,"not-retryable","当前拼谱版本无需重试。");
            return await ReviseAsync(id,token,new(job.CurrentRevision,ScoreJson.Read<FragmentPlan>(revision.Snapshot)),ct);
        }
        if (retry) await scores.RetryAsync(id,token,ct); else await scores.CancelAsync(id,token,ct);
        return await GetAsync(id,token,null,ct);
    }
}
