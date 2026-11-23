using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MusicRecognition.Infrastructure.Configuration;
using MusicRecognition.Infrastructure.Recognition;
using MusicRecognition.Infrastructure.Storage;
using Xunit;

public sealed class ToolProcessTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task Arguments_environment_and_utf8_survive_paths_with_spaces()
    {
        using var fixture = new ProcessFixture();
        await fixture.Script("document.py", """
            import json, os, pathlib, sys
            job = pathlib.Path(sys.argv[sys.argv.index('--job') + 1])
            assert pathlib.Path.cwd() == pathlib.Path(sys.argv[sys.argv.index('--root') + 1])
            assert os.environ['PYTHONIOENCODING'] == 'utf-8'
            assert os.environ['MUSICRECOGNITION_PDFTOPPM'].endswith('renderer with spaces')
            assert sys.argv[sys.argv.index('--extension') + 1] == '中文 "quoted" 参数'
            print('中文标准输出')
            print('中文标准错误', file=sys.stderr)
            (job / 'document').mkdir()
            (job / 'document' / 'manifest.json').write_text(json.dumps({'pageCount': 2}))
            """);
        var result = await fixture.Engine.PrepareDocumentAsync(fixture.Id, "中文 \"quoted\" 参数", CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        var log = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fixture.Job, "prepare-*.log")));
        Assert.Contains("中文标准输出", log);
        Assert.Contains("中文标准错误", log);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Output_limit_keeps_draining_and_preserves_the_final_failure_reason()
    {
        using var fixture = new ProcessFixture();
        await fixture.Script("document.py", """
            import sys
            print('x' * 5000)
            for i in range(12000):
                print('stdout ' + str(i))
                print('stderr ' + str(i), file=sys.stderr)
            print('最后的失败原因：fixture-error', file=sys.stderr)
            sys.exit(23)
            """);
        var error = await Assert.ThrowsAsync<ToolFailure>(() => fixture.Engine.PrepareDocumentAsync(fixture.Id, ".pdf", CancellationToken.None));
        Assert.Equal("tool-failed", error.Code);
        Assert.Contains("最后的失败原因：fixture-error", error.DiagnosticReason!);
        Assert.DoesNotContain("fixture-error", error.Message);
        var lines = await File.ReadAllLinesAsync(Assert.Single(Directory.GetFiles(fixture.Job, "prepare-*.log")));
        Assert.Equal(10000, lines.Length);
        Assert.All(lines, line => Assert.InRange(line.Length, 0, 4000));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_cancellation_terminate_children_and_grandchildren(bool cancel)
    {
        using var fixture = new ProcessFixture();
        await fixture.Script("document.py", TreeScript);
        using var stop = new CancellationTokenSource();
        var task = fixture.Engine.PrepareDocumentAsync(fixture.Id, ".pdf", stop.Token);
        await VerifyTreeTermination(fixture, task, stop, cancel, isPreflight: false);
        Assert.False(Directory.Exists(Path.Combine(fixture.Job, "raw")));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Preflight_cancellation_terminates_the_tree_and_stays_unhealthy()
    {
        using var fixture = new ProcessFixture();
        await fixture.Script("preflight.py", TreeScript);
        using var stop = new CancellationTokenSource();
        var runtime = new RecognitionRuntime(fixture.Options, NullLogger<RecognitionRuntime>.Instance);
        await VerifyTreeTermination(fixture, runtime.ValidateAsync(stop.Token), stop, cancel: true, isPreflight: true);
        Assert.Equal(HealthStatus.Unhealthy, (await runtime.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Preflight_failure_retains_bounded_utf8_diagnostics()
    {
        using var fixture = new ProcessFixture();
        await fixture.Script("preflight.py", """
            import sys
            for i in range(1000):
                print('x' * 100, file=sys.stderr)
            print('中文依赖失败', file=sys.stderr)
            sys.exit(3)
            """);
        var runtime = new RecognitionRuntime(fixture.Options, NullLogger<RecognitionRuntime>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ValidateAsync(CancellationToken.None));
        Assert.Contains("中文依赖失败", error.Message);
        Assert.InRange(error.Message.Length, 1, 16100);
        Assert.Equal(HealthStatus.Unhealthy, (await runtime.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    private static async Task VerifyTreeTermination(ProcessFixture fixture, Task task, CancellationTokenSource stop, bool cancel, bool isPreflight)
    {
        int[] ids = [];
        try
        {
            var ready = Path.Combine(fixture.Root, "process-tree.json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(ready)) await Task.Delay(50, deadline.Token);
            ids = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(ready))!;
            Assert.Equal(3, ids.Length);
            Assert.All(ids, id => Assert.False(Exited(id)));
            if (cancel) stop.Cancel();
            if (isPreflight || cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
            else
            {
                var error = await Assert.ThrowsAsync<ToolFailure>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal("processing-timeout", error.Code);
            }
            // Kill(entireProcessTree) can return before the OS finishes reaping descendants.
            var exitDeadline = Stopwatch.StartNew();
            while (ids.Any(id => !Exited(id)) && exitDeadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            Assert.All(ids, id => Assert.True(Exited(id), $"Process {id} survived; tree={string.Join(',', ids)}"));
        }
        finally
        {
            stop.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            foreach (var id in ids.Where(id => !Exited(id)))
                try { using var process = Process.GetProcessById(id); process.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
        }
    }

    private static bool Exited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private const string TreeScript = """
        import json, os, pathlib, subprocess, sys, time
        child_code = '''import json, os, pathlib, subprocess, sys, time
        grandchild = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])
        pathlib.Path('descendants.json').write_text(json.dumps([os.getpid(), grandchild.pid]))
        time.sleep(120)
        '''
        child = subprocess.Popen([sys.executable, '-c', child_code])
        descendants = pathlib.Path('descendants.json')
        while not descendants.exists():
            time.sleep(0.02)
        ids = [os.getpid()] + json.loads(descendants.read_text())
        # Publish readiness atomically so the test never reads a partial JSON file.
        pathlib.Path('process-tree.tmp').write_text(json.dumps(ids))
        pathlib.Path('process-tree.tmp').replace('process-tree.json')
        time.sleep(120)
        """;

    private sealed class ProcessFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cliwrap 中文 (test)-" + Guid.NewGuid().ToString("N"));
        public Guid Id { get; } = Guid.NewGuid();
        public RecognitionOptions Options { get; }
        public ToolEngine Engine { get; }
        public string Job { get; }
        public ProcessFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "scripts", "runtime"));
            Options = new RecognitionOptions
            {
                RepositoryRoot = Root, DataRoot = Path.Combine(Root, "jobs"), TimeoutSeconds = 10,
                Python = Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? "python",
                Pdftoppm = Path.Combine(Root, "renderer with spaces")
            };
            var files = new FileStorage(Options, NullLogger<FileStorage>.Instance);
            Job = files.JobDirectory(Id); Directory.CreateDirectory(Job);
            Engine = new ToolEngine(Options, files, NullLogger<ToolEngine>.Instance, new DiagnosticScoreRepository());
        }
        public Task Script(string name, string content) => File.WriteAllTextAsync(Path.Combine(Root, "scripts", "runtime", name), content);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
