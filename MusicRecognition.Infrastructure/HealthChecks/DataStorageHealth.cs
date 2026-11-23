using Microsoft.Extensions.Diagnostics.HealthChecks;
using MusicRecognition.Infrastructure.Configuration;

namespace MusicRecognition.Infrastructure.HealthChecks;

// 谱面存储的健康检查：往数据目录真正写一个临时文件，确认磁盘可写。
// 顺带看一眼剩余空间，低于配置下限就报“不健康”。
// 结果会汇总到 /health 接口，运维靠它判断服务现在能不能收新任务。
public sealed class DataStorageHealth(RecognitionOptions options) : IHealthCheck
{
    // 每次健康检查都现场试写一次，因为磁盘满、权限变更是随时可能发生的。
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // 数据目录可能被清理过，先补建；目录建不出来也会在这里暴露成异常。
        // 数据目录可能被清理过，先补建；目录建不出来也会在这里暴露成异常。
            Directory.CreateDirectory(options.DataRoot);
            // 用随机文件名写探针，DeleteOnClose 保证文件流一关就自动删除，不留垃圾。
        // 用随机文件名写探针，DeleteOnClose 保证文件流一关就自动删除，不留垃圾。
            var probe = Path.Combine(options.DataRoot, ".health-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            // 取数据目录所在盘的剩余空间；配置下限单位是 MiB，乘 1024 换成字节再比较。
        // 取数据目录所在盘的剩余空间；配置下限单位是 MiB，乘 1024 换成字节再比较。
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataRoot))!);
            return Task.FromResult(drive.AvailableFreeSpace < options.MinimumFreeSpaceMiB * 1024L * 1024
                ? HealthCheckResult.Unhealthy("谱面存储可用空间低于配置下限。")
                : HealthCheckResult.Healthy("谱面存储可写。"));
        }
        // 只吞掉“磁盘写不了”这类预期错误，其它异常（例如配置为空）继续向上抛。
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Task.FromResult(HealthCheckResult.Unhealthy("谱面持久存储不可写。")); }
    }
}
