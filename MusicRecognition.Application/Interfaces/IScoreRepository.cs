using MusicRecognition.Application.DTOs;

namespace MusicRecognition.Application.Interfaces;

// 乐谱数据仓库接口（repository port）：应用层通过它读写任务（job）和修订（revision），不直接碰数据库。
// 上半部分是查询：按主键、按幂等请求哈希、按版本、按父子关系取任务。
// 两个 Add 只是把对象挂到待保存集合，SaveAsync 才真正写库并检查并发冲突（乐观锁）。
// Clear 用于保存失败后清掉待保存改动，避免脏数据影响下一次操作。
public interface IScoreRepository
{
    Task<ScoreJob?> FindJobAsync(Guid id,CancellationToken ct);
    Task<ScoreJob?> FindRequestAsync(string hash,CancellationToken ct);
    Task<ScoreRevision?> FindRevisionAsync(Guid id,int version,CancellationToken ct);
    Task<ScoreJob[]> FindPagesAsync(Guid parentId, CancellationToken ct);
    Task<PageJobView[]> PageViewsAsync(Guid parentId, CancellationToken ct);
    void Add(ScoreJob job);
    void Add(ScoreRevision revision);
    Task SaveAsync(CancellationToken ct);
    void Clear();
}
