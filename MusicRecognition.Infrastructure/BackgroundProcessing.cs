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
        if (string.IsNullOrWhiteSpace(options.DataRoot) || string.IsNullOrWhiteSpace(options.RepositoryRoot) || options.TimeoutSeconds is < 10 or > 1800)
            throw new InvalidOperationException("Configure Recognition RepositoryRoot, DataRoot and TimeoutSeconds (10..1800).");
        services.AddSingleton(options); services.AddSingleton<IScoreStorage, FileStorage>();
        var connection = config.GetConnectionString("recognitiondb") ?? throw new InvalidOperationException("Missing recognitiondb connection string.");
        services.AddDbContext<ScoreDb>(o => o.UseNpgsql(connection));
        services.AddHealthChecks().AddCheck<DatabaseHealth>("recognitiondb");
        services.AddScoped<IScoreRepository, ScoreRepository>(); services.AddScoped<IScoreService, ScoreService>(); services.AddScoped<IRecognitionEngine, ToolEngine>(); services.AddScoped<ScoreProcessor>();
        services.AddHangfire(h => h.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connection), new PostgreSqlStorageOptions { InvisibilityTimeout = TimeSpan.FromMinutes(1), QueuePollInterval = TimeSpan.FromSeconds(2) }));
        if (worker)
        {
            services.AddSingleton<RecognitionRuntime>();
            services.AddHealthChecks().AddCheck<RecognitionRuntime>("recognition-runtime");
            services.AddHangfireServer(o => { o.WorkerCount = 1; o.Queues = ["recognition", "render"]; o.CancellationCheckInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(10); });
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
        logger.LogInformation("🚀 Dispatch loop started: scanning the outbox every 2 seconds (lock music-dispatch)");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = storage.GetConnection();
                using var gate = connection.AcquireDistributedLock("music-dispatch", TimeSpan.FromSeconds(5));
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ScoreDb>();
                foreach (var id in await db.Jobs.Where(j => j.State == "Queued" && j.BackgroundId == null && !j.CancelRequested).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).Select(j => j.Id).Take(100).ToListAsync(stoppingToken))
                {
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Recognize(id, CancellationToken.None));
                    // The worker can start before this update. Do not overwrite its state/xmin.
                    await db.Jobs.Where(j => j.Id == id && j.State == "Queued" && j.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("📥 Dispatched score recognition {ScoreId} as Hangfire job {BackgroundJobId}", id, backgroundId);
                }
                foreach (var revision in await db.Revisions.AsNoTracking().Where(r => r.State == "Queued" && r.BackgroundId == null).OrderBy(r => r.JobId).ThenBy(r => r.Version).Take(100).ToListAsync(stoppingToken))
                {
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Render(revision.JobId, revision.Version, CancellationToken.None));
                    await db.Revisions.Where(r => r.JobId == revision.JobId && r.Version == revision.Version && r.State == "Queued" && r.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("🎨 Dispatched score export {ScoreId} revision {Revision} as Hangfire job {BackgroundJobId}", revision.JobId, revision.Version, backgroundId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "❌ Durable score dispatch failed; retrying"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        logger.LogInformation("🛑 Dispatch loop stopped");
    }
}

public sealed class ScoreProcessor(ScoreDb db, IRecognitionEngine engine, IScoreStorage files, JobStorage storage, IServiceScopeFactory scopes, ILogger<ScoreProcessor> logger)
{
    [Queue("recognition"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    public async Task Recognize(Guid id, CancellationToken shutdown)
    {
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        var job = await db.Jobs.SingleAsync(j => j.Id == id, shutdown);
        if (job.CancelRequested)
        {
            if (job.State == "Cancelling") { job.State = "Cancelled"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown); }
            logger.LogInformation("🛑 Cancellation was requested; skipping recognition of {ScoreId}", id);
            return;
        }
        if (job.State == "Ready") { logger.LogDebug("✅ {ScoreId} is already Ready; skipping recognition", id); return; }
        job.State = "Processing"; job.Attempts++; job.ErrorCode = null; job.ErrorMessage = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(shutdown);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("⚙️ Score recognition started {ScoreId} attempt {Attempt} extension {Extension} preprocessing {Preprocessing} page {PageNumber}",
            id, job.Attempts, job.Extension, job.Preprocessing, job.PageNumber);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var watcher = WatchCancellation(id, cancelled);
        try
        {
            if (job.ParentId is null && !job.PagesPrepared)
            {
                logger.LogInformation("📄 PDF page preparation started {ScoreId}", id);
                var document = await engine.PrepareDocumentAsync(id, job.Extension, cancelled.Token);
                logger.LogInformation("📑 PDF page preparation completed {ScoreId} pageCount {PageCount} elapsedMs {ElapsedMs}", id, document.PageCount, stopwatch.ElapsedMilliseconds);
                job.PageCount = document.PageCount; job.PagesPrepared = true;
                if (document.PageCount > 1)
                {
                    for (var number = 1; number <= document.PageCount; number++)
                    {
                        var child = new ScoreJob { Id = Guid.NewGuid(), ParentId = id, PageNumber = number, PagesPrepared = true,
                            TokenHash = job.TokenHash, RequestHash = Guid.NewGuid().ToString("N"), FileHash = job.FileHash,
                            FileName = $"Page {number}.pdf", Extension = ".pdf", Preprocessing = job.Preprocessing };
                        var directory = files.JobDirectory(child.Id); Directory.CreateDirectory(directory);
                        File.Copy(Path.Combine(files.JobDirectory(id), "document", $"{number}.pdf"), Path.Combine(directory, "source.pdf"));
                        db.Jobs.Add(child);
                    }
                    await db.SaveChangesAsync(cancelled.Token);
                    logger.LogInformation("📑 PDF page jobs created {ScoreId} pageCount {PageCount} elapsedMs {ElapsedMs}", id, document.PageCount, stopwatch.ElapsedMilliseconds);
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
            logger.LogInformation("✅ Score recognition completed {ScoreId} revision {Revision} noteCount {NoteCount} warningCount {WarningCount} elapsedMs {ElapsedMs}",
                id, job.CurrentRevision, snapshot.Notes.Length, snapshot.Warnings.Length, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            job.State = job.CancelRequested ? "Cancelled" : "Queued"; job.BackgroundId = null; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            logger.LogWarning("🛑 Score recognition cancelled or interrupted {ScoreId} state {State} elapsedMs {ElapsedMs}", id, job.State, stopwatch.ElapsedMilliseconds);
            if (shutdown.IsCancellationRequested) throw;
        }
        catch (Exception e)
        {
            db.ChangeTracker.Clear(); job = await db.Jobs.SingleAsync(j => j.Id == id);
            if (!job.CancelRequested) { job.State = "Failed"; job.ErrorCode = e is ToolFailure t ? t.Code : "recognition-failed"; job.ErrorMessage = e is ToolFailure ? e.Message : "Recognition failed. Consult the server logs using this score ID."; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            logger.LogError(e, "💥 Score recognition failed {ScoreId} document {DocumentId} page {PageNumber} errorCode {ErrorCode} failureReason {FailureReason} attempt {Attempt} elapsedMs {ElapsedMs}",
                id, job.ParentId ?? id, job.PageNumber, job.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, job.Attempts, stopwatch.ElapsedMilliseconds);
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
            if (await other.Jobs.AnyAsync(j => j.Id == id && j.CancelRequested, stop.Token)) { logger.LogDebug("🛑 Cancel request detected by poller for {ScoreId}", id); stop.Cancel(); return; }
        }
    }
    [Queue("render"), AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 5, 15 })]
    public async Task Render(Guid id, int version, CancellationToken shutdown)
    {
        using var connection = storage.GetConnection(); using var gate = connection.AcquireDistributedLock("score-" + id, TimeSpan.FromMinutes(10));
        var r = await db.Revisions.FindAsync([id, version], shutdown);
        if (r is null || r.State == "Ready") { logger.LogDebug("✅ Nothing to render for {ScoreId} revision {Revision}", id, version); return; }
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, shutdown);
        r.State = "Processing"; await db.SaveChangesAsync(shutdown);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("🎨 Score export started {ScoreId} revision {Revision} pageCount {PageCount}", id, version, job.PageCount);
        try
        {
            if (job.PageCount > 1) await engine.MergeDocumentAsync(id, version, ScoreJson.Read<DocumentExportSnapshot>(r.Snapshot), shutdown);
            else await engine.RenderAsync(id, version, job.Extension, ScoreJson.Read<ScoreSnapshot>(r.Snapshot), shutdown);
            r.State = "Ready"; r.ErrorCode = null; await db.SaveChangesAsync(shutdown);
            logger.LogInformation("✅ Score export completed {ScoreId} revision {Revision} pageCount {PageCount} elapsedMs {ElapsedMs}", id, version, job.PageCount, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { r.State = "Queued"; r.BackgroundId = null; await db.SaveChangesAsync(); logger.LogWarning("⏸️ Score export interrupted by worker shutdown {ScoreId} revision {Revision} elapsedMs {ElapsedMs}", id, version, stopwatch.ElapsedMilliseconds); throw; }
        catch (Exception e) { r.State = "Failed"; r.ErrorCode = e is ToolFailure t ? t.Code : "render-failed"; await db.SaveChangesAsync(); logger.LogError(e, "💥 Score export failed {ScoreId} revision {Revision} errorCode {ErrorCode} failureReason {FailureReason} elapsedMs {ElapsedMs}", id, version, r.ErrorCode, e is ToolFailure failure ? failure.DiagnosticReason ?? e.Message : e.Message, stopwatch.ElapsedMilliseconds); }
    }
}
