using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MusicRecognition.Infrastructure.Observability;

// 进程内部的可观测性入口：用 .NET 的 Meter/ActivitySource 记录每次工具操作的次数、耗时和内存。
// 这些指标由 OpenTelemetry 收集后导出到监控面板，用来回答“识谱是不是变慢了、失败变多了”。
// 名字固定为 MusicRecognition.Processing，ServiceDefaults 里按这个名字订阅。
public static class ProcessingTelemetry
{
    // 指标和追踪共用的名字，ServiceDefaults 里按这个名字订阅。
    public const string Name = "MusicRecognition.Processing";
    // Meter 管指标（计数、耗时），ActivitySource 管追踪（一次操作的耗时链路）。
    private static readonly Meter Meter = new(Name);
    public static readonly ActivitySource Activities = new(Name);
    // 计数器：每完成一次工具操作加一；"{operation}" 是单位占位，表示“次”。
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("musicrecognition.tool.operations", "{operation}");
    // 直方图：记录操作耗时（秒），方便算平均值和 P95。
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("musicrecognition.tool.duration", "s");
    // 直方图：记录外部工具主进程的内存峰值（字节）。
    private static readonly Histogram<long> RootMemory = Meter.CreateHistogram<long>("musicrecognition.tool.root_process.peak_memory", "By");

    // 汇总记录一次操作结果；内存为 0 表示没采到，就不写入统计。
    public static void Record(string operation, string outcome, TimeSpan elapsed, long peakRootBytes)
    {
        // operation 是 prepare/recognize/render 等操作名，outcome 是 success/timeout/error。
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Completed.Add(1, tags); Duration.Record(elapsed.TotalSeconds, tags);
        if (peakRootBytes > 0) RootMemory.Record(peakRootBytes, tags);
    }
}
