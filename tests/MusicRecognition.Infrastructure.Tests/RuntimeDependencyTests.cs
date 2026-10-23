using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MusicRecognition.Infrastructure;
using Xunit;

public sealed class RuntimeDependencyTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task Missing_renderer_prevents_runtime_readiness_before_jobs_start()
    {
        var root = Environment.GetEnvironmentVariable("MUSICRECOGNITION_TEST_ROOT")
            ?? throw new InvalidOperationException("Set MUSICRECOGNITION_TEST_ROOT for real-tool integration tests.");
        var options = new RecognitionOptions
        {
            RepositoryRoot = root,
            Python = Environment.GetEnvironmentVariable("MUSICRECOGNITION_PYTHON") ?? "python",
            Pdftoppm = Path.Combine(root, ".tools", "missing-renderer", "pdftoppm.exe")
        };
        var runtime = new RecognitionRuntime(options, NullLogger<RecognitionRuntime>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ValidateAsync(CancellationToken.None));
        Assert.Contains("Poppler pdftoppm is missing", error.Message);
        Assert.Equal(HealthStatus.Unhealthy, (await runtime.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
