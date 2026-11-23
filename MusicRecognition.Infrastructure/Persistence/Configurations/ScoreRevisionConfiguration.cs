using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MusicRecognition.Application;

namespace MusicRecognition.Infrastructure.Persistence.Configurations;

internal sealed class ScoreRevisionConfiguration : IEntityTypeConfiguration<ScoreRevision>
{
    public void Configure(EntityTypeBuilder<ScoreRevision> builder)
    {
        builder.HasKey(r => new { r.JobId, r.Version });
        builder.Property(r => r.Snapshot).HasColumnType("jsonb");
        builder.HasOne<ScoreJob>().WithMany().HasForeignKey(r => r.JobId);
    }
}
