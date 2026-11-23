using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure.Persistence.Configurations;

// 修订表的数据库映射：每识别一次就追加一条不可变快照，旧修订永远保留。
// 主键是“任务 ID + 版本号”，同一个任务可以有很多个版本。
// 快照用 PostgreSQL 的 jsonb 存，读取时不必再关联别的表。
internal sealed class ScoreRevisionConfiguration : IEntityTypeConfiguration<ScoreRevision>
{
    public void Configure(EntityTypeBuilder<ScoreRevision> builder)
    {
        // 用任务 ID 和版本号组成联合主键，天然保证同一任务的同一版本只有一条记录。
        builder.HasKey(r => new { r.JobId, r.Version });
        // jsonb 是 PostgreSQL 的二进制 JSON 类型，保存紧凑，也能直接查询内部字段。
        builder.Property(r => r.Snapshot).HasColumnType("jsonb");
        // 外键指向所属任务；修订不会单独存在，删除仍被引用的任务会被数据库拦住。
        builder.HasOne<ScoreJob>().WithMany().HasForeignKey(r => r.JobId);
    }
}
