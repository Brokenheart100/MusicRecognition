using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MusicRecognition.Application;
using MusicRecognition.Application.Enums;

namespace MusicRecognition.Infrastructure.Persistence.Configurations;

internal sealed class ScoreJobConfiguration : IEntityTypeConfiguration<ScoreJob>
{
    public void Configure(EntityTypeBuilder<ScoreJob> builder)
    {
        builder.HasKey(j => j.Id);
        builder.HasIndex(j => j.RequestHash).IsUnique();
        builder.Property(j => j.Version).IsRowVersion();
        builder.Property(j => j.Preprocessing).HasDefaultValue(PreprocessingMode.None);
        builder.Property(j => j.Engine).HasDefaultValue(RecognitionEngineKind.Audiveris);
        builder.Property(j => j.PageNumber).HasDefaultValue(1);
        builder.Property(j => j.PageCount).HasDefaultValue(1);
        builder.HasIndex(j => new { j.ParentId, j.PageNumber }).IsUnique().HasFilter("\"ParentId\" IS NOT NULL");
        builder.HasOne<ScoreJob>().WithMany().HasForeignKey(j => j.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
