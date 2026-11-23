using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Infrastructure.Observability;

namespace MusicRecognition.Infrastructure.BackgroundJobs;

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
