using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;
using MusicRecognition.Infrastructure.Observability;
using MusicRecognition.Infrastructure.Recognition;

namespace MusicRecognition.Infrastructure;

// 本文件是 Worker 里的“车间主任”：Hangfire 队列把排队的识谱/导出任务重放到这里执行。
// Recognize 负责准备页面、逐页识别、把结果存成新修订；Render 负责把标注渲染成 PDF。
// 它不自己做识谱计算和画图，那些交给识别引擎（engine）和 Python 脚本，这里只做编排。
// 用生活类比：领活 → 分工 → 盯进度 → 记账、出错 → 收尾，每一步都写进日志。
// Hangfire stores this type name and method signatures in queued jobs.
// 依赖都由依赖注入（DI）提供：数据库、识别引擎、文件存储、Hangfire 存储、日志、查询仓储。
// Hangfire 是后台任务队列库：把方法调用和参数存进数据库，再由 Worker 进程取出来执行。
public sealed class ScoreProcessor(ScoreDb db, IRecognitionEngine engine, IScoreStorage files, JobStorage storage, IServiceScopeFactory scopes, ILogger<ScoreProcessor> logger, IScoreRepository repository)
{
    // 任务放进 recognition 队列；失败自动重试 2 次，间隔 5 秒、15 秒，给临时故障留恢复时间。
    [Queue("recognition"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    // 识谱入口：id 是任务编号；shutdown 是服务停机时触发的取消令牌（token）。
    public async Task Recognize(Guid id, CancellationToken shutdown)
    {
        // 同一任务用数据库分布式锁串行化：重试或重复入队时，10 分钟内只允许一个在执行。
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        // 从数据库读任务的当前状态；取消标记和已尝试次数都以库里的为准。
        var job = await db.Jobs.SingleAsync(j => j.Id == id, shutdown);
        // 解析日志上下文（乐谱名等）；任务不存在时也给出可读默认值，日志不会中断。
        var context = await ScoreDiagnostics.ResolveAsync(repository, job, shutdown);
        // 开启日志作用域（scope）：这一行之后的所有日志自动带上乐谱名。
        using var logScope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        // 用户已请求取消：不再识谱，只把状态收尾成“已取消”就返回。
        if (job.CancelRequested)
        {
            // 只有“正在取消”才推进到终态，避免把别的状态误改。
            if (job.State == "Cancelling") { job.State = "Cancelled"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown); }
            logger.LogInformation("🛑 {ScoreName}：已收到取消请求，跳过识谱（任务 {ScoreId}）", name, id);
            return;
        }
        // 已经是终态（完成/失败/已取消）就跳过：迟到的重试不能把好结果改坏。
        if (job.State is "Ready" or "Failed" or "Cancelled") { logger.LogInformation("✅ {ScoreName}：识谱已处于终态 {State}，跳过过期的重复执行（任务 {ScoreId}）", name, job.State, id); return; }
        // 标记“处理中”、累计尝试次数，并清空上次的错误码与错误信息。
        job.State = "Processing"; job.Attempts++; job.ErrorCode = null; job.ErrorMessage = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown);
        // 从开始到结束计时，日志统一用毫秒，方便看每一步慢在哪。
        var stopwatch = Stopwatch.StartNew();
        // 开工日志：记录格式、预处理开关（None 显示“关闭”）和第几次尝试。
        logger.LogInformation("⚙️ {ScoreName}：开始识谱，第 {Attempt} 次尝试，格式 {Extension}，预处理 {Preprocessing}（任务 {ScoreId}）",
            name, job.Attempts, job.Extension, job.Preprocessing == PreprocessingMode.None ? "关闭" : "自适应增强", id);
        // 把服务停机信号与“用户取消监视器”合成一个令牌，任一取消都能中断后续步骤。
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        // 启动后台取消监视器（见下面的 WatchCancellation），用户点取消时最多 0.5 秒生效。
        var watcher = WatchCancellation(id, cancelled);
        try
        {
            // 只有“原始任务”且还没拆页时才准备页面；逐页子任务和已准备过的直接去识别。
            if (job.ParentId is null && !job.PagesPrepared)
            {
                logger.LogInformation("📄 {ScoreName}：开始准备 PDF 页面（任务 {ScoreId}）", name, id);
                // 调引擎把 PDF/图片拆成逐页文件，返回总页数。
                var document = await engine.PrepareDocumentAsync(id, job.Extension, cancelled.Token);
                logger.LogInformation("📑 {ScoreName}：PDF 页面准备完成，共 {PageCount} 页，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, document.PageCount, stopwatch.ElapsedMilliseconds, id);
                // 记录页数和“已拆页”标记；重试时据此跳过重复拆页。
                job.PageCount = document.PageCount; job.PagesPrepared = true;
                // 多页文档拆成“母任务 + 每页一个子任务”，每页独立识别、互不拖累；单页直接往下识别。
                if (document.PageCount > 1)
                {
                    // 一页一个子任务：页码从 1 开始，原稿用该页单独的 PDF。
                    for (var number = 1; number <= document.PageCount; number++)
                    {
                        // 子任务沿用母任务的令牌哈希、文件指纹、预处理和引擎，只填自己的 ID 和页码。
                        var child = new ScoreJob
                        {
                            Id = Guid.NewGuid(),
                            ParentId = id,
                            PageNumber = number,
                            PagesPrepared = true,
                            TokenHash = job.TokenHash,
                            // 新请求指纹：避免和母任务或其它页在同一张表里被判为重复请求。
                            RequestHash = Guid.NewGuid().ToString("N"),
                            FileHash = job.FileHash,
                            FileName = job.FileName,
                            Extension = ".pdf",
                            Preprocessing = job.Preprocessing,
                            Engine = job.Engine
                        };
                        // 每个子任务有自己的任务目录，里面放 source.pdf 和识别产物。
                        var directory = files.JobDirectory(child.Id); Directory.CreateDirectory(directory);
                        // 把母任务拆出的第 N 页复制成子任务的原稿（文件名叫 source.pdf）。
                        File.Copy(Path.Combine(files.JobDirectory(id), "document", $"{number}.pdf"), Path.Combine(directory, "source.pdf"));
                        // 先把子任务挂进数据库上下文，循环结束后统一保存。
                        db.Jobs.Add(child);
                    }
                    // 一次性保存全部子任务；母任务到此结束，真正的识谱交给各页自己的队列消息。
                    await db.SaveChangesAsync(cancelled.Token);
                    logger.LogInformation("📑 {ScoreName}：已创建 {PageCount} 个逐页任务，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, document.PageCount, stopwatch.ElapsedMilliseconds, id);
                    return;
                }
                // 单页也要把“已拆页”落库，重试时才不会再拆一次。
                await db.SaveChangesAsync(cancelled.Token);
            }
            // 母任务不自己识谱：多页时每页的子任务会各自识别。
            if (job.PageCount > 1) return; // Idempotent parent preparation; page jobs own recognition.
            logger.LogInformation("🎼 {ScoreName}：使用识谱引擎 {RecognitionEngine}（任务 {ScoreId}）", name, job.Engine, id);
            // 快照在区域重识别时会用旧修订合并，普通识别则直接用引擎结果。
            ScoreSnapshot snapshot;
            if (job.RegionBaseRevision is { } regionBase)
            {
                // 选区的基准修订必须还是当前修订，否则用户已经改过页面，不能再用旧区域覆盖。
                if (regionBase != job.CurrentRevision)
                    throw new ToolFailure("region-base-changed", "选区重识别开始前页面已更新，请重新选择区域。");
                var baseRevision = await db.Revisions.FindAsync([id, regionBase], cancelled.Token)
                    ?? throw new InvalidOperationException("Missing region base revision.");
                var baseSnapshot = ScoreJson.Read<ScoreSnapshot>(baseRevision.Snapshot);
                var region = new Rect(job.RegionX ?? 0, job.RegionY ?? 0, job.RegionWidth ?? 0, job.RegionHeight ?? 0);
                // 区域识别把整页 OCR 跑到临时 raw，canonical raw 保持不动，旧修订仍能正常渲染。
                var regionDirectory = Path.Combine(files.JobDirectory(id), "region-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(regionDirectory);
                try
                {
                    var recognized = await engine.RecognizeAsync(id, job.Extension, cancelled.Token, job.Preprocessing, job.Engine, Path.Combine(regionDirectory, "raw"));
                    try { snapshot = RegionMerge.Merge(baseSnapshot, recognized, region); }
                    catch (InvalidOperationException error) { throw new ToolFailure("region-empty", "选区内没有识别到音符，页面保持原样。", error.Message); }
                }
                finally
                {
                    // 临时目录只服务这一次合并；删不掉也不影响结果。
                    try { Directory.Delete(regionDirectory, recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                logger.LogInformation("🎯 {ScoreName}：区域重识别完成，选区 ({X},{Y},{Width},{Height})，音符 {NoteCount} 个（任务 {ScoreId}）",
                    name, region.X, region.Y, region.Width, region.Height, snapshot.Notes.Length, id);
            }
            else
            {
                // 调识别引擎做真正的识谱，得到页面快照（snapshot：音符、页宽高等）。
                snapshot = await engine.RecognizeAsync(id, job.Extension, cancelled.Token, job.Preprocessing, job.Engine);
            }
            // 识谱可能很久，重新从数据库读任务行，看期间用户有没有点取消。
            await db.Entry(job).ReloadAsync(cancelled.Token);
            // 期间用户取消：丢弃这次结果，不写入修订。
            if (job.CancelRequested) return;
            // 识别期间如果当前修订已经变了，区域结果就不再可靠，保留旧版本并报可恢复错误。
            if (job.RegionBaseRevision is not null && job.RegionBaseRevision != job.CurrentRevision)
                throw new ToolFailure("region-base-changed", "选区重识别完成前页面已更新，请重新选择区域。");
            // Re-recognition appends a new immutable revision; older revisions stay downloadable.
            // 每次重新识谱都追加一个新修订，旧修订保留，用户仍可下载历史版本。
            var nextRevision = job.CurrentRevision + 1;
            // 任务状态改为“完成”，CurrentRevision 指向最新修订号；选区字段用完后清空。
            job.CurrentRevision = nextRevision; job.State = "Ready"; job.UpdatedAt = DateTimeOffset.UtcNow;
            job.RegionX = null; job.RegionY = null; job.RegionWidth = null; job.RegionHeight = null; job.RegionBaseRevision = null;
            // 修订只增不改：把快照序列化成 JSON 存进修订表。
            db.Revisions.Add(new ScoreRevision { JobId = id, Version = nextRevision, Snapshot = ScoreJson.Write(snapshot) });
            // 同一次 SaveChanges 提交状态和修订，避免只成功一半。
            await db.SaveChangesAsync(cancelled.Token);
            logger.LogInformation("✅ {ScoreName}：识谱完成，修订 {Revision}，音符 {NoteCount} 个，警告 {WarningCount} 条，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）",
                name, job.CurrentRevision, snapshot.Notes.Length, snapshot.Warnings.Length, stopwatch.ElapsedMilliseconds, id);
        }
        // 取消或中断（用户取消、服务停机、超时）：不能让任务停在“处理中”。
        catch (OperationCanceledException)
        {
            // 清掉内存里的旧跟踪状态，重新读最新任务行，避免把脏数据写回库里。
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            // 用户主动取消就落“已取消”；否则回到“排队中”，等待 Hangfire 重试。
            job.State = job.CancelRequested ? "Cancelled" : "Queued"; job.BackgroundId = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            logger.LogWarning("🛑 {ScoreName}：识谱已取消或中断，状态 {State}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, job.State == "Cancelled" ? "已取消" : "等待重试", stopwatch.ElapsedMilliseconds, id);
            // 服务停机导致的取消继续往外抛（交给队列重新入队）；用户取消则正常吞掉。
            if (shutdown.IsCancellationRequested) throw;
        }
        // 其它失败：把任务标成 Failed，记录错误码和给用户看的中文原因，然后打错误日志。
        catch (Exception e)
        {
            // 同样先清跟踪、再读最新任务，防止把失败写进过期的对象。
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            // 用户已取消时不再标失败（取消优先）；ToolFailure 是带错误码、可展示给用户的异常。
            if (!job.CancelRequested) { job.State = "Failed"; job.ErrorCode = e is ToolFailure t ? t.Code : "recognition-failed"; job.ErrorMessage = e is ToolFailure ? e.Message : "识谱失败，请使用任务 ID 查看服务器诊断日志。"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            // 错误日志带上母任务 ID、页码和诊断原因，便于按任务 ID 在日志里定位。
            logger.LogError(e, "💥 {ScoreName}：识谱失败，错误码 {ErrorCode}，原因 {FailureReason}，第 {Attempt} 次尝试，用时 {ElapsedMs} 毫秒（任务 {ScoreId}，原文档 {DocumentId}，页码 {PageNumber}）",
                name, job.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, job.Attempts, stopwatch.ElapsedMilliseconds, id, job.ParentId ?? id, job.PageNumber);
            // Deterministic failures are user-visible and retried explicitly, not silently looped.
        }
        // 收尾：停掉取消监视器并等它退出，避免后台循环泄漏。
        finally { cancelled.Cancel(); try { await watcher; } catch (OperationCanceledException) { } }
    }
    // 取消监视器：识谱期间每 0.5 秒查一次数据库，发现用户取消就触发取消令牌。
    private async Task WatchCancellation(Guid id, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(500, stop.Token);
            // 单独开依赖注入作用域，拿独立的 ScoreDb，避免和在用 DbContext 的跟踪状态互相干扰。
            await using var scope = scopes.CreateAsyncScope(); var other = scope.ServiceProvider.GetRequiredService<ScoreDb>();
            // 只判断“该任务是否请求取消”；查到就取消令牌并退出循环。
            if (await other.Jobs.AnyAsync(j => j.Id == id && j.CancelRequested, stop.Token)) { stop.Cancel(); return; }
        }
    }
    // 导出任务单独放 render 队列；失败同样自动重试 2 次（5 秒、15 秒）。
    [Queue("render"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    // 导出入口：把某个修订（version）的标注快照交给脚本，渲染成带简谱的 PDF。
    public async Task Render(Guid id, int version, CancellationToken shutdown)
    {
        // 同一乐谱的导出也加分布式锁，防止两次导出同时改同一批文件。
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        // 取要导出的修订；任务 ID + 版本号是联合主键。
        var r = await db.Revisions.FindAsync([id, version], shutdown);
        // 任务只读（AsNoTracking 不跟踪修改），因为这里只改修订的状态。
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, shutdown);
        var context = await ScoreDiagnostics.ResolveAsync(repository, job, shutdown);
        using var logScope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        // 修订不存在，或已导出/已失败：跳过，重试不应重复生成文件。
        if (r is null || r.State is "Ready" or "Failed") { logger.LogInformation("✅ {ScoreName}：修订 {Revision} 无需重复导出（任务 {ScoreId}）", name, version, id); return; }
        // 标记导出进行中并保存，让并发的第二次执行能看到当前状态而跳过。
        r.State = "Processing"; await db.SaveChangesAsync(shutdown);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("🎨 {ScoreName}：开始标注导出，修订 {Revision}，共 {PageCount} 页（任务 {ScoreId}）", name, version, job.PageCount, id);
        try
        {
            // 多页文档：把各页已标注的结果合并成一份 PDF（MergeDocumentAsync）。
            if (job.PageCount > 1) await engine.MergeDocumentAsync(id, version, ScoreJson.Read<DocumentExportSnapshot>(r.Snapshot), shutdown);
            // 单页：把标注画到原谱留白处；快照从修订的 JSON 反序列化得到。
            else await engine.RenderAsync(id, version, job.Extension, ScoreJson.Read<ScoreSnapshot>(r.Snapshot), shutdown);
            // 导出成功：修订标记 Ready 并清掉旧错误码。
            r.State = "Ready"; r.ErrorCode = null; await db.SaveChangesAsync(shutdown);
            logger.LogInformation("✅ {ScoreName}：标注导出完成，修订 {Revision}，共 {PageCount} 页，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, job.PageCount, stopwatch.ElapsedMilliseconds, id);
        }
        // 服务停机导致的中断：退回“排队中”重新入队，重启后继续导出。
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { r.State = "Queued"; r.BackgroundId = null; await db.SaveChangesAsync(); logger.LogWarning("⏸️ {ScoreName}：服务关闭，导出已中断，修订 {Revision}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, stopwatch.ElapsedMilliseconds, id); throw; }
        // 导出失败：修订标 Failed，记录错误码和诊断原因。
        catch (Exception e) { r.State = "Failed"; r.ErrorCode = e is ToolFailure t ? t.Code : "render-failed"; await db.SaveChangesAsync(); logger.LogError(e, "💥 {ScoreName}：导出失败，修订 {Revision}，错误码 {ErrorCode}，原因 {FailureReason}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, r.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, stopwatch.ElapsedMilliseconds, id); }
    }
}
