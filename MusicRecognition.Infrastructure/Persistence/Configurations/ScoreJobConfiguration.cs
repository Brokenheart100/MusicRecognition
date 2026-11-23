using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MusicRecognition.Application;
using MusicRecognition.Application.Enums;

namespace MusicRecognition.Infrastructure.Persistence.Configurations;

// 任务表的数据库映射：规定主键、唯一索引、默认值和父子任务关系。
// EF Core 按这里的规则生成建表语句；改这里等于改表结构，需要配套数据库迁移。
// 唯一索引是防重复的防线：同一个请求哈希只会落一条任务。
internal sealed class ScoreJobConfiguration : IEntityTypeConfiguration<ScoreJob>
{
    // 配置 ScoreJob（识谱任务）在 PostgreSQL 里的列、约束和默认值。
    public void Configure(EntityTypeBuilder<ScoreJob> builder)
    {
        builder.HasKey(j => j.Id);
        // 请求哈希唯一：同一份文件、同一组参数重复提交时会命中已有任务，不会重复建任务。
        builder.HasIndex(j => j.RequestHash).IsUnique();
        // 行版本号，更新时自动比对；两人同时改同一条任务会抛并发冲突而不是互相覆盖。
        builder.Property(j => j.Version).IsRowVersion();
        // 老数据或调用方没指定时按“关闭预处理、Audiveris 引擎”处理。
        builder.Property(j => j.Preprocessing).HasDefaultValue(PreprocessingMode.None);
        builder.Property(j => j.Engine).HasDefaultValue(RecognitionEngineKind.Audiveris);
        // 页码和总页数默认 1，表示单页文档。
        builder.Property(j => j.PageNumber).HasDefaultValue(1);
        builder.Property(j => j.PageCount).HasDefaultValue(1);
        // 同一个父任务下页码不能重复；过滤条件把没有父任务的整份文档排除在唯一约束外。
        builder.HasIndex(j => new { j.ParentId, j.PageNumber }).IsUnique().HasFilter("\"ParentId\" IS NOT NULL");
        // 父任务和子任务自关联；限制删除，避免删父任务时把逐页任务变成孤儿。
        builder.HasOne<ScoreJob>().WithMany().HasForeignKey(j => j.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
