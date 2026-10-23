using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MusicRecognition.Application;
using MusicRecognition.Domain;

namespace MusicRecognition.Infrastructure;

public sealed class ToolFailure(string code, string message, string? diagnosticReason = null) : Exception(message)
{
    public string Code { get; } = code;
    // Internal diagnostics are logged separately from the safe client-facing Message.
    public string? DiagnosticReason { get; } = diagnosticReason;
}

public sealed class ToolEngine(RecognitionOptions options, IScoreStorage files, ILogger<ToolEngine> logger) : IRecognitionEngine
{
    private async Task Run(string operation, Guid id, string[] args, CancellationToken ct, string script = "pipeline.py")
    {
        var dir = files.JobDirectory(id); var logPath = Path.Combine(dir, $"{operation}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var start = new ProcessStartInfo(options.Python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = options.RepositoryRoot, CreateNoWindow = true };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(options.RepositoryRoot, "scripts", "runtime", script));
        start.ArgumentList.Add(operation); start.ArgumentList.Add("--root"); start.ArgumentList.Add(options.RepositoryRoot);
        start.ArgumentList.Add("--job"); start.ArgumentList.Add(dir);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["MUSICRECOGNITION_PDFTOPPM"] = options.PdftoppmPath;
        using var process = new Process { StartInfo = start };
        await using var log = new StreamWriter(logPath);
        log.AutoFlush = true;
        using var logGate = new SemaphoreSlim(1); long lines = 0;
        var stderrTail = new Queue<string>();
        async Task Drain(StreamReader reader, bool isError = false)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var bounded = line[..Math.Min(line.Length, 4000)];
                if (isError) { if (stderrTail.Count == 8) stderrTail.Dequeue(); stderrTail.Enqueue(bounded); }
                if (Interlocked.Increment(ref lines) > 10000) continue;
                await logGate.WaitAsync();
                try { await log.WriteLineAsync(bounded); } finally { logGate.Release(); }
            }
        }
        var stopwatch = Stopwatch.StartNew();
        using var progressStop = new CancellationTokenSource();
        async Task ReportProgress()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(progressStop.Token))
                logger.LogInformation("⏳ Score tool operation still running {ScoreId} operation {Operation} elapsedSeconds {ElapsedSeconds} outputLines {OutputLines}",
                    id, operation, (long)stopwatch.Elapsed.TotalSeconds, Interlocked.Read(ref lines));
        }
        Task? progress = null;
        try
        {
            logger.LogDebug("🔧 Launching {Script} operation {Operation} for {ScoreId} ({ArgumentCount} arguments)", script, operation, id, start.ArgumentList.Count);
            // A failure before Python imports/initialization must not inherit an earlier attempt's report.
            File.Delete(Path.Combine(dir, "error.json"));
            process.Start();
            logger.LogInformation("▶️ Score tool operation started {ScoreId} operation {Operation} processId {ProcessId} timeoutSeconds {TimeoutSeconds}", id, operation, process.Id, options.TimeoutSeconds);
            progress = ReportProgress();
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError, isError: true);
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr); }
            if (process.ExitCode != 0)
            {
                var errorFile = Path.Combine(dir, "error.json");
                var code = "tool-failed";
                var stderrSummary = string.Join("\n", stderrTail);
                if (stderrSummary.Length > 4000) stderrSummary = stderrSummary[^4000..];
                var reason = string.IsNullOrWhiteSpace(stderrSummary) ? "Tool exited without a diagnostic report or stderr output." : stderrSummary;
                string? reportIssue = null;
                try
                {
                    if (File.Exists(errorFile))
                    {
                        if (new FileInfo(errorFile).Length > 65536) reportIssue = "Diagnostic report exceeded 64 KiB; using stderr.";
                        else
                        {
                            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(errorFile, ct));
                            var root = report.RootElement;
                            if (root.ValueKind != JsonValueKind.Object) reportIssue = "Diagnostic report was not an object; using stderr.";
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
                    "unsupported-page-count" => "PDF must contain between 1 and 30 pages.",
                    "encrypted-pdf" => "Password-protected PDFs are not supported. Import an unencrypted PDF.",
                    "pdf-renderer-unavailable" => "The server PDF renderer is unavailable. Contact the administrator using this score ID.",
                    "pitch-consistency-failed" => "识谱结果的音高、声部或符头坐标一致性校验未通过，尚未生成标注。请使用任务 ID 查看具体诊断日志。",
                    _ => $"{operation} failed; consult the protected server logs for score {id}."
                };
                logger.LogWarning("❌ Score tool operation exited with code {ExitCode} {ScoreId} operation {Operation} errorCode {ErrorCode} failureReason {FailureReason} diagnosticLog {DiagnosticLog} stderrTail {StderrTail} reportIssue {DiagnosticReportIssue}",
                    process.ExitCode, id, operation, code, reason, logPath, stderrSummary, reportIssue);
                throw new ToolFailure(code, message, reason);
            }
            logger.LogInformation("⏹️ Score tool operation completed {ScoreId} operation {Operation} exitCode {ExitCode} elapsedMs {ElapsedMs} outputLines {OutputLines}",
                id, operation, process.ExitCode, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("⏱️ Score tool operation timed out {ScoreId} operation {Operation} timeoutSeconds {TimeoutSeconds} elapsedMs {ElapsedMs} outputLines {OutputLines} diagnosticLog {DiagnosticLog}",
                id, operation, options.TimeoutSeconds, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines), logPath);
            throw new ToolFailure("processing-timeout", $"{operation} exceeded {options.TimeoutSeconds} seconds.");
        }
        catch (Exception e) when (e is not OperationCanceledException and not ToolFailure)
        {
            logger.LogError(e, "💥 Score tool operation failed {ScoreId} operation {Operation} elapsedMs {ElapsedMs} outputLines {OutputLines}",
                id, operation, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines));
            throw;
        }
        finally
        {
            progressStop.Cancel();
            if (progress is not null)
                try { await progress; } catch (OperationCanceledException) { }
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
    public async Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None)
    {
        if (!Enum.IsDefined(preprocessing)) throw new ArgumentException("Invalid preprocessing mode.");
        await Run("recognize", id, ["--extension", extension, "--preprocess", preprocessing.ToString().ToLowerInvariant()], ct);
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
            root.GetProperty("warnings").EnumerateArray().Select(v => v.GetString()!).ToArray());
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
