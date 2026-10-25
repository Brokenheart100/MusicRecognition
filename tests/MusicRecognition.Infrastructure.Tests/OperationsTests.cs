using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MusicRecognition.Infrastructure;
using Xunit;

public sealed class OperationsTests
{
    [Fact]
    public async Task Storage_probe_requires_writable_directory_and_respects_space_threshold()
    {
        var directory = Path.Combine(Path.GetTempPath(), "musicrecognition-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var health = new DataStorageHealth(new() { DataRoot = directory, MinimumFreeSpaceMiB = 0 });
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
            Assert.Empty(Directory.GetFiles(directory));
            var file = Path.Combine(directory, "file"); await File.WriteAllTextAsync(file,"x");
            var invalid = new DataStorageHealth(new() { DataRoot = file });
            Assert.Equal(HealthStatus.Unhealthy, (await invalid.CheckHealthAsync(new())).Status);
            var full = new DataStorageHealth(new() { DataRoot = directory, MinimumFreeSpaceMiB = int.MaxValue });
            Assert.Equal(HealthStatus.Unhealthy, (await full.CheckHealthAsync(new())).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Processing_measurements_have_fixed_operation_outcome_dimensions()
    {
        var measurements = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument,l) => { if (instrument.Meter.Name==ProcessingTelemetry.Name) l.EnableMeasurementEvents(instrument); };
        void Record<T>(Instrument instrument,T value,ReadOnlySpan<KeyValuePair<string, object?>> tags,object? state)
        { if (tags.ToArray().Any(t=>Equals(t.Value,"test-operations"))) measurements.Add(instrument.Name+":"+string.Join(",",tags.ToArray().Select(t=>t.Key))); }
        listener.SetMeasurementEventCallback<long>(Record); listener.SetMeasurementEventCallback<double>(Record); listener.Start();
        ProcessingTelemetry.Record("test-operations","success",TimeSpan.FromSeconds(2),1024);
        Assert.Equal(3, measurements.Count);
        Assert.All(measurements, item => Assert.EndsWith(":operation,outcome",item));
    }
}
