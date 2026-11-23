using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MusicRecognition.Infrastructure.Observability;

public static class ProcessingTelemetry
{
    public const string Name = "MusicRecognition.Processing";
    private static readonly Meter Meter = new(Name);
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("musicrecognition.tool.operations", "{operation}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("musicrecognition.tool.duration", "s");
    private static readonly Histogram<long> RootMemory = Meter.CreateHistogram<long>("musicrecognition.tool.root_process.peak_memory", "By");

    public static void Record(string operation, string outcome, TimeSpan elapsed, long peakRootBytes)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Completed.Add(1, tags); Duration.Record(elapsed.TotalSeconds, tags);
        if (peakRootBytes > 0) RootMemory.Record(peakRootBytes, tags);
    }
}
