using Microsoft.EntityFrameworkCore;
using MusicRecognition.Application;
using MusicRecognition.Infrastructure.Persistence.Configurations;

namespace MusicRecognition.Infrastructure;

// 数据库上下文：整个基础设施层访问 PostgreSQL 的唯一入口。
// 目前只映射两张表：Jobs（识谱任务）和 Revisions（识谱结果修订）。
// 表名和列名由 Migrations 目录里的迁移文件决定，这里只暴露集合入口。
public sealed class ScoreDb(DbContextOptions<ScoreDb> options) : DbContext(options)
{
    // 两张表的查询入口：Jobs 存任务进度，Revisions 存每次识别的结果快照。
    public DbSet<ScoreJob> Jobs => Set<ScoreJob>();
    public DbSet<ScoreRevision> Revisions => Set<ScoreRevision>();

    // 建表规则拆到 Configurations 目录的配置类里，这里只负责挂上。
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfiguration(new ScoreJobConfiguration());
        model.ApplyConfiguration(new ScoreRevisionConfiguration());
    }
}
