using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Exceptions;
using MusicRecognition.Application.Interfaces;
using Npgsql;

namespace MusicRecognition.Infrastructure.Persistence.Repositories;

// 数据仓库（repository）：把“任务、修订怎么存怎么查”包成普通方法，上游不写数据库语句。
// 应用层只说“按任务 ID 找任务”“按请求哈希找任务”，SQL 由这里的 EF Core 生成。
// 保存时把数据库错误翻译成 HTTP 语义（409 冲突等），上层不必认识 PostgreSQL 错误码。
public sealed class ScoreRepository(ScoreDb db, ILogger<ScoreRepository> logger) : IScoreRepository
{
    // 按任务 ID 找任务；找不到返回 null，由上层决定报 404 还是新建。
    public Task<ScoreJob?> FindJobAsync(Guid id, CancellationToken ct) => db.Jobs.SingleOrDefaultAsync(j => j.Id == id, ct);
    // 按请求哈希找任务：同一份文件、同一组参数重复提交时能认出来。
    public Task<ScoreJob?> FindRequestAsync(string hash, CancellationToken ct) => db.Jobs.SingleOrDefaultAsync(j => j.RequestHash == hash, ct);
    // 按“任务 ID + 版本号”找对应的修订快照。
    public async Task<ScoreRevision?> FindRevisionAsync(Guid id, int version, CancellationToken ct) => await db.Revisions.FindAsync([id, version], ct);
    // 取出多页文档的全部逐页子任务，并按页码排好序。
    public Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct) => db.Jobs.Where(j => j.ParentId == parentId).OrderBy(j => j.PageNumber).ToArrayAsync(ct);
    // 一次性查出逐页任务的展示信息，省得前端逐页再查一次。
    public Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct) => db.Jobs.AsNoTracking().Where(j => j.ParentId == parentId).OrderBy(j => j.PageNumber)
        .Select(j => new PageJobView(j.PageNumber, j.Id, j.State, j.CurrentRevision, j.ErrorCode,
            // 每页当前修订的导出状态用子查询现算，取不到就返回空。
            db.Revisions.Where(r => r.JobId == j.Id && r.Version == j.CurrentRevision).Select(r => r.State).FirstOrDefault())).ToArrayAsync(ct);
    // 把新对象挂到变更跟踪器，真正写库要等 SaveAsync。
    public void Add(ScoreJob job) => db.Add(job);
    public void Add(ScoreRevision revision) => db.Add(revision);
    // 清空变更跟踪器：重试前丢掉内存里改到一半的实体。
    public void Clear() => db.ChangeTracker.Clear();
    // 统一保存入口：把数据库错误翻译成上层认识的业务错误。
    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("💾 乐谱任务状态已保存");
        }
        // 行版本号对不上说明别人先改了，提示前端刷新后重试。
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("⚠️ 保存乐谱状态时发生并发冲突，请重新加载后重试");
            throw new RequestFailure(409, "concurrency-conflict", "State changed. Reload before retrying.");
        }
        // 23505 是 PostgreSQL 唯一约束冲突的错误码。
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } p)
        {
            logger.LogWarning(e, "⚠️ 保存乐谱状态时遇到重复记录：约束 {Constraint}", p.ConstraintName);
            // 两个唯一索引分别对应“重复提交同一请求”和“重复写入同一修订”。
            throw new RequestFailure(409, p.ConstraintName == "IX_Jobs_RequestHash" ? "duplicate-request" : "revision-conflict", "This request or revision was already saved.");
        }
    }
}
