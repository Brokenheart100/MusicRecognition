using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CliWrap;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Application.DTOs;
using MusicRecognition.Application.Enums;
using MusicRecognition.Application.Interfaces;
using MusicRecognition.Application.Serialization;
using MusicRecognition.Domain.Entities;
using MusicRecognition.Domain.Enums;
using MusicRecognition.Domain.Services;
using MusicRecognition.Domain.ValueObjects;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.Observability;

namespace MusicRecognition.Infrastructure.Recognition;

public sealed class ToolEngine(RecognitionOptions options, IScoreStorage files, ILogger<ToolEngine> logger, IScoreRepository repository) : IRecognitionEngine
{
    internal async Task Run(string operation, Guid id, string[] args, CancellationToken ct, string script = "pipeline.py", string? jobDirectory = null)
    {
        var job = await repository.FindJobAsync(id, ct);
        var context = await ScoreDiagnostics.ResolveAsync(repository, job ?? new ScoreJob { Id = id }, ct);
        using var scope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        var operationName = ScoreDiagnostics.Operation(operation);
        var dir = jobDirectory ?? files.JobDirectory(id); var logPath = Path.Combine(dir, $"{operation}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        string[] arguments = ["-u", Path.Combine(options.RepositoryRoot, "scripts", "runtime", script),
            operation, "--root", options.RepositoryRoot, "--job", dir, .. args];
        await using var log = new StreamWriter(logPath);
        log.AutoFlush = true;
        using var logGate = new SemaphoreSlim(1); long lines = 0;
        var stderrTail = new Queue<string>();
        async Task WriteLine(string line, bool isError = false)
        {
            var bounded = line[..Math.Min(line.Length, 4000)];
            if (isError) { if (stderrTail.Count == 8) stderrTail.Dequeue(); stderrTail.Enqueue(bounded); }
            if (Interlocked.Increment(ref lines) > 10000) return;
            await logGate.WaitAsync();
            try { await log.WriteLineAsync(bounded); } finally { logGate.Release(); }
        }
        var command = Cli.Wrap(options.Python)
            .WithArguments(arguments)
            .WithWorkingDirectory(options.RepositoryRoot)
            .WithEnvironmentVariables(env => env
                .Set("PYTHONIOENCODING", "utf-8")
                .Set("MUSICRECOGNITION_HOMR_PYTHON", options.HomrPythonPath)
                .Set("MUSICRECOGNITION_PDFTOPPM", options.PdftoppmPath))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => WriteLine(line), Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => WriteLine(line, isError: true), Encoding.UTF8))
            // Nonzero exits are translated below using the protected diagnostic report.
            .WithValidation(CommandResultValidation.None);
        var stopwatch = Stopwatch.StartNew();
        using var activity = ProcessingTelemetry.Activities.StartActivity("score." + operation);
        activity?.SetTag("operation", operation);
        var outcome = "error"; long peakRootMemory = 0;
        using var progressStop = new CancellationTokenSource();
        async Task ReportProgress(int processId)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var ticks = 0;
            while (await timer.WaitForNextTickAsync(progressStop.Token))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    peakRootMemory = Math.Max(peakRootMemory, process.WorkingSet64);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (++ticks % 20 != 0) continue;
                logger.LogInformation("⏳ {ScoreName}：{OperationName}仍在运行，已用 {ElapsedSeconds} 秒，输出 {OutputLines} 行（任务 {ScoreId}，操作 {Operation}）",
                    name, operationName, (long)stopwatch.Elapsed.TotalSeconds, Interlocked.Read(ref lines), id, operation);
            }
        }
        Task? progress = null;
        try
        {
            logger.LogInformation("🔧 {ScoreName}：启动脚本 {Script}，操作 {OperationName}，参数数 {ArgumentCount}（任务 {ScoreId}，操作 {Operation}）", name, script, operationName, arguments.Length, id, operation);
            // A failure before Python imports/initialization must not inherit an earlier attempt's report.
            File.Delete(Path.Combine(dir, "error.json"));
            var result = await ToolCommand.ExecuteAsync(command, timeout.Token, logger, processId =>
            {
                logger.LogInformation("▶️ {ScoreName}：开始{OperationName}，进程 {ProcessId}，超时 {TimeoutSeconds} 秒（任务 {ScoreId}，操作 {Operation}）", name, operationName, processId, options.TimeoutSeconds, id, operation);
                progress = ReportProgress(processId);
            });
            if (result.ExitCode != 0)
            {
                var errorFile = Path.Combine(dir, "error.json");
                var code = "tool-failed";
                var stderrSummary = string.Join("\n", stderrTail);
                if (stderrSummary.Length > 4000) stderrSummary = stderrSummary[^4000..];
                var reason = string.IsNullOrWhiteSpace(stderrSummary) ? "工具退出时未提供诊断报告或标准错误输出。" : stderrSummary;
                string? reportIssue = null;
                try
                {
                    if (File.Exists(errorFile))
                    {
                        if (new FileInfo(errorFile).Length > 65536) reportIssue = "诊断报告超过 64 KiB，改用标准错误输出。";
                        else
                        {
                            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(errorFile, ct));
                            var root = report.RootElement;
                            if (root.ValueKind != JsonValueKind.Object) reportIssue = "诊断报告格式无效，改用标准错误输出。";
                            else
                            {
                                if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())) code = c.GetString()![..Math.Min(c.GetString()!.Length, 128)];
                                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString())) reason = m.GetString()!;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { reportIssue = $"{e.GetType().Name}: {e.Message}"; }
                reason = reason[..Math.Min(reason.Length, 4000)];
                var message = code switch
                {
                    "unsupported-page-count" => "PDF 页数必须为 1 至 30 页。",
                    "encrypted-pdf" => "暂不支持加密 PDF，请上传未加密的文件。",
                    "pdf-renderer-unavailable" => "服务器 PDF 渲染工具不可用，请向管理员提供任务 ID。",
                    "homr-not-provisioned" => "homr 独立环境或模型尚未准备，请运行 scripts/setup/setup-homr.ps1，或选择 Audiveris。",
                    "homr-coordinate-unavailable" => "homr 未能提供可对应原图的音符坐标，已保留诊断结果；请使用 Audiveris 或提供更完整的谱面。",
                    "pitch-consistency-failed" => "识谱结果的音高、声部或符头坐标一致性校验未通过，尚未生成标注。请使用任务 ID 查看具体诊断日志。",
                    "octave-evidence-ambiguous" => "无法可靠确定八度线的范围或所属谱表，尚未生成标注。请使用任务 ID 查看八度线诊断记录。",
                    _ => $"{operationName}失败，请使用任务 ID {id} 查看服务器诊断日志。"
                };
                logger.LogWarning("❌ {ScoreName}：{OperationName}失败，退出码 {ExitCode}，错误码 {ErrorCode}，原因 {FailureReason}；诊断日志 {DiagnosticLog}；标准错误末尾 {StderrTail}；诊断报告问题 {DiagnosticReportIssue}（任务 {ScoreId}，操作 {Operation}）",
                    name, operationName, result.ExitCode, code, reason, logPath, stderrSummary, reportIssue, id, operation);
                throw new ToolFailure(code, message, reason);
            }
            logger.LogInformation("⏹️ {ScoreName}：{OperationName}完成，用时 {ElapsedMs} 毫秒，退出码 {ExitCode}，输出 {OutputLines} 行（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, stopwatch.ElapsedMilliseconds, result.ExitCode, Interlocked.Read(ref lines), id, operation);
            outcome = "success";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "timeout";
            logger.LogError("⏱️ {ScoreName}：{OperationName}超时，限制 {TimeoutSeconds} 秒，已用 {ElapsedMs} 毫秒，输出 {OutputLines} 行；诊断日志 {DiagnosticLog}（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, options.TimeoutSeconds, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines), logPath, id, operation);
            throw new ToolFailure("processing-timeout", $"{operationName}超过 {options.TimeoutSeconds} 秒，处理已终止。");
        }
        catch (Exception e) when (e is not OperationCanceledException and not ToolFailure)
        {
            logger.LogError(e, "💥 {ScoreName}：{OperationName}异常，用时 {ElapsedMs} 毫秒，输出 {OutputLines} 行（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines), id, operation);
            throw;
        }
        finally
        {
            progressStop.Cancel();
            if (progress is not null)
                try { await progress; } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) outcome = "cancelled";
            ProcessingTelemetry.Record(operation, outcome, stopwatch.Elapsed, peakRootMemory);
            activity?.SetTag("outcome", outcome);
            if (outcome is "error" or "timeout") activity?.SetStatus(ActivityStatusCode.Error);
        }
    }
    public async Task<PreparedDocument> PrepareDocumentAsync(Guid id, string extension, CancellationToken ct)
    {
        await Run("prepare", id, ["--extension", extension], ct, "document.py");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.JobDirectory(id), "document", "manifest.json"), ct));
        return new(manifest.RootElement.GetProperty("pageCount").GetInt32());
    }
    public async Task MergeDocumentAsync(Guid id, int version, DocumentExportSnapshot snapshot, CancellationToken ct)
    {
        var manifest = Path.Combine(files.JobDirectory(id), $"document-export-{version}.json");
        await File.WriteAllTextAsync(manifest, ScoreJson.Write(snapshot), ct);
        await Run("merge", id, ["--version", version.ToString(), "--manifest", manifest], ct, "document.py");
    }
    public async Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris)
    {
        if (!Enum.IsDefined(preprocessing)) throw new ArgumentException("Invalid preprocessing mode.");
        if (!Enum.IsDefined(engine)) throw new ArgumentException("Invalid recognition engine.");
        await Run("recognize", id, ["--extension", extension, "--preprocess", preprocessing.ToString().ToLowerInvariant(), "--engine", engine.ToString().ToLowerInvariant()], ct);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.JobDirectory(id), "raw", "notes-1.json"), ct));
        var root = document.RootElement;
        var notes = root.GetProperty("notes").EnumerateArray().Select(n =>
        {
            var pitch = new Pitch(Enum.Parse<Step>(n.GetProperty("step").GetString()!), n.GetProperty("alter").GetInt32(), n.GetProperty("octave").GetInt32());
            var b = n.GetProperty("bounds").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            return new Note(Guid.NewGuid(), n.GetProperty("interId").GetInt32(), n.GetProperty("measure").GetInt32(), n.GetProperty("part").GetInt32(),
                n.GetProperty("staff").GetInt32(), n.GetProperty("voice").GetInt32(), pitch, new(b[0], b[1], b[2], b[3]), Numbering.Convert(pitch, n.GetProperty("measure").GetInt32(), NumberingOptions.FixedC));
        }).ToArray();
        if (notes.Length == 0 || notes.Length != root.GetProperty("headCount").GetInt32()) throw new ToolFailure("invalid-note-count", "No complete note set was produced.");
        return new(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), root.GetProperty("interline").GetInt32(), root.GetProperty("engine").GetString()!, NumberingOptions.FixedC, notes,
            root.GetProperty("warnings").EnumerateArray().Select(v => v.GetString()!).ToArray(), AnnotationStyle.Default);
    }
    public async Task RenderAsync(Guid id, int version, string extension, ScoreSnapshot snapshot, CancellationToken ct)
    {
        var style = snapshot.AnnotationStyle ?? AnnotationStyle.Default;
        style.Validate();
        var styleFile = Path.Combine(files.JobDirectory(id), $"annotation-style-{version}.json");
        await File.WriteAllTextAsync(styleFile, ScoreJson.Write(style), ct);
        var labels = Path.Combine(files.JobDirectory(id), $"labels-{version}.json");
        await File.WriteAllTextAsync(labels, ScoreJson.Write(snapshot.Notes.ToDictionary(n => n.InterId.ToString(), n => new { noteId = n.Id, pitch = n.Pitch, placement = n.Placement, degree = n.Label.Degree, alteration = n.Label.Alteration, octaveDots = n.Label.OctaveDots, style = n.Style })), ct);
        await Run("render", id, ["--extension", extension, "--version", version.ToString(), "--labels", labels, "--font", options.Font, "--style", styleFile], ct);
    }
}
