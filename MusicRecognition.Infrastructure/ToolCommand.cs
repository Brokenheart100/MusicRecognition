using System.Globalization;
using CliWrap;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure;

internal static class ToolCommand
{
    public static async Task<CommandResult> ExecuteAsync(Command command, CancellationToken ct, ILogger logger, Action<int>? started = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            var execution = command.ExecuteAsync(ct);
            started?.Invoke(execution.ProcessId);
            return await execution;
        }

        // .NET 10 Kill(entireProcessTree) can miss descendants of Windows Python launchers:
        // https://github.com/dotnet/runtime/issues/107992
        // Let taskkill snapshot/terminate this specific tree before CliWrap kills its root.
        using var force = new CancellationTokenSource();
        var running = command.ExecuteAsync(force.Token);
        try
        {
            started?.Invoke(running.ProcessId);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(running.Task, cancelled.Task) == cancelled.Task && !running.Task.IsCompleted)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var cleanup = await Cli.Wrap(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"))
                        .WithArguments(["/PID", running.ProcessId.ToString(CultureInfo.InvariantCulture), "/T", "/F"])
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteAsync(cleanupTimeout.Token);
                    if (cleanup.ExitCode != 0)
                        logger.LogWarning("⚠️ 工具进程树清理返回 {ExitCode}（进程 {ProcessId}），将执行强制终止；进程可能已自行退出。", cleanup.ExitCode, running.ProcessId);
                }
                catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning(exception, "⚠️ 工具进程树清理未完成（进程 {ProcessId}），将执行强制终止。", running.ProcessId);
                }
                finally { await force.CancelAsync(); }
            }
            var result = await running;
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            await force.CancelAsync();
            // Observe completion even if the start callback or cleanup throws, before pipes are disposed.
            try { await running; } catch (Exception) { }
        }
    }
}
