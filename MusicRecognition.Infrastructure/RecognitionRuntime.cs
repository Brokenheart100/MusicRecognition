using System.Text;
using CliWrap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure;

public sealed class RecognitionRuntime(RecognitionOptions options, ILogger<RecognitionRuntime> logger) : IHealthCheck
{
    private bool ready;

    public async Task ValidateAsync(CancellationToken ct)
    {
        ready = false;
        logger.LogInformation("🩺 正在检查识谱依赖（Python {Python}，PDF 渲染工具 {Pdftoppm}）", options.Python, options.PdftoppmPath);
        List<string> arguments = ["-u", Path.Combine(options.RepositoryRoot, "scripts", "runtime", "preflight.py"),
            "--root", options.RepositoryRoot, "--pdftoppm", options.PdftoppmPath, "--font", options.Font];
        if (options.RequireOpenCv) arguments.Add("--require-opencv");
        if (options.RequireComposition) arguments.Add("--require-composition");
        var output = new StringBuilder(); var error = new StringBuilder();
        static void Capture(StringBuilder buffer, string line)
        {
            buffer.AppendLine(line[..Math.Min(line.Length, 4000)]);
            if (buffer.Length > 16000) buffer.Remove(0, buffer.Length - 16000);
        }
        var command = Cli.Wrap(options.Python)
            .WithArguments(arguments)
            .WithWorkingDirectory(options.RepositoryRoot)
            .WithEnvironmentVariables(env => env.Set("PYTHONIOENCODING", "utf-8"))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => Capture(output, line), Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Capture(error, line), Encoding.UTF8))
            .WithValidation(CommandResultValidation.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await ToolCommand.ExecuteAsync(command, timeout.Token, logger);
            var stdout = output.ToString(); var stderr = error.ToString();
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"识谱依赖检查失败： {stderr.Trim()} {stdout.Trim()}");
            ready = true;
            logger.LogInformation("✅ 识谱依赖检查通过：{Runtime}", stdout.Trim());
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "❌ 后台服务启动依赖检查失败。请运行 scripts/setup/prepare.ps1，并检查 Recognition:Python、Recognition:Pdftoppm 和 Recognition:Font。");
            throw;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var files = new[] { options.PdftoppmPath, options.Font,
            Path.Combine(options.RepositoryRoot, ".tools", "poc-classes", "musicrecognition", "poc", "ExportNotes.class"),
            Path.Combine(options.RepositoryRoot, ".tools", "audiveris", "Audiveris", "runtime", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java") };
        var healthy = ready && files.All(File.Exists);
        if (!healthy) logger.LogInformation("🩺 识谱运行环境健康检查失败（已验证 {Validated}，缺少 {MissingCount} 个工具）", ready, files.Count(f => !File.Exists(f)));
        return Task.FromResult(healthy
            ? HealthCheckResult.Healthy("识谱工具已通过启动检查。")
            : HealthCheckResult.Unhealthy("识谱工具尚未就绪，请查看后台服务诊断日志。"));
    }
}
