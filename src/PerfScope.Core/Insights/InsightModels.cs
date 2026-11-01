using System.Text.Json.Serialization;

namespace PerfScope.Core.Insights;

/// <summary>
/// 分析事件类型（稳定字符串，写入 JSONL 后不随代码重命名而变）。
/// </summary>
public static class InsightKinds
{
    /// <summary>某个应用的 CPU 持续高占用告一段落。</summary>
    public const string AppCpuEpisode = "app_cpu_episode";

    /// <summary>某个应用内存持续增长（疑似泄漏 / 缓存膨胀）告一段落。</summary>
    public const string AppMemoryGrowth = "app_memory_growth";

    /// <summary>整机 CPU 持续高负载。</summary>
    public const string SystemCpuEpisode = "system_cpu_episode";

    /// <summary>可用内存持续偏低（内存压力）。</summary>
    public const string SystemMemoryPressure = "system_memory_pressure";

    /// <summary>固定窗口的聚合摘要（默认 5 分钟一条）。</summary>
    public const string Digest = "digest";

    /// <summary>一次会话的开始（带采样参数与机器信息）。</summary>
    public const string SessionStart = "session_start";
}

/// <summary>
/// 一条分析事件。字段按需填充，序列化时自动忽略 null；同一行一个 JSON 对象，方便流式读取。
/// 所有百分比都是「整机口径」（0~100），内存单位是 MB。
/// </summary>
public sealed record InsightEvent
{
    /// <summary>事件类型，见 <see cref="InsightKinds"/>。</summary>
    public required string Kind { get; init; }

    /// <summary>事件产生时刻（UTC）。</summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>应用归并键（应用级事件才有）。</summary>
    public string? AppKey { get; init; }

    /// <summary>应用展示名。</summary>
    public string? AppName { get; init; }

    /// <summary>事件区间开始（UTC）。</summary>
    public DateTime? StartUtc { get; init; }

    /// <summary>事件区间结束（UTC）。</summary>
    public DateTime? EndUtc { get; init; }

    /// <summary>持续秒数。</summary>
    public double DurationSeconds { get; init; }

    /// <summary>区间平均 CPU%（整机口径）。</summary>
    public double AvgCpuPercent { get; init; }

    /// <summary>区间峰值 CPU%。</summary>
    public double PeakCpuPercent { get; init; }

    /// <summary>区间平均内存（MB）。</summary>
    public double AvgMemoryMb { get; init; }

    /// <summary>区间峰值内存（MB）。</summary>
    public double PeakMemoryMb { get; init; }

    /// <summary>内存增速（MB/分钟），仅内存增长事件使用。</summary>
    public double GrowthMbPerMinute { get; init; }

    /// <summary>区间内进程数峰值。</summary>
    public int ProcessCount { get; init; }

    /// <summary>参与统计的采样点数。</summary>
    public int SampleCount { get; init; }

    /// <summary>补充说明（中文，直接给人/给模型读）。</summary>
    public string? Note { get; init; }

    /// <summary>窗口内应用摘要（digest 事件使用）。</summary>
    public IReadOnlyList<InsightAppLine>? Apps { get; init; }

    /// <summary>系统级指标（digest / 系统事件使用）。</summary>
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
}

/// <summary>digest 事件里的单行应用摘要。</summary>
public sealed record InsightAppLine(
    string Key,
    string Name,
    double AvgCpuPercent,
    double PeakCpuPercent,
    double AvgMemoryMb,
    double PeakMemoryMb,
    int SampleCount);

/// <summary>一次运行的分析快照（供 UI / 报告使用）。</summary>
public sealed record InsightSnapshot(
    DateTime SessionStartUtc,
    int Frames,
    double SystemCpuAvg,
    double SystemCpuPeak,
    double SystemMemoryAvg,
    long MinAvailableBytes,
    IReadOnlyList<InsightEvent> RecentEvents,
    int PendingEventCount);
