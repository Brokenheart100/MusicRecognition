using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicRecognition.Application;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.Recognition;
using MusicRecognition.Infrastructure.Storage;
using Xunit;

public sealed class ToolFailureLoggingTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData("report")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("stale")]
    public async Task Failure_logs_keep_the_root_cause_without_exposing_it_in_client_messages(string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "recognition-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "runtime"));
        try
        {
            var script = """
                import json, pathlib, sys
                job = pathlib.Path(sys.argv[sys.argv.index('--job') + 1])
                if SCENARIO == 'report':
                    (job / 'error.json').write_text(json.dumps({'code': 'pitch-consistency-failed', 'message': 'MusicXML omitted a rhythm-assigned head; missingCount=1; interId=42'}))
                elif SCENARIO == 'malformed':
                    (job / 'error.json').write_text('{broken')
                print("ModuleNotFoundError: No module named 'diagnostic_fixture'", file=sys.stderr)
                sys.exit(7)
                """.Replace("SCENARIO", "'" + scenario + "'");
            await File.WriteAllTextAsync(Path.Combine(folder, "scripts", "runtime", "document.py"), script);
            var options = new RecognitionOptions { RepositoryRoot = folder, DataRoot = Path.Combine(folder, "jobs"),
                Python = Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? "python", TimeoutSeconds = 10 };
            var files = new FileStorage(options, NullLogger<FileStorage>.Instance);
            var id = Guid.NewGuid(); Directory.CreateDirectory(files.JobDirectory(id));
            if (scenario == "stale") await File.WriteAllTextAsync(Path.Combine(files.JobDirectory(id), "error.json"), "{\"code\":\"stale-error\",\"message\":\"obsolete cause\"}");
            var logger = new RecordingLogger<ToolEngine>();
            var parent = new ScoreJob { Id = Guid.NewGuid(), FileName = "测试钢琴谱.pdf", PageCount = 6 };
            var child = new ScoreJob { Id = id, ParentId = parent.Id, PageNumber = 2, FileName = "Page 2.pdf", PagesPrepared = true };
            var engine = new ToolEngine(options, files, logger, new DiagnosticScoreRepository(parent, child));
            var error = await Assert.ThrowsAsync<ToolFailure>(() => engine.PrepareDocumentAsync(id, ".pdf", CancellationToken.None));
            Assert.Equal(scenario == "report" ? "pitch-consistency-failed" : "tool-failed", error.Code);
            Assert.Contains(scenario == "report" ? "interId=42" : "ModuleNotFoundError", error.DiagnosticReason!);
            Assert.DoesNotContain("diagnostic_fixture", error.Message);
            Assert.DoesNotContain("interId=42", error.Message);
            var entry = Assert.Single(logger.Entries, entry => entry.ContainsKey("FailureReason"));
            Assert.Equal(7, entry["ExitCode"]);
            Assert.Equal(id, entry["ScoreId"]);
            Assert.Equal("prepare", entry["Operation"]);
            Assert.Equal("测试钢琴谱.pdf - 第2页", entry["ScoreName"]);
            Assert.Equal("PDF 拆页", entry["OperationName"]);
            Assert.Equal(error.DiagnosticReason, entry["FailureReason"]);
            Assert.True(File.Exists((string)entry["DiagnosticLog"]!));
            Assert.Contains("diagnostic_fixture", (string)entry["StderrTail"]!);
            if (scenario == "malformed") Assert.StartsWith("Json", (string)entry["DiagnosticReportIssue"]!);
        }
        finally
        {
            if (Path.GetFullPath(folder).StartsWith(Path.Combine(Path.GetTempPath(), "recognition-diagnostics-"), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values) Entries.Enqueue(values.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }
}
