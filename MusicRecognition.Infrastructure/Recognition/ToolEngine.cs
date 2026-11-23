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

// 识别引擎适配器：把“识谱/导出”翻译成一次 Python 脚本调用，再把结果读回 C# 对象。
// 上层只认 IRecognitionEngine 接口，不关心脚本怎么跑、参数怎么拼、失败怎么包装。
// 它把 Audiveris、homr 这些外部工具的脏活挡在基础设施层，Domain 只处理音高与乐理。
// 每次调用都留一份日志文件，任务失败时能追溯到具体哪一行输出。
public sealed class ToolEngine(RecognitionOptions options, IScoreStorage files, ILogger<ToolEngine> logger, IScoreRepository repository) : IRecognitionEngine
{
    // 所有识谱操作的公共执行器：拼参数、起 Python 进程、写日志、处理超时和失败。
    // operation 是动作名（prepare/recognize/render/merge），其余参数由调用方追加。
    internal async Task Run(string operation, Guid id, string[] args, CancellationToken ct, string script = "pipeline.py", string? jobDirectory = null)
    {
        // 取任务信息用于日志上下文；任务还没落库时用临时对象，日志不会因此中断。
        var job = await repository.FindJobAsync(id, ct);
        var context = await ScoreDiagnostics.ResolveAsync(repository, job ?? new ScoreJob { Id = id }, ct);
        // 把任务名等信息塞进日志作用域，这之后的所有日志都自动带上。
        using var scope = logger.BeginScope(context.Scope());
        var name = context.ScoreName;
        // 把内部动作名翻译成中文（如 recognize → 识谱），日志里给人看。
        var operationName = ScoreDiagnostics.Operation(operation);
        // 每次执行单独一个日志文件，文件名带时间戳，失败时能精确对上这一次运行。
        var dir = jobDirectory ?? files.JobDirectory(id); var logPath = Path.Combine(dir, $"{operation}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
        // 把外部取消和超时合成一个令牌，任一触发都会终止子进程。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 总超时取配置值（默认 300 秒）；到点没结束就强制终止，防止任务永远卡住。
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        // 统一参数格式：脚本路径、动作名、仓库根、任务目录，再加各操作自己的参数。
        string[] arguments = ["-u", Path.Combine(options.RepositoryRoot, "scripts", "runtime", script),
            operation, "--root", options.RepositoryRoot, "--job", dir, .. args];
        // 子进程输出同时落到日志文件；两条管道回调可能并发，下面用信号量保证串行写。
        await using var log = new StreamWriter(logPath);
        // 每写一行立刻落盘：进程被杀时日志也尽量完整。
        log.AutoFlush = true;
        // 标准输出和标准错误来自两个回调线程，用信号量串行写文件；lines 统计总行数。
        using var logGate = new SemaphoreSlim(1); long lines = 0;
        var stderrTail = new Queue<string>();
        // 单行超过 4000 字符就截断，防止某一行异常输出撑爆日志。
        async Task WriteLine(string line, bool isError = false)
        {
            var bounded = line[..Math.Min(line.Length, 4000)];
            // 只保留最近 8 行标准错误，拼进失败摘要；完整内容在日志文件里。
            if (isError) { if (stderrTail.Count == 8) stderrTail.Dequeue(); stderrTail.Enqueue(bounded); }
            // 整个进程最多保留 10000 行日志，超过就丢弃，避免日志刷满磁盘。
            if (Interlocked.Increment(ref lines) > 10000) return;
            await logGate.WaitAsync();
            try { await log.WriteLineAsync(bounded); } finally { logGate.Release(); }
        }
        // 启动 Python 并把三个工具路径通过环境变量传给脚本，脚本不必自己猜位置。
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
        // 记录耗时，结束写进日志和指标。
        var stopwatch = Stopwatch.StartNew();
        // 开启一个 OpenTelemetry 追踪片段（span），把这次操作接进监控。
        using var activity = ProcessingTelemetry.Activities.StartActivity("score." + operation);
        activity?.SetTag("operation", operation);
        // outcome 记录结果（success/timeout/error/cancelled），peakRootMemory 记录内存峰值。
        var outcome = "error"; long peakRootMemory = 0;
        using var progressStop = new CancellationTokenSource();
        // 后台进度线程：每秒记一次子进程内存峰值，每 20 秒打一条“仍在运行”的日志。
        async Task ReportProgress(int processId)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var ticks = 0;
            while (await timer.WaitForNextTickAsync(progressStop.Token))
            {
                try
                {
                    // 读不到进程（已退出或权限不足）就忽略，下一轮再试。
                    using var process = Process.GetProcessById(processId);
                    peakRootMemory = Math.Max(peakRootMemory, process.WorkingSet64);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                // 20 是按每秒一次算的：每 20 秒才打一条进度日志，避免长任务刷满日志。
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
            // 执行前先删掉上一次留下的 error.json，防止旧失败信息被误用。
            File.Delete(Path.Combine(dir, "error.json"));
            // 统一走 ToolCommand：取消、超时和进程树清理逻辑都在那里。
            var result = await ToolCommand.ExecuteAsync(command, timeout.Token, logger, processId =>
            {
                logger.LogInformation("▶️ {ScoreName}：开始{OperationName}，进程 {ProcessId}，超时 {TimeoutSeconds} 秒（任务 {ScoreId}，操作 {Operation}）", name, operationName, processId, options.TimeoutSeconds, id, operation);
                progress = ReportProgress(processId);
            });
            // 非 0 退出：先找诊断报告 error.json，拿错误码和原因。
            if (result.ExitCode != 0)
            {
                // error.json 是 Python 脚本失败时写下的结构化诊断报告。
                var errorFile = Path.Combine(dir, "error.json");
                // 默认错误码；报告里给了更具体的就用报告里的。
                var code = "tool-failed";
                var stderrSummary = string.Join("\n", stderrTail);
                if (stderrSummary.Length > 4000) stderrSummary = stderrSummary[^4000..];
                var reason = string.IsNullOrWhiteSpace(stderrSummary) ? "工具退出时未提供诊断报告或标准错误输出。" : stderrSummary;
                // 记录报告本身的问题（缺失、过大、格式错），只写日志，不返回给用户。
                string? reportIssue = null;
                try
                {
                    if (File.Exists(errorFile))
                    {
                        // 报告超过 64 KiB 视为异常，改用标准错误，避免解析超大 JSON。
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
                // 把错误码翻译成用户看得懂的中文提示，前端直接展示这段文字。
                var message = code switch
                {
                    // 30 页是当前识谱流程的上限，超过会让工具链超时或内存过高。
                    "unsupported-page-count" => "PDF 页数必须为 1 至 30 页。",
                    "encrypted-pdf" => "暂不支持加密 PDF，请上传未加密的文件。",
                    "pdf-renderer-unavailable" => "服务器 PDF 渲染工具不可用，请向管理员提供任务 ID。",
                    // homr 是另一套识别引擎，需要独立的 Python 环境和模型。
                    "homr-not-provisioned" => "homr 独立环境或模型尚未准备，请运行 scripts/setup/setup-homr.ps1，或选择 Audiveris。",
                    "homr-coordinate-unavailable" => "homr 未能提供可对应原图的音符坐标，已保留诊断结果；请使用 Audiveris 或提供更完整的谱面。",
                    "pitch-consistency-failed" => "识谱结果的音高、声部或符头坐标一致性校验未通过，尚未生成标注。请使用任务 ID 查看具体诊断日志。",
                    "octave-evidence-ambiguous" => "无法可靠确定八度线的范围或所属谱表，尚未生成标注。请使用任务 ID 查看八度线诊断记录。",
                    _ => $"{operationName}失败，请使用任务 ID {id} 查看服务器诊断日志。"
                };
                logger.LogWarning("❌ {ScoreName}：{OperationName}失败，退出码 {ExitCode}，错误码 {ErrorCode}，原因 {FailureReason}；诊断日志 {DiagnosticLog}；标准错误末尾 {StderrTail}；诊断报告问题 {DiagnosticReportIssue}（任务 {ScoreId}，操作 {Operation}）",
                    name, operationName, result.ExitCode, code, reason, logPath, stderrSummary, reportIssue, id, operation);
                // 抛出统一异常：上层据此把任务标成 Failed 并记录错误码。
                throw new ToolFailure(code, message, reason);
            }
            logger.LogInformation("⏹️ {ScoreName}：{OperationName}完成，用时 {ElapsedMs} 毫秒，退出码 {ExitCode}，输出 {OutputLines} 行（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, stopwatch.ElapsedMilliseconds, result.ExitCode, Interlocked.Read(ref lines), id, operation);
            outcome = "success";
        }
        // 只有“超时”才走这里（外部主动取消不算）；记日志并抛可展示的超时错误。
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "timeout";
            logger.LogError("⏱️ {ScoreName}：{OperationName}超时，限制 {TimeoutSeconds} 秒，已用 {ElapsedMs} 毫秒，输出 {OutputLines} 行；诊断日志 {DiagnosticLog}（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, options.TimeoutSeconds, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines), logPath, id, operation);
            throw new ToolFailure("processing-timeout", $"{operationName}超过 {options.TimeoutSeconds} 秒，处理已终止。");
        }
        // 其它意外异常：记日志后原样抛出，细节只进服务器日志。
        catch (Exception e) when (e is not OperationCanceledException and not ToolFailure)
        {
            logger.LogError(e, "💥 {ScoreName}：{OperationName}异常，用时 {ElapsedMs} 毫秒，输出 {OutputLines} 行（任务 {ScoreId}，操作 {Operation}）",
                name, operationName, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref lines), id, operation);
            throw;
        }
        finally
        {
            // 收尾：先停进度线程，再记录指标和追踪结果。
            progressStop.Cancel();
            if (progress is not null)
                try { await progress; } catch (OperationCanceledException) { }
            // 外部要求取消时覆盖结果标记，指标里区分“取消”和“失败”。
            if (ct.IsCancellationRequested) outcome = "cancelled";
            ProcessingTelemetry.Record(operation, outcome, stopwatch.Elapsed, peakRootMemory);
            activity?.SetTag("outcome", outcome);
            // 只把失败和超时标成追踪错误；取消属于正常操作。
            if (outcome is "error" or "timeout") activity?.SetStatus(ActivityStatusCode.Error);
        }
    }
    // 第一步：把上传的 PDF/图片拆成逐页图片，并读回页数。
    public async Task<PreparedDocument> PrepareDocumentAsync(Guid id, string extension, CancellationToken ct)
    {
        // 调 document.py 的 prepare 动作，产物（每页图片和清单）写在任务目录。
        await Run("prepare", id, ["--extension", extension], ct, "document.py");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.JobDirectory(id), "document", "manifest.json"), ct));
        // manifest.json 是脚本产出的页面清单，这里只取页数告诉上层。
        return new(manifest.RootElement.GetProperty("pageCount").GetInt32());
    }
    // 多页文档导出：先把各页标注快照写成清单，再让脚本合并成整份 PDF。
    public async Task MergeDocumentAsync(Guid id, int version, DocumentExportSnapshot snapshot, CancellationToken ct)
    {
        var manifest = Path.Combine(files.JobDirectory(id), $"document-export-{version}.json");
        // 清单先写到任务目录，脚本按路径读，避免命令行参数过长。
        await File.WriteAllTextAsync(manifest, ScoreJson.Write(snapshot), ct);
        await Run("merge", id, ["--version", version.ToString(), "--manifest", manifest], ct, "document.py");
    }
    // 识谱主操作：校验参数 → 调引擎 → 把原始 JSON 转成领域对象（Note/Pitch/Label）。
    public async Task<ScoreSnapshot> RecognizeAsync(Guid id, string extension, CancellationToken ct, PreprocessingMode preprocessing = PreprocessingMode.None, RecognitionEngineKind engine = RecognitionEngineKind.Audiveris, string? rawDirectory = null)
    {
        // 枚举值先校验，防止调用方传入未定义的预处理模式或引擎。
        if (!Enum.IsDefined(preprocessing)) throw new ArgumentException("Invalid preprocessing mode.");
        if (!Enum.IsDefined(engine)) throw new ArgumentException("Invalid recognition engine.");
        // 预处理模式和引擎名转小写传给 Python，与脚本参数约定一致。
        // rawDirectory 只在区域重识别时传入：强制把整页识别跑到临时目录，不覆盖 canonical raw。
        var arguments = new List<string> { "--extension", extension, "--preprocess", preprocessing.ToString().ToLowerInvariant(), "--engine", engine.ToString().ToLowerInvariant() };
        if (rawDirectory is not null) { arguments.Add("--raw"); arguments.Add(rawDirectory); }
        await Run("recognize", id, [.. arguments], ct);
        // 读引擎产出的原始 JSON；区域识别读临时目录，普通识别读任务目录的 raw/notes-1.json。
        var raw = rawDirectory ?? Path.Combine(files.JobDirectory(id), "raw");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(raw, "notes-1.json"), ct));
        var root = document.RootElement;
        // 逐条把 JSON 音符转成 Note：音高、坐标、所属小节/声部组/五线谱/声部。
        var notes = root.GetProperty("notes").EnumerateArray().Select(n =>
        {
            // Pitch = 音名 + 升降号（alter）+ 八度，是乐理层认识的音高表示。
            var pitch = new Pitch(Enum.Parse<Step>(n.GetProperty("step").GetString()!), n.GetProperty("alter").GetInt32(), n.GetProperty("octave").GetInt32());
            // bounds 是音符在原图上的外接矩形（x、y、宽、高）。
            var b = n.GetProperty("bounds").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            // 顺带算出简谱数字（label）；默认按 C 调（FixedC）换算。
            var chordId = n.TryGetProperty("chordId", out var chord) && chord.ValueKind == JsonValueKind.Number ? chord.GetInt32() : (int?)null;
            var staffInPart = n.TryGetProperty("staffInPart", out var part) && part.ValueKind == JsonValueKind.Number ? part.GetInt32() : 0;
            return new Note(Guid.NewGuid(), n.GetProperty("interId").GetInt32(), n.GetProperty("measure").GetInt32(), n.GetProperty("part").GetInt32(),
                n.GetProperty("staff").GetInt32(), n.GetProperty("voice").GetInt32(), pitch, new(b[0], b[1], b[2], b[3]), Numbering.Convert(pitch, n.GetProperty("measure").GetInt32(), NumberingOptions.FixedC),
                // system 是原始谱表组编号；chordId/staffInPart 供区域重识别后的自动排版和音高对账。
                System: n.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Number ? system.GetInt32() : null,
                ChordId: chordId, StaffInPart: staffInPart);
        }).ToArray();
        // 一个音符都没识别出来，或数量与谱面符头数不一致，都算失败，宁可报错也不给错谱。
        if (notes.Length == 0 || notes.Length != root.GetProperty("headCount").GetInt32()) throw new ToolFailure("invalid-note-count", "No complete note set was produced.");
        // 组装快照：页面宽高、五线谱间距、引擎名、默认标注样式和警告列表。
        return new(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), root.GetProperty("interline").GetInt32(), root.GetProperty("engine").GetString()!, NumberingOptions.FixedC, notes,
            root.GetProperty("warnings").EnumerateArray().Select(v => v.GetString()!).ToArray(), AnnotationStyle.Default);
    }
    // 导出操作：把每个音符的简谱数字和位置写成 JSON，再让脚本画到原谱空白处。
    public async Task RenderAsync(Guid id, int version, string extension, ScoreSnapshot snapshot, CancellationToken ct)
    {
        // 没指定样式就用默认样式；先校验，颜色或字号不合法就提前失败。
        var style = snapshot.AnnotationStyle ?? AnnotationStyle.Default;
        style.Validate();
        // 样式单独存一个文件，脚本读文件比读超长命令行参数稳妥。
        var styleFile = Path.Combine(files.JobDirectory(id), $"annotation-style-{version}.json");
        await File.WriteAllTextAsync(styleFile, ScoreJson.Write(style), ct);
        // 区域重识别会替换 canonical raw 中的部分符头；这些旧符头不在快照里，
        // 渲染脚本必须跳过它们，否则 labels 和 raw 的编号集合对不上。
        var removed = Array.Empty<int>();
        var rawNotes = Path.Combine(files.JobDirectory(id), "raw", "notes-1.json");
        if (File.Exists(rawNotes))
        {
            using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(rawNotes, ct));
            var kept = snapshot.Notes.Select(n => n.InterId).ToHashSet();
            removed = raw.RootElement.GetProperty("notes").EnumerateArray()
                .Select(n => n.GetProperty("interId").GetInt32())
                .Where(interId => !kept.Contains(interId)).ToArray();
        }
        var removedFile = Path.Combine(files.JobDirectory(id), $"removed-{version}.json");
        if (removed.Length > 0) await File.WriteAllTextAsync(removedFile, ScoreJson.Write(removed), ct);
        // 标注清单：每个音符的简谱数字、位置和样式，脚本据此决定每个数字写在哪。
        var labels = Path.Combine(files.JobDirectory(id), $"labels-{version}.json");
        await File.WriteAllTextAsync(labels, ScoreJson.Write(snapshot.Notes.ToDictionary(n => n.InterId.ToString(), n => new
        {
            noteId = n.Id, pitch = n.Pitch, placement = n.Placement, degree = n.Label.Degree, alteration = n.Label.Alteration,
            // system 是谱表组编号、manual 标记手工补录、region 标记区域重识别，自动排版时靠它们还原位置。
            octaveDots = n.Label.OctaveDots, style = n.Style, system = n.System, manual = n.Manual,
            region = n.RegionRecognized, chordId = n.ChordId, staffInPart = n.StaffInPart,
            bounds = new[] { n.Bounds.X, n.Bounds.Y, n.Bounds.Width, n.Bounds.Height },
            measure = n.Measure, part = n.Part, staff = n.Staff, voice = n.Voice
        })), ct);
        // 最后一步：调脚本渲染，产出带简谱标注的 PDF。
        var renderArguments = new List<string> { "--extension", extension, "--version", version.ToString(), "--labels", labels, "--font", options.Font, "--style", styleFile };
        if (removed.Length > 0) { renderArguments.Add("--removed"); renderArguments.Add(removedFile); }
        await Run("render", id, [.. renderArguments], ct);
    }
}
