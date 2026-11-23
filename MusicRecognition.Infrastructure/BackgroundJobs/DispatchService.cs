using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Infrastructure.Observability;

namespace MusicRecognition.Infrastructure.BackgroundJobs;

// 后台调度员：把数据库里“排队中”的任务领出来，交给 Hangfire 真正执行。
// 数据库里的任务表相当于一个发件箱（outbox）：状态持久化，服务重启也不丢任务。
// 多实例部署时靠一把分布式锁互斥，同一时刻只有一个实例在派发。
// The domain records form a durable dispatch outbox. Publishing a Hangfire job twice is safe:
// each handler takes the same per-score lock, then checks the persisted terminal state.
public sealed class DispatchService(IServiceScopeFactory scopes, IBackgroundJobClient jobs, JobStorage storage, ILogger<DispatchService> logger) : BackgroundService
{
    // 常驻循环：每 2 秒扫一次数据库，把新任务派发出去，直到服务关闭。
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 后台调度已启动：每 2 秒扫描一次待处理任务（锁 music-dispatch）");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 先抢一把 5 秒有效的分布式锁；抢不到就跳过这一轮，避免重复派发。
                using var connection = storage.GetConnection();
                using var gate = connection.AcquireDistributedLock("music-dispatch", TimeSpan.FromSeconds(5));
                // 每轮新建一个依赖注入作用域，用完即释放，数据库连接不会一直挂着。
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ScoreDb>();
                var repository = scope.ServiceProvider.GetRequiredService<IScoreRepository>();
                // 挑出“已排队、未派发、未请求取消”的识谱任务，按提交先后，每轮最多 100 个。
                foreach (var job in await db.Jobs.AsNoTracking().Where(j => j.State == "Queued" && j.BackgroundId == null && !j.CancelRequested).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).Take(100).ToListAsync(stoppingToken))
                {
                    var id = job.Id;
                    var context = await ScoreDiagnostics.ResolveAsync(repository, job, stoppingToken);
                    using var logScope = logger.BeginScope(context.Scope());
                    // 交给 Hangfire 排队，返回后台任务编号；真正的识谱在 ScoreProcessor 里做。
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Recognize(id, CancellationToken.None));
                    // The worker can start before this update. Do not overwrite its state/xmin.
                    // 只回填后台任务编号，并再次确认状态没被改动，避免覆盖 Worker 写入的进度。
                    // 用条件更新（WHERE 仍要求排队中）回填编号，避免覆盖 Worker 刚写入的进度。
                    await db.Jobs.Where(j => j.Id == id && j.State == "Queued" && j.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("📥 {ScoreName}：已加入识谱队列（任务 {ScoreId}，后台任务 {BackgroundJobId}）", context.ScoreName, id, backgroundId);
                }
                // 再处理导出：修订处于排队且未派发时，交给 Render 生成带简谱标注的 PDF。
                foreach (var revision in await db.Revisions.AsNoTracking().Where(r => r.State == "Queued" && r.BackgroundId == null).OrderBy(r => r.JobId).ThenBy(r => r.Version).Take(100).ToListAsync(stoppingToken))
                {
                    // 修订属于某份乐谱，取父任务信息，让日志上下文能显示文件名。
                    var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == revision.JobId, stoppingToken);
                    var context = await ScoreDiagnostics.ResolveAsync(repository, job, stoppingToken);
                    using var logScope = logger.BeginScope(context.Scope());
                    // 导出走单独的 render 队列；同一份乐谱的识谱和导出共用一把按乐谱的锁。
                    var backgroundId = jobs.Enqueue<ScoreProcessor>(p => p.Render(revision.JobId, revision.Version, CancellationToken.None));
                    // 同样用条件更新回填导出编号；状态被 Worker 改过就不覆盖。
                    await db.Revisions.Where(r => r.JobId == revision.JobId && r.Version == revision.Version && r.State == "Queued" && r.BackgroundId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.BackgroundId, backgroundId), stoppingToken);
                    logger.LogInformation("🎨 {ScoreName}：修订 {Revision} 已加入导出队列（任务 {ScoreId}，后台任务 {BackgroundJobId}）", context.ScoreName, revision.Version, revision.JobId, backgroundId);
                }
            }
            // 服务正在停止：安静退出循环，不当作错误。
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // 单次扫描失败不让后台服务退出，记日志后等下一轮重试。
            catch (Exception e) { logger.LogError(e, "❌ 后台任务调度失败，稍后重试"); }
            // 固定 2 秒一轮；空转开销很小，换来新任务几乎立刻被接走。
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        logger.LogInformation("🛑 后台任务调度已停止");
    }
}
