using System.Globalization;
using CliWrap;
using Microsoft.Extensions.Logging;

namespace MusicRecognition.Infrastructure.Recognition;

// 调外部工具进程的统一封装：所有 Python 脚本都从这里启动，超时与取消行为一致。
// 收什么：要执行的命令、取消令牌、日志器；产出：进程的退出码和输出。
// 为什么单独一层：Windows 上终止外部进程时子进程容易漏杀，需要专门处理。
internal static class ToolCommand
{
    // 执行命令并等待结束；started 回调把进程号上报，调用方可据此记录进度。
    // 取消令牌触发时会终止整棵进程树，并抛出取消异常。
    public static async Task<CommandResult> ExecuteAsync(Command command, CancellationToken ct, ILogger logger, Action<int>? started = null)
    {
        ct.ThrowIfCancellationRequested();
        // 非 Windows 系统进程树管理足够可靠，直接用 CliWrap 执行。
        if (!OperatingSystem.IsWindows())
        {
            var execution = command.ExecuteAsync(ct);
            started?.Invoke(execution.ProcessId);
            return await execution;
        }

        // Windows 专用分支：由 taskkill 先把整棵进程树清掉，再让 CliWrap 终止根进程。
        // .NET 10 Kill(entireProcessTree) can miss descendants of Windows Python launchers:
        // https://github.com/dotnet/runtime/issues/107992
        // Let taskkill snapshot/terminate this specific tree before CliWrap kills its root.
        // 这个令牌只负责“强制停止”，是否取消由外部令牌决定。
        using var force = new CancellationTokenSource();
        var running = command.ExecuteAsync(force.Token);
        try
        {
            started?.Invoke(running.ProcessId);
            // 把“收到取消通知”变成可等待的信号，方便和进程结束赛跑。
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancelled.TrySetResult());
            // 谁先完成：进程自己退出，还是外部要求取消。
            if (await Task.WhenAny(running.Task, cancelled.Task) == cancelled.Task && !running.Task.IsCompleted)
            {
                // 清理最多给 10 秒，防止 taskkill 本身卡死。
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    // /T 连子进程一起、/F 强制；PID 用不变文化格式转字符串，避开本地化数字差异。
                    var cleanup = await Cli.Wrap(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"))
                        .WithArguments(["/PID", running.ProcessId.ToString(CultureInfo.InvariantCulture), "/T", "/F"])
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteAsync(cleanupTimeout.Token);
                    // taskkill 返回非 0 常见于进程已自行退出，只警告，后面还有兜底。
                    if (cleanup.ExitCode != 0)
                        logger.LogWarning("⚠️ 工具进程树清理返回 {ExitCode}（进程 {ProcessId}），将执行强制终止；进程可能已自行退出。", cleanup.ExitCode, running.ProcessId);
                }
                // 拿不到 taskkill（权限受限等）时只警告，继续走强制终止。
                catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning(exception, "⚠️ 工具进程树清理未完成（进程 {ProcessId}），将执行强制终止。", running.ProcessId);
                }
                // 不管清理成功与否都要让 CliWrap 停止进程，避免留下孤儿进程。
                finally { await force.CancelAsync(); }
            }
            // 取回进程结果；如果外部已取消但进程恰好正常退出，也按取消处理。
            var result = await running;
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            await force.CancelAsync();
            // 兜底：确保进程任务已被观察，异常不会以未观察任务的形式悄悄丢掉。
            // Observe completion even if the start callback or cleanup throws, before pipes are disposed.
            try { await running; } catch (Exception) { }
        }
    }
}
