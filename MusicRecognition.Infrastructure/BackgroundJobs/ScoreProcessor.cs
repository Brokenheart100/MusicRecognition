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
using MusicRecognition.Domain.ValueObjects;
using MusicRecognition.Infrastructure.Observability;
using MusicRecognition.Infrastructure.Recognition;

namespace MusicRecognition.Infrastructure;

// Hangfire stores this type name and method signatures in queued jobs.
public sealed class ScoreProcessor(ScoreDb db, IRecognitionEngine engine, IScoreStorage files, JobStorage storage, IServiceScopeFactory scopes, ILogger<ScoreProcessor> logger, IScoreRepository repository)
{
    [Queue("recognition"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    public async Task Recognize(Guid id, CancellationToken shutdown)
    {
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        var job = await db.Jobs.SingleAsync(j => j.Id == id, shutdown);
        var context = await ScoreDiagnostics.ResolveAsync(repository, job, shutdown);
        using var logScope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        if (job.CancelRequested)
        {
            if (job.State == "Cancelling") { job.State = "Cancelled"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown); }
            logger.LogInformation("🛑 {ScoreName}：已收到取消请求，跳过识谱（任务 {ScoreId}）", name, id);
            return;
        }
        if (job.State is "Ready" or "Failed" or "Cancelled") { logger.LogInformation("✅ {ScoreName}：识谱已处于终态 {State}，跳过过期的重复执行（任务 {ScoreId}）", name, job.State, id); return; }
        job.State = "Processing"; job.Attempts++; job.ErrorCode = null; job.ErrorMessage = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("⚙️ {ScoreName}：开始识谱，第 {Attempt} 次尝试，格式 {Extension}，预处理 {Preprocessing}（任务 {ScoreId}）",
            name, job.Attempts, job.Extension, job.Preprocessing == PreprocessingMode.None ? "关闭" : "自适应增强", id);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var watcher = WatchCancellation(id, cancelled);
        try
        {
            if (job.ParentId is null && !job.PagesPrepared)
            {
                logger.LogInformation("📄 {ScoreName}：开始准备 PDF 页面（任务 {ScoreId}）", name, id);
                var document = await engine.PrepareDocumentAsync(id, job.Extension, cancelled.Token);
                logger.LogInformation("📑 {ScoreName}：PDF 页面准备完成，共 {PageCount} 页，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, document.PageCount, stopwatch.ElapsedMilliseconds, id);
                job.PageCount = document.PageCount; job.PagesPrepared = true;
                if (document.PageCount > 1)
                {
                    for (var number = 1; number <= document.PageCount; number++)
                    {
                        var child = new ScoreJob
                        {
                            Id = Guid.NewGuid(),
                            ParentId = id,
                            PageNumber = number,
                            PagesPrepared = true,
                            TokenHash = job.TokenHash,
                            RequestHash = Guid.NewGuid().ToString("N"),
                            FileHash = job.FileHash,
                            FileName = job.FileName,
                            Extension = ".pdf",
                            Preprocessing = job.Preprocessing,
                            Engine = job.Engine
                        };
                        var directory = files.JobDirectory(child.Id); Directory.CreateDirectory(directory);
                        File.Copy(Path.Combine(files.JobDirectory(id), "document", $"{number}.pdf"), Path.Combine(directory, "source.pdf"));
                        db.Jobs.Add(child);
                    }
                    await db.SaveChangesAsync(cancelled.Token);
                    logger.LogInformation("📑 {ScoreName}：已创建 {PageCount} 个逐页任务，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, document.PageCount, stopwatch.ElapsedMilliseconds, id);
                    return;
                }
                await db.SaveChangesAsync(cancelled.Token);
            }
            if (job.PageCount > 1) return; // Idempotent parent preparation; page jobs own recognition.
            logger.LogInformation("🎼 {ScoreName}：使用识谱引擎 {RecognitionEngine}（任务 {ScoreId}）", name, job.Engine, id);
            var snapshot = await engine.RecognizeAsync(id, job.Extension, cancelled.Token, job.Preprocessing, job.Engine);
            await db.Entry(job).ReloadAsync(cancelled.Token);
            if (job.CancelRequested) return;
            // Re-recognition appends a new immutable revision; older revisions stay downloadable.
            var nextRevision = job.CurrentRevision + 1;
            job.CurrentRevision = nextRevision; job.State = "Ready"; job.UpdatedAt = DateTimeOffset.UtcNow;
            db.Revisions.Add(new ScoreRevision { JobId = id, Version = nextRevision, Snapshot = ScoreJson.Write(snapshot) });
            await db.SaveChangesAsync(cancelled.Token);
            logger.LogInformation("✅ {ScoreName}：识谱完成，修订 {Revision}，音符 {NoteCount} 个，警告 {WarningCount} 条，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）",
                name, job.CurrentRevision, snapshot.Notes.Length, snapshot.Warnings.Length, stopwatch.ElapsedMilliseconds, id);
        }
        catch (OperationCanceledException)
        {
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            job.State = job.CancelRequested ? "Cancelled" : "Queued"; job.BackgroundId = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            logger.LogWarning("🛑 {ScoreName}：识谱已取消或中断，状态 {State}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, job.State == "Cancelled" ? "已取消" : "等待重试", stopwatch.ElapsedMilliseconds, id);
            if (shutdown.IsCancellationRequested) throw;
        }
        catch (Exception e)
        {
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            if (!job.CancelRequested) { job.State = "Failed"; job.ErrorCode = e is ToolFailure t ? t.Code : "recognition-failed"; job.ErrorMessage = e is ToolFailure ? e.Message : "识谱失败，请使用任务 ID 查看服务器诊断日志。"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            logger.LogError(e, "💥 {ScoreName}：识谱失败，错误码 {ErrorCode}，原因 {FailureReason}，第 {Attempt} 次尝试，用时 {ElapsedMs} 毫秒（任务 {ScoreId}，原文档 {DocumentId}，页码 {PageNumber}）",
                name, job.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, job.Attempts, stopwatch.ElapsedMilliseconds, id, job.ParentId ?? id, job.PageNumber);
            // Deterministic failures are user-visible and retried explicitly, not silently looped.
        }
        finally { cancelled.Cancel(); try { await watcher; } catch (OperationCanceledException) { } }
    }
    private async Task WatchCancellation(Guid id, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(500, stop.Token);
            await using var scope = scopes.CreateAsyncScope(); var other = scope.ServiceProvider.GetRequiredService<ScoreDb>();
            if (await other.Jobs.AnyAsync(j => j.Id == id && j.CancelRequested, stop.Token)) { stop.Cancel(); return; }
        }
    }
    [Queue("render"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    public async Task Render(Guid id, int version, CancellationToken shutdown)
    {
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        var r = await db.Revisions.FindAsync([id, version], shutdown);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, shutdown);
        var context = await ScoreDiagnostics.ResolveAsync(repository, job, shutdown);
        using var logScope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        if (r is null || r.State is "Ready" or "Failed") { logger.LogInformation("✅ {ScoreName}：修订 {Revision} 无需重复导出（任务 {ScoreId}）", name, version, id); return; }
        r.State = "Processing"; await db.SaveChangesAsync(shutdown);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("🎨 {ScoreName}：开始标注导出，修订 {Revision}，共 {PageCount} 页（任务 {ScoreId}）", name, version, job.PageCount, id);
        try
        {
            if (job.PageCount > 1) await engine.MergeDocumentAsync(id, version, ScoreJson.Read<DocumentExportSnapshot>(r.Snapshot), shutdown);
            else await engine.RenderAsync(id, version, job.Extension, ScoreJson.Read<ScoreSnapshot>(r.Snapshot), shutdown);
            r.State = "Ready"; r.ErrorCode = null; await db.SaveChangesAsync(shutdown);
            logger.LogInformation("✅ {ScoreName}：标注导出完成，修订 {Revision}，共 {PageCount} 页，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, job.PageCount, stopwatch.ElapsedMilliseconds, id);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { r.State = "Queued"; r.BackgroundId = null; await db.SaveChangesAsync(); logger.LogWarning("⏸️ {ScoreName}：服务关闭，导出已中断，修订 {Revision}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, stopwatch.ElapsedMilliseconds, id); throw; }
        catch (Exception e) { r.State = "Failed"; r.ErrorCode = e is ToolFailure t ? t.Code : "render-failed"; await db.SaveChangesAsync(); logger.LogError(e, "💥 {ScoreName}：导出失败，修订 {Revision}，错误码 {ErrorCode}，原因 {FailureReason}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, r.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, stopwatch.ElapsedMilliseconds, id); }
    }
}
