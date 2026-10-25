using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Domain;
using System.Diagnostics;

namespace MusicRecognition.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddRecognition(this IServiceCollection services, IConfiguration config, bool worker)
    {
        var options = config.GetSection("Recognition").Get<RecognitionOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.DataRoot) || string.IsNullOrWhiteSpace(options.RepositoryRoot) || options.TimeoutSeconds is < 10 or > 1800 || options.WorkerCount is < 1 or > 4 || options.MinimumFreeSpaceMiB is < 0 or > 1048576)
            throw new InvalidOperationException("Configure Recognition RepositoryRoot, DataRoot and TimeoutSeconds (10..1800).");
        services.AddSingleton(options); services.AddSingleton<IScoreStorage, FileStorage>();
        var connection = config.GetConnectionString("recognitiondb") ?? throw new InvalidOperationException("Missing recognitiondb connection string.");
        services.AddDbContext<ScoreDb>(o => o.UseNpgsql(connection));
        services.AddHealthChecks().AddCheck<DatabaseHealth>("recognitiondb").AddCheck<DataStorageHealth>("score-storage");
        services.AddScoped<IScoreRepository, ScoreRepository>(); services.AddScoped<IScoreService, ScoreService>(); services.AddScoped<IRecognitionEngine, ToolEngine>(); services.AddScoped<ScoreProcessor>();
        services.AddScoped<IReflowEngine, ToolEngine>(); services.AddScoped<ReflowService>();
        services.AddScoped<ToolEngine>(); services.AddScoped<IFragmentEngine, FragmentEngine>();
        services.AddScoped<IFragmentStorage, FragmentStorage>(); services.AddScoped<FragmentService>();
        services.AddHangfire(h => h.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connection), new PostgreSqlStorageOptions { InvisibilityTimeout = TimeSpan.FromMinutes(1), QueuePollInterval = TimeSpan.FromSeconds(2) }));
        if (worker)
        {
            services.AddSingleton<RecognitionRuntime>();
            services.AddHealthChecks().AddCheck<RecognitionRuntime>("recognition-runtime");
            services.AddHangfireServer(o => { o.WorkerCount = options.WorkerCount; o.Queues = ["recognition", "render"]; o.CancellationCheckInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(10); });
            services.AddHostedService<DispatchService>();
        }
        return services;
    }
    public static async Task MigrateRecognitionAsync(this IServiceProvider provider, CancellationToken ct = default)
    { await using var scope = provider.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ScoreDb>().Database.MigrateAsync(ct); }
}

// The domain records form a durable dispatch outbox. Publishing a Hangfire job twice is safe:
// each handler takes the same per-score lock, then checks the persisted terminal state.
public sealed class DispatchService(IServiceScopeFactory scopes, IBackgroundJobClient jobs, JobStorage storage, ILogger<DispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 后台调度已启动：每 2 秒扫描一次待处理任务（锁 music-dispatch）");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = storage.GetConnection();
                using var gate = connection.AcquireDistributedLock("music-dispatch", TimeSpan.FromSeconds(5));
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ScoreDb>();
                var repository = scope.ServiceProvider.GetRequiredService<IScoreRepository>();
                foreach (var job in await db.Jobs.AsNoTracking().Where(j => j.State == "Queued" && j.BackgroundId == null && !j.CancelRequested).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).Take(100).ToListAsync(stoppingToken))
                {
                    var id = job.Id;
                    var context = await ScoreDiagnostics.ResolveAsync(repository, job, stoppingToken);
                    using var logScope = logger.BeginScope(context.Scope());
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Recognize(id, CancellationToken.None));
                    // The worker can start before this update. Do not overwrite its state/xmin.
                    await db.Jobs.Where(j => j.Id == id && j.State == "Queued" && j.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("📥 {ScoreName}：已加入识谱队列（任务 {ScoreId}，后台任务 {BackgroundJobId}）", context.ScoreName, id, backgroundId);
                }
                foreach (var revision in await db.Revisions.AsNoTracking().Where(r => r.State == "Queued" && r.BackgroundId == null).OrderBy(r => r.JobId).ThenBy(r => r.Version).Take(100).ToListAsync(stoppingToken))
                {
                    var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == revision.JobId, stoppingToken);
                    var context = await ScoreDiagnostics.ResolveAsync(repository, job, stoppingToken);
                    using var logScope = logger.BeginScope(context.Scope());
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Render(revision.JobId, revision.Version, CancellationToken.None));
                    await db.Revisions.Where(r => r.JobId == revision.JobId && r.Version == revision.Version && r.State == "Queued" && r.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("🎨 {ScoreName}：修订 {Revision} 已加入导出队列（任务 {ScoreId}，后台任务 {BackgroundJobId}）", context.ScoreName, revision.Version, revision.JobId, backgroundId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "❌ 后台任务调度失败，稍后重试"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        logger.LogInformation("🛑 后台任务调度已停止");
    }
}

public sealed class ScoreProcessor(ScoreDb db, IRecognitionEngine engine, IScoreStorage files, JobStorage storage, IServiceScopeFactory scopes, ILogger<ScoreProcessor> logger, IScoreRepository repository, IFragmentEngine fragments, IFragmentStorage fragmentStorage)
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
            if (job.Extension == FragmentService.Extension)
            {
                var input = await fragmentStorage.InputAsync(id,cancelled.Token);
                await fragments.ComposeAsync(id,1,input.Plan,cancelled.Token);
                await db.Entry(job).ReloadAsync(cancelled.Token);
                if (job.CancelRequested) return;
                job.CurrentRevision=1; job.State="Ready"; job.UpdatedAt=DateTimeOffset.UtcNow;
                db.Revisions.Add(new ScoreRevision { JobId=id, Version=1, State="Ready", Snapshot=ScoreJson.Write(input.Plan) });
                await db.SaveChangesAsync(cancelled.Token);
                logger.LogInformation("✅ {ScoreName}：片段拼谱与 PDF 完成，共 {FragmentCount} 张来源图（任务 {ScoreId}）",name,input.Sources.Length,id);
                return;
            }
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
                            Preprocessing = job.Preprocessing
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
            var snapshot = await engine.RecognizeAsync(id, job.Extension, cancelled.Token, job.Preprocessing);
            await db.Entry(job).ReloadAsync(cancelled.Token);
            if (job.CancelRequested) return;
            job.CurrentRevision = 1; job.State = "Ready"; job.UpdatedAt = DateTimeOffset.UtcNow;
            db.Revisions.Add(new ScoreRevision { JobId = id, Version = 1, Snapshot = ScoreJson.Write(snapshot) });
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
            if (job.Extension == FragmentService.Extension) await fragments.ComposeAsync(id,version,ScoreJson.Read<FragmentPlan>(r.Snapshot),shutdown);
            else if (job.PageCount > 1) await engine.MergeDocumentAsync(id, version, ScoreJson.Read<DocumentExportSnapshot>(r.Snapshot), shutdown);
            else await engine.RenderAsync(id, version, job.Extension, ScoreJson.Read<ScoreSnapshot>(r.Snapshot), shutdown);
            r.State = "Ready"; r.ErrorCode = null; await db.SaveChangesAsync(shutdown);
            logger.LogInformation("✅ {ScoreName}：标注导出完成，修订 {Revision}，共 {PageCount} 页，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, job.PageCount, stopwatch.ElapsedMilliseconds, id);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { r.State = "Queued"; r.BackgroundId = null; await db.SaveChangesAsync(); logger.LogWarning("⏸️ {ScoreName}：服务关闭，导出已中断，修订 {Revision}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, stopwatch.ElapsedMilliseconds, id); throw; }
        catch (Exception e) { r.State = "Failed"; r.ErrorCode = e is ToolFailure t ? t.Code : "render-failed"; await db.SaveChangesAsync(); logger.LogError(e, "💥 {ScoreName}：导出失败，修订 {Revision}，错误码 {ErrorCode}，原因 {FailureReason}，用时 {ElapsedMs} 毫秒（任务 {ScoreId}）", name, version, r.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, stopwatch.ElapsedMilliseconds, id); }
    }
}
