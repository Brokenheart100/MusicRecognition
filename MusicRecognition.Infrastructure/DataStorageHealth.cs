using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MusicRecognition.Infrastructure;

public sealed class DataStorageHealth(RecognitionOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(options.DataRoot);
            var probe = Path.Combine(options.DataRoot, ".health-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataRoot))!);
            return Task.FromResult(drive.AvailableFreeSpace < options.MinimumFreeSpaceMiB * 1024L * 1024
                ? HealthCheckResult.Unhealthy("谱面存储可用空间低于配置下限。")
                : HealthCheckResult.Healthy("谱面存储可写。"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Task.FromResult(HealthCheckResult.Unhealthy("谱面持久存储不可写。")); }
    }
}
