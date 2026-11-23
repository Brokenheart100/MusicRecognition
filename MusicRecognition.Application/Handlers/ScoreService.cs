using MusicRecognition.Application.Authorization;
using MusicRecognition.Application.Commands;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;
using MusicRecognition.Domain.Entities;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.Handlers;

// 乐谱用例的主实现（application service）：把"上传、查任务、读修订、保存校对、取消/重试、
// 下载产物、整份导出"这些业务步骤串起来，是 API 与数据库、识别引擎之间的中间人。
// 它只做编排和业务校验：读写交给 IScoreRepository，文件交给 IScoreStorage，
// 识别和渲染交给后台任务（Infrastructure 层），这里不直接跑任何外部程序。
// 涉及单个乐谱的方法都先做访问令牌校验（ScoreAccess），再检查任务状态和并发版本。
public sealed class ScoreService(IScoreRepository db, IScoreStorage files) : IScoreService
{
    // 把任务实体转成接口视图（DTO），前端只依赖这个形状，实体字段改名不影响接口契约。
    private static JobView View(ScoreJob j) => new(j.Id, j.FileName, j.State, j.Attempts, j.CurrentRevision, j.CancelRequested, j.ErrorCode, j.ErrorMessage, j.CreatedAt, j.UpdatedAt, j.Preprocessing, j.PageCount, Engine: j.Engine);
    // 多页文档的状态要由子任务汇总：任何一页还在排队或识别，整份就算进行中。
    // 单页任务直接返回，不必多查一次子任务表。
    private async Task<JobView> WithPages(ScoreJob job, CancellationToken ct)
    {
        // 下面先算出"还有页在跑"，再按取消、就绪、部分就绪的优先级归并出一个整体状态。
        var view = View(job);
        if (job.PageCount <= 1) return view;
        var pages = await db.PageViewsAsync(job.Id, ct);
        // running 表示还有页没结束；状态优先级：取消中 > 取消 > 处理中 > 全就绪 > 部分就绪 > 失败。
        var running = pages.Any(p => p.State is "Queued" or "Processing" or "Cancelling");
        var state = job.CancelRequested ? running ? "Cancelling" : "Cancelled" : running ? "Processing" :
            pages.All(p => p.State == "Ready") ? "Ready" : pages.Any(p => p.State == "Ready") ? "Partial" : "Failed";
        // 只有部分页就绪时标记 Partial 并给统一错误码，前端可先让用户校对已完成的页。
        return view with { Pages = pages, State = state, ErrorCode = state is "Partial" or "Failed" ? "page-recognition-failed" : null };
    }
    // 上传入口：校验参数 → 保存原稿 → 幂等查重 → 建任务入库，返回排队中的任务视图。
    // 幂等键（idempotencyKey）让网络抖动后的重试不会生成两个任务：同键同文件返回原任务。
    public async Task<JobView> ImportAsync(Stream stream, string filename, string token, string idempotencyKey, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris)
    {
        // 先挡掉枚举范围外的值，避免脏参数进入后台。
        if (!Enum.IsDefined(preprocessing)) throw new RequestFailure(400, "invalid-preprocessing", "Choose None or Adaptive preprocessing.");
        if (!Enum.IsDefined(engine)) throw new RequestFailure(400, "invalid-engine", "Choose Audiveris or Homr recognition.");
        // 令牌只存哈希（SHA-256），数据库里不保留明文。
        var hash = ScoreAccess.TokenHash(token);
        // 幂等键长度限制 8~100：太短容易撞、太长没必要。
        if (idempotencyKey.Length is < 8 or > 100) throw new RequestFailure(400, "invalid-idempotency-key", "Supply Idempotency-Key (8..100 characters).");
        // 请求哈希把"令牌+幂等键"绑在一起，不同用户的同名键不会串。
        var requestHash = ScoreAccess.Hash(hash + ":" + idempotencyKey); var id = Guid.NewGuid();
        // 先把文件落盘再查重：查重需要内容哈希，而哈希要读完整个文件才算得出。
        var saved = await files.SaveSourceAsync(id, stream, filename, ct);
        var existing = await db.FindRequestAsync(requestHash, ct);
        // 同键同文件同参数：删掉刚存的多余副本，直接返回已有任务。
        if (existing is not null) { files.DiscardSource(id, saved.Extension); return Match(existing, saved.Hash, preprocessing, engine); }
        // 文件名只留最后一段并截到 200 字符，防止超长名字或路径片段进数据库。
        var job = new ScoreJob { Id = id, TokenHash = hash, RequestHash = requestHash, FileHash = saved.Hash, Extension = saved.Extension, Preprocessing = preprocessing, Engine = engine, FileName = Path.GetFileName(filename)[..Math.Min(Path.GetFileName(filename).Length, 200)] };
        db.Add(job);
        // 两个并发上传可能同时通过查重：唯一索引报 duplicate-request 时，回退到已有任务。
        try { await db.SaveAsync(ct); }
        catch (RequestFailure e) when (e.Code == "duplicate-request")
        {
            db.Clear(); files.DiscardSource(id, saved.Extension);
            return Match((await db.FindRequestAsync(requestHash, ct))!, saved.Hash, preprocessing, engine);
        }
        return View(job);
    }
    // 幂等键命中但文件/预处理/引擎不一致：说明这个键被别的请求用过，返回 409，不能悄悄复用。
    private static JobView Match(ScoreJob job, string fileHash, PreprocessingMode preprocessing, RecognitionEngineKind engine) => job.FileHash == fileHash && job.Preprocessing == preprocessing && job.Engine == engine ? View(job) : throw new RequestFailure(409, "idempotency-conflict", "This key already belongs to a different file, preprocessing mode or engine.");
    // 查询任务状态；多页文档会把各页状态一起带回去。
    public async Task<JobView> GetAsync(Guid id, string token, CancellationToken ct) => await WithPages(await ScoreAccess.AuthorizeAsync(db, id, token, ct), ct);
    // 读取指定版本的完整谱面快照，前端据此渲染校对页。
    // 多页文档的父任务没有自己的谱面，必须先选具体一页，所以这里要求单页任务。
    public async Task<RevisionView> RevisionAsync(Guid id, int version, string token, CancellationToken ct)
    {
        var j = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        if (j.PageCount > 1) throw new RequestFailure(400, "select-page", "Select a page task to read its revisions.");
        var r = await db.FindRevisionAsync(id, version, ct) ?? throw new RequestFailure(404, "revision-not-found", "Revision not found.");
        return new(r.Version, r.State, ScoreJson.Read<ScoreSnapshot>(r.Snapshot), r.ErrorCode);
    }
    // 保存人工校对：把音高、标注位置、单音样式、手工补录/删除应用到上一版快照，
    // 再用当前换算设置重算所有简谱数字，生成新修订交给后台重新渲染。
    public async Task<RevisionView> CorrectAsync(Guid id, string token, RevisionRequest request, CancellationToken ct)
    {
        var j = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        if (j.PageCount > 1) throw new RequestFailure(400, "select-page", "Select a page task before correcting.");
        // 只有识别完成且没在取消的任务才能校对，否则会改到马上被覆盖的数据。
        if (j.State != "Ready" || j.CancelRequested) throw new RequestFailure(409, "score-not-ready", "Wait for recognition before correcting.");
        // 乐观并发：前端带的是它看到的那一版，服务器已不是这版就让用户刷新重来。
        if (request.ExpectedRevision != j.CurrentRevision) throw new RequestFailure(409, "revision-conflict", "Reload the current revision before saving.");
        // 防御性校验：缺字段或一次改超过 1 万个音，说明请求异常。
        if (request.Numbering is null || request.Corrections is null || request.Corrections.Length > 10000) throw new ArgumentException("Missing or excessive corrections.");
        // 换算设置本身也要合法（调号范围、Movable Do 的起始调等）。
        request.Numbering.Validate();
        // 取出上一版快照作为底稿；后面的修改都基于它，旧版数据不会被改动。
        var old = await db.FindRevisionAsync(id, j.CurrentRevision, ct) ?? throw new InvalidOperationException("Missing revision.");
        var snapshot = ScoreJson.Read<ScoreSnapshot>(old.Snapshot);
        // 每个音只能改一次且必须属于这份谱，防止前端传错 ID 改到别的乐谱。
        if (request.Corrections.Any(c => c is null || c.Pitch is null) || request.Corrections.Select(c => c.NoteId).Distinct().Count() != request.Corrections.Length ||
           request.Corrections.Any(c => !snapshot.Notes.Any(n => n.Id == c.NoteId))) throw new ArgumentException("Corrections contain duplicate or unknown note IDs.");
        var changes = request.Corrections.ToDictionary(c => c.NoteId, c => c.Pitch);
        // 位置同样要求不重复、属于本谱；坐标越界在后面逐个 Validate。
        var placements = request.Placements ?? [];
        if (placements.Length > 10000 || placements.Any(c => c is null || c.Placement is null) ||
            placements.Select(c => c.NoteId).Distinct().Count() != placements.Length ||
            placements.Any(c => !snapshot.Notes.Any(n => n.Id == c.NoteId)))
            throw new ArgumentException("Placements contain duplicate or unknown note IDs.");
        // 标注位置必须落在识别图像素范围内，防止标到页面外。
        foreach (var change in placements) change.Placement.Validate(snapshot.Width, snapshot.Height);
        var positions = placements.ToDictionary(c => c.NoteId, c => c.Placement);
        // 样式修改也要求音符存在且不重复；null 表示恢复整份谱的默认样式。
        var styles = request.StyleChanges ?? [];
        if (styles.Length > 10000 || styles.Any(c => c is null) ||
            styles.Select(c => c.NoteId).Distinct().Count() != styles.Length ||
            styles.Any(c => !snapshot.Notes.Any(n => n.Id == c.NoteId)))
            throw new ArgumentException("Style changes contain duplicate or unknown note IDs.");
        foreach (var change in styles) change.Style?.Validate();
        var overrides = styles.ToDictionary(c => c.NoteId, c => c.Style);
        // 手工补录的音符由客户端生成 Id；最多 1000 个，Id 不能为空、不能和已有音符重复。
        // 补录用于引擎漏识别的音，用户点出位置后由服务端补进快照。
        var additions = request.AddedNotes ?? [];
        if (additions.Length > 1000 || additions.Any(a => a is null || a.Pitch is null || a.Bounds is null) ||
            additions.Select(a => a.Id).Distinct().Count() != additions.Length ||
            additions.Any(a => a.Id == Guid.Empty || snapshot.Notes.Any(n => n.Id == a.Id)))
            throw new ArgumentException("Added notes contain duplicate or existing IDs.");
        // 每条补录都要有合法的谱表/声部编号，以及落在页面内的正矩形。
        foreach (var addition in additions)
        {
            if (addition.Measure < 1 || addition.Part < 1 || addition.Staff < 1 || addition.Voice < 0 || addition.System < 1)
                throw new ArgumentException("Added notes require positive measure, part, staff and system, and a non-negative voice.");
            var bounds = addition.Bounds;
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < 0 || bounds.Y < 0 ||
                bounds.X + bounds.Width > snapshot.Width || bounds.Y + bounds.Height > snapshot.Height)
                throw new ArgumentException("Added note bounds must be a positive rectangle inside the page.");
            addition.Placement?.Validate(snapshot.Width, snapshot.Height);
            addition.Style?.Validate();
        }
        // 只允许删除手工补录的音符：引擎识别出的音只能改音高或隐藏，不能直接删。
        var removals = request.RemovedNoteIds ?? [];
        if (removals.Length > 1000 || removals.Distinct().Count() != removals.Length ||
            removals.Any(noteId => snapshot.Notes.All(n => n.Id != noteId)))
            throw new ArgumentException("Removed note IDs contain duplicates or unknown notes.");
        if (removals.Any(noteId => snapshot.Notes.Any(n => n.Id == noteId && !n.Manual)))
            throw new ArgumentException("Only manually added notes can be removed.");
        // 同一次保存里不能又加又删同一个音符，避免结果依赖执行顺序。
        if (additions.Any(a => removals.Contains(a.Id)))
            throw new ArgumentException("A note cannot be added and removed in the same revision.");
        // 补录音符的编号从现有最小 InterId 继续往下减，负数保证永远不和引擎编号冲突。
        // Manual notes get negative InterIds so they can never collide with engine output in labels.json.
        var nextInterId = snapshot.Notes.Length == 0 ? 0 : Math.Min(0, snapshot.Notes.Min(n => n.InterId));
        // 补录音符的简谱数字按请求里的换算设置算好，再存进快照。
        var addedNotes = new List<Note>(additions.Length);
        foreach (var addition in additions)
        {
            if (nextInterId == int.MinValue) throw new ArgumentException("No internal note IDs remain.");
            nextInterId--;
            addedNotes.Add(new Note(addition.Id, nextInterId, addition.Measure, addition.Part, addition.Staff, addition.Voice,
                addition.Pitch, addition.Bounds, Numbering.Convert(addition.Pitch, addition.Measure, request.Numbering),
                addition.Placement, addition.Style, addition.System, true));
        }
        // 先按删除集合过滤旧音符，再追加补录音符，顺序就是新快照里的音符顺序。
        var removed = removals.ToHashSet();
        // 逐音应用修改：有纠正用新音高重算简谱，有位置/样式用新值，否则保持原样。
        var notes = snapshot.Notes.Where(n => !removed.Contains(n.Id)).Select(n => { var pitch = changes.GetValueOrDefault(n.Id, n.Pitch); return n with { Pitch = pitch, Label = Numbering.Convert(pitch, n.Measure, request.Numbering), Placement = positions.TryGetValue(n.Id, out var position) ? position : n.Placement, Style = overrides.TryGetValue(n.Id, out var noteStyle) ? noteStyle : n.Style }; }).Concat(addedNotes).ToArray();
        // 整份谱的样式：请求没给就沿用上一版，再没有就用默认黑色。
        var style = request.AnnotationStyle ?? snapshot.AnnotationStyle ?? AnnotationStyle.Default;
        style.Validate();
        var updated = snapshot with { Numbering = request.Numbering, Notes = notes, AnnotationStyle = style };
        // 版本号加一后保存新修订；数据库行版本会拦住同时写入，冲突转成 409。
        j.CurrentRevision++; j.UpdatedAt = DateTimeOffset.UtcNow;
        var r = new ScoreRevision { JobId = id, Version = j.CurrentRevision, Snapshot = ScoreJson.Write(updated) }; db.Add(r);
        try { await db.SaveAsync(ct); } catch (RequestFailure e) when (e.Status == 409) { throw new RequestFailure(409, "revision-conflict", "Another revision was saved. Reload before retrying."); }
        return new(r.Version, r.State, updated, null);
    }
    // 取消识别：排队中的直接标取消，正在跑的请求后台尽快停下。
    public async Task<JobView> CancelAsync(Guid id, string token, CancellationToken ct)
    {
        var j = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        if (j.PageCount > 1)
        {
            // 多页文档先汇总状态；已经结束的任务没有可取消的动作，返回 409。
            var view = await WithPages(j, ct);
            if (view.State is "Ready" or "Failed" or "Partial" or "Cancelled") throw new RequestFailure(409, "terminal-job", "No active recognition remains.");
            // 还没开始的页直接取消，正在跑的页只标记取消中，等后台收尾。
            foreach (var page in await db.FindPagesAsync(id, ct))
                if (page.State is "Queued" or "Processing" or "Cancelling") { page.CancelRequested = true; page.State = page.State == "Queued" ? "Cancelled" : "Cancelling"; page.UpdatedAt = DateTimeOffset.UtcNow; }
            j.CancelRequested = true; j.State = "Cancelling"; j.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveAsync(ct); return await WithPages(j, ct);
        }
        // 单页任务同理：排队中的直接变已取消，处理中的转取消中。
        if (j.State is "Ready" or "Failed" or "Cancelled") throw new RequestFailure(409, "terminal-job", "Only queued or processing recognition jobs can be cancelled.");
        j.CancelRequested = true; j.State = j.State is "Processing" or "Cancelling" ? "Cancelling" : "Cancelled"; j.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    // 重试：把失败或已取消的任务重新放回队列；已经就绪的单页还可以"重新识别"。
    public async Task<JobView> RetryAsync(Guid id, string token, CancellationToken ct, RetryRequest? request = null)
    {
        var j = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        // 带选区时只重识别单页/子页的 Ready 修订；区域外音符由 Worker 合并时保留。
        if (request?.Region is { } region)
        {
            if (j.PageCount > 1) throw new RequestFailure(400, "select-page", "Select an individual page before recognizing a region.");
            if (j.State != "Ready" || j.CurrentRevision <= 0)
                throw new RequestFailure(409, "not-retryable", "Region recognition requires a ready page revision.");
            var current = await db.FindRevisionAsync(id, j.CurrentRevision, ct) ?? throw new RequestFailure(404, "revision-not-found", "Revision not found.");
            if (current.State is "Queued" or "Processing")
                throw new RequestFailure(409, "layout-in-progress", "Wait for the current revision layout to finish before recognizing this area again.");
            var page = ScoreJson.Read<ScoreSnapshot>(current.Snapshot);
            var rectangle = new Rect(region.X, region.Y, region.Width, region.Height);
            try { RegionMerge.ValidateRegion(page, rectangle); }
            catch (ArgumentException error) { throw new RequestFailure(400, "invalid-region", error.Message); }
            // 子页重识别也要避开父任务的整份导出，否则导出可能读到一半的页。
            if (j.ParentId is { } parentId)
            {
                var parent = await db.FindJobAsync(parentId, ct);
                var document = parent is { CurrentRevision: > 0 } ? await db.FindRevisionAsync(parentId, parent.CurrentRevision, ct) : null;
                if (document is { State: "Queued" or "Processing" })
                    throw new RequestFailure(409, "export-in-progress", "Wait for the full PDF export to finish before recognizing this area again.");
            }
            // 记录选区和基准修订；Worker 用基准快照合并，区域外的修改不会丢。
            j.RegionX = region.X; j.RegionY = region.Y; j.RegionWidth = region.Width; j.RegionHeight = region.Height;
            j.RegionBaseRevision = j.CurrentRevision;
            j.State = "Queued"; j.CancelRequested = false; j.BackgroundId = null; j.ErrorCode = null; j.ErrorMessage = null; j.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveAsync(ct); return View(j);
        }
        if (j.PageCount > 1)
        {
            // 多页文档只重排失败/取消的页，已完成的页保持不动，避免白白重跑。
            var pages = await db.FindPagesAsync(id, ct);
            // 没有可重试的页就返回 409，不让用户点了按钮却什么都没发生。
            var retryable = pages.Where(p => p.State is "Failed" or "Cancelled").ToArray();
            if (retryable.Length == 0) throw new RequestFailure(409, "not-retryable", "No failed or cancelled page can be retried.");
            foreach (var page in retryable) { page.State = "Queued"; page.CancelRequested = false; page.BackgroundId = null; page.ErrorCode = null; page.ErrorMessage = null; page.UpdatedAt = DateTimeOffset.UtcNow; }
            j.State = "Processing"; j.CancelRequested = false; j.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveAsync(ct); return await WithPages(j, ct);
        }
        // Ready 表示用户想换参数再识别一次，要先确认不会覆盖正在进行的渲染或导出。
        var reRecognize = j.State == "Ready";
        if (j.State is not ("Failed" or "Cancelled" or "Ready")) throw new RequestFailure(409, "not-retryable", "Only failed, cancelled or ready pages can be recognized again.");
        if (reRecognize && j.CurrentRevision > 0)
        {
            // 当前修订若还在后台渲染，先等它结束，否则重新识别会让渲染结果过期。
            var current = await db.FindRevisionAsync(id, j.CurrentRevision, ct);
            if (current is { State: "Queued" or "Processing" })
                throw new RequestFailure(409, "layout-in-progress", "Wait for the current revision layout to finish before recognizing this page again.");
            // 单页若属于整份文档，还要确认整份导出没在进行，避免导出读到一半的页。
            if (j.ParentId is { } parentId)
            {
                var parent = await db.FindJobAsync(parentId, ct);
                var document = parent is { CurrentRevision: > 0 } ? await db.FindRevisionAsync(parentId, parent.CurrentRevision, ct) : null;
                if (document is { State: "Queued" or "Processing" })
                    throw new RequestFailure(409, "export-in-progress", "Wait for the full PDF export to finish before recognizing this page again.");
            }
        }
        // 普通重试/重新识别必须清掉上一次的选区，避免 Worker 误走区域合并。
        j.RegionX = null; j.RegionY = null; j.RegionWidth = null; j.RegionHeight = null; j.RegionBaseRevision = null;
        // 重置成干净的排队状态：清掉后台号、错误码和取消标记。
        j.State = "Queued"; j.CancelRequested = false; j.BackgroundId = null; j.ErrorCode = null; j.ErrorMessage = null; j.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveAsync(ct); return View(j);
    }
    // 下载入口：按名字返回文件位置和类型，API 层再把它读成 HTTP 响应。
    // 允许的名字是白名单：source、score.mxl、annotated.pdf、annotations.json、metrics.json。
    public async Task<StoredFile> ArtifactAsync(Guid id, int? version, string name, string token, CancellationToken ct)
    {
        var j = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        // 原稿永远可下：路径由存储层拼出，按扩展名给 MIME 类型。
        if (name == "source") return new(Path.Combine(files.JobDirectory(id), "source" + j.Extension), j.Extension == ".pdf" ? "application/pdf" : j.Extension == ".png" ? "image/png" : "image/jpeg", "source" + j.Extension);
        // MusicXML 是引擎原始产物，只有识别成功且是单页时才有。
        if (name == "score.mxl")
        {
            if (j.PageCount > 1) throw new RequestFailure(409, "page-required", "Select an individual page for MusicXML preview.");
            var path = Path.Combine(files.JobDirectory(id), "raw", "score.mxl");
            if (j.State != "Ready" || !File.Exists(path)) throw new RequestFailure(409, "recognition-not-ready", "MusicXML is available after successful recognition.");
            return new(path, "application/vnd.recordare.musicxml", $"score-{id}.mxl");
        }
        // 其余产物跟着修订走：可指定版本，且该版本必须渲染完成。
        if (name is not ("annotated.pdf" or "annotations.json" or "metrics.json")) throw new RequestFailure(404, "not-found", "Artifact not found.");
        var r = await db.FindRevisionAsync(id, version ?? j.CurrentRevision, ct) ?? throw new RequestFailure(404, "revision-not-found", "Revision not found.");
        if (r.State != "Ready") throw new RequestFailure(409, "artifact-not-ready", "The requested revision artifact is not ready.");
        return new(Path.Combine(files.JobDirectory(id), "revisions", r.Version.ToString(), name), name.EndsWith("pdf") ? "application/pdf" : "application/json", $"score-{id}-r{r.Version}-{name}");
    }
    // 整份导出：要求每页都识别完成且当前修订已导出，然后创建一份"页清单"修订。
    // 合并脚本按清单顺序把各页标注 PDF 拼回整份；清单也存成修订，方便追溯和查进度。
    public async Task<DocumentExportView> ExportAsync(Guid id, string token, CancellationToken ct)
    {
        var job = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        // 单页没有"整份"概念，让前端走单页修订下载接口。
        if (job.PageCount <= 1) throw new RequestFailure(400, "single-page-export", "Use the page revision download for a single page.");
        var pages = await db.PageViewsAsync(id, ct);
        // 任何一页没就绪就拒绝：缺页的合并结果没有意义。
        if (pages.Length != job.PageCount || pages.Any(p => p.State != "Ready" || p.ExportState != "Ready"))
            throw new RequestFailure(409, "pages-not-ready", "Every page must be recognized and its current revision exported before exporting the document.");
        // 清单只记页码、页任务 ID 和修订号，合并时靠它们去各页目录取文件。
        var snapshot = new DocumentExportSnapshot(pages.Select(p => new DocumentPageRef(p.Number, p.Id, p.CurrentRevision)).ToArray());
        var json = ScoreJson.Write(snapshot);
        var previous = await db.FindRevisionAsync(id, job.CurrentRevision, ct);
        // 清单和上一版完全相同且没失败：直接复用上一版，避免重复点导出生成一堆相同修订。
        if (previous is not null && previous.Snapshot == json && previous.State != "Failed")
            return new(previous.Version, previous.State, snapshot.Pages, previous.ErrorCode);
        // 新版本入库后由后台调度器接手合并；这里先返回排队中的状态。
        job.CurrentRevision++; job.UpdatedAt = DateTimeOffset.UtcNow;
        var revision = new ScoreRevision { JobId = id, Version = job.CurrentRevision, Snapshot = json };
        db.Add(revision); await db.SaveAsync(ct);
        return new(revision.Version, revision.State, snapshot.Pages, null);
    }
    // 按版本查询整份导出的进度和页清单。
    public async Task<DocumentExportView> ExportStatusAsync(Guid id, int version, string token, CancellationToken ct)
    {
        var job = await ScoreAccess.AuthorizeAsync(db, id, token, ct);
        if (job.PageCount <= 1) throw new RequestFailure(400, "single-page-export", "This score is a single page.");
        var revision = await db.FindRevisionAsync(id, version, ct) ?? throw new RequestFailure(404, "not-found", "Export not found.");
        return new(revision.Version, revision.State, ScoreJson.Read<DocumentExportSnapshot>(revision.Snapshot).Pages, revision.ErrorCode);
    }
}
