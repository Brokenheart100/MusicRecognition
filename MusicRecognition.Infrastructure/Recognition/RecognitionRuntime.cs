using System.Text;
using CliWrap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MusicRecognition.Infrastructure.Configuration;

namespace MusicRecognition.Infrastructure.Recognition;

// 识谱运行环境自检：Worker 启动时先跑一次，确认 Python、PDF 渲染工具、字体都可用。
// 工具链有一样没准备好就让启动失败——带着坏环境跑，只会让后面每个任务都失败。
// 同时实现健康检查接口：启动自检通过、关键文件还在，才算“能识谱”。
public sealed class RecognitionRuntime(RecognitionOptions options, ILogger<RecognitionRuntime> logger) : IHealthCheck
{
    // 记录启动自检是否通过，健康检查接口据此快速回答。
    private bool ready;

    // 启动自检：调 scripts/runtime/preflight.py 逐项检查依赖，失败抛异常。
    // 自查内容包括 Python 包、PDF 渲染工具、字体；开启 RequireOpenCv 时还要求 OpenCV。
    public async Task ValidateAsync(CancellationToken ct)
    {
        ready = false;
        logger.LogInformation("🩺 正在检查识谱依赖（Python {Python}，PDF 渲染工具 {Pdftoppm}）", options.Python, options.PdftoppmPath);
        // 把脚本需要的路径都显式传进去：仓库根、渲染工具、字体，避免脚本靠猜。
        List<string> arguments = ["-u", Path.Combine(options.RepositoryRoot, "scripts", "runtime", "preflight.py"),
            "--root", options.RepositoryRoot, "--pdftoppm", options.PdftoppmPath, "--font", options.Font];
        // 自适应预处理依赖 OpenCV；配置要求时缺库即视为环境不合格。
        if (options.RequireOpenCv) arguments.Add("--require-opencv");
        var output = new StringBuilder(); var error = new StringBuilder();
        // 收集脚本输出：每行最多 4000 字符、总量最多 16000 字符，防止日志撑爆内存。
        static void Capture(StringBuilder buffer, string line)
        {
            buffer.AppendLine(line[..Math.Min(line.Length, 4000)]);
            if (buffer.Length > 16000) buffer.Remove(0, buffer.Length - 16000);
        }
        // 用 CliWrap 调 Python；PYTHONIOENCODING=utf-8 保证中文路径在控制台不乱码。
        var command = Cli.Wrap(options.Python)
            .WithArguments(arguments)
            .WithWorkingDirectory(options.RepositoryRoot)
            .WithEnvironmentVariables(env => env.Set("PYTHONIOENCODING", "utf-8"))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Capture(output, line), Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Capture(error, line), Encoding.UTF8))
            // 退出码自己判断，不让 CliWrap 直接抛异常，好统一包装错误信息。
            .WithValidation(CommandResultValidation.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 自检最多等 60 秒，外部依赖卡死时不要拖住整个 Worker 启动。
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await ToolCommand.ExecuteAsync(command, timeout.Token, logger);
            var stdout = output.ToString(); var stderr = error.ToString();
            // 非 0 退出说明有依赖没准备好，把标准错误和标准输出一起带出来方便定位。
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"识谱依赖检查失败： {stderr.Trim()} {stdout.Trim()}");
            // 通过后记下标记，健康检查接口据此回答“就绪”。
            ready = true;
            logger.LogInformation("✅ 识谱依赖检查通过：{Runtime}", stdout.Trim());
        }
        catch (Exception exception)
        {
            // 自检失败直接抛异常，让 Worker 启动失败，问题尽早暴露。
            logger.LogCritical(exception, "❌ 后台服务启动依赖检查失败。请运行 scripts/setup/prepare.ps1，并检查 Recognition:Python、Recognition:Pdftoppm 和 Recognition:Font。");
            throw;
        }
    }

    // 健康检查：启动自检通过、且关键工具文件都还在，才算识谱可用。
    // 文件被清理或挂载丢失时，即使启动时通过过，这里也会报不健康。
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // 逐个确认四样东西：PDF 转图工具、字体、音符坐标导出脚本、内置 Java 运行时。
        var files = new[] { options.PdftoppmPath, options.Font,
            Path.Combine(options.RepositoryRoot, ".tools", "poc-classes", "musicrecognition", "poc", "ExportNotes.class"),
            Path.Combine(options.RepositoryRoot, ".tools", "audiveris", "Audiveris", "runtime", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java") };
        // 启动标志和文件存在两个条件都满足才健康。
        var healthy = ready && files.All(File.Exists);
        if (!healthy) logger.LogInformation("🩺 识谱运行环境健康检查失败（已验证 {Validated}，缺少 {MissingCount} 个工具）", ready, files.Count(f => !File.Exists(f)));
        return Task.FromResult(healthy
            ? HealthCheckResult.Healthy("识谱工具已通过启动检查。")
            : HealthCheckResult.Unhealthy("识谱工具尚未就绪，请查看后台服务诊断日志。"));
    }
}
