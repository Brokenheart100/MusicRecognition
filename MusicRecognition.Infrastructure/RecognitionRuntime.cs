using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure;

public sealed class RecognitionRuntime(RecognitionOptions options, ILogger<RecognitionRuntime> logger) : IHealthCheck
{
    private bool ready;

    public async Task ValidateAsync(CancellationToken ct)
    {
        ready = false;
        logger.LogInformation("🩺 Validating recognition dependencies (python {Python}, pdftoppm {Pdftoppm})", options.Python, options.PdftoppmPath);
        var start = new ProcessStartInfo(options.Python)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = options.RepositoryRoot
        };
        foreach (var arg in new[] { "-u", Path.Combine(options.RepositoryRoot, "scripts", "runtime", "preflight.py"),
                     "--root", options.RepositoryRoot, "--pdftoppm", options.PdftoppmPath, "--font", options.Font })
            start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            var stdout = await output; var stderr = await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Recognition dependency check failed: {stderr.Trim()} {stdout.Trim()}");
            ready = true;
            logger.LogInformation("✅ Recognition dependencies ready: {Runtime}", stdout.Trim());
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "❌ Worker startup dependency check failed. Run scripts/runtime/prepare.ps1 and check Recognition:Python, Recognition:Pdftoppm and Recognition:Font.");
            throw;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var files = new[] { options.PdftoppmPath, options.Font,
            Path.Combine(options.RepositoryRoot, ".tools", "poc-classes", "musicrecognition", "poc", "ExportNotes.class"),
            Path.Combine(options.RepositoryRoot, ".tools", "audiveris", "Audiveris", "runtime", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java") };
        var healthy = ready && files.All(File.Exists);
        if (!healthy) logger.LogDebug("🩺 Recognition runtime health check failed (validated {Validated}, missing {MissingCount} tools)", ready, files.Count(f => !File.Exists(f)));
        return Task.FromResult(healthy
            ? HealthCheckResult.Healthy("Recognition tools passed startup validation.")
            : HealthCheckResult.Unhealthy("Recognition tools are not ready. See protected worker logs."));
    }
}
