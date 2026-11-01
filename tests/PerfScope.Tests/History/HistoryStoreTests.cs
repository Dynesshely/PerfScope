using System.Diagnostics;
using PerfScope.Core.Abstractions;
using PerfScope.Core.History;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;
using PerfScope.Tests.Support;
using Xunit.Abstractions;

namespace PerfScope.Tests.History;

/// <summary><see cref="HistoryStore"/> 测试：趋势环形缓冲、分钟聚合、磁盘重启一致性、Trim、截断容忍与性能。</summary>
public sealed class HistoryStoreTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>注入测试输出，用于打印耗时与体积数据。</summary>
    public HistoryStoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AppendThenGetTrend_ReturnsAscendingPoints()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { MemoryRetentionMinutes = 5, SampleIntervalMs = 1000, EnableDiskHistory = false };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));

        var start = LocalTime(9, 10, 5);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Frame(start.AddSeconds(i * 2), ("app:a", "应用A", 10 + i, 100L * 1024 * 1024)));
        }

        var trend = store.GetTrend("app:a", 0);

        Assert.Equal(5, trend.Count);
        Assert.Equal(new[] { 10d, 11d, 12d, 13d, 14d }, trend.Select(point => point.CpuPercent).ToArray());
        for (var i = 1; i < trend.Count; i++)
        {
            Assert.True(trend[i].TimestampUtc > trend[i - 1].TimestampUtc);
        }

        var lastThree = store.GetTrend("app:a", 3);
        Assert.Equal(3, lastThree.Count);
        Assert.Equal(12d, lastThree[0].CpuPercent);
        Assert.Empty(store.GetTrend("app:不存在", 0));
    }

    [Fact]
    public void TrendBuffer_IsBoundedAndResizesOnSettingsChange()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings
        {
            MemoryRetentionMinutes = 5,
            SampleIntervalMs = 1000,
            EnableDiskHistory = false,
            TrendAppSlots = 4,
        };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));

        var start = LocalTime(9, 0, 0);
        for (var i = 0; i < 400; i++)
        {
            store.Append(Frame(start.AddSeconds(i), ("app:a", "应用A", 5, 64L * 1024 * 1024)));
        }

        // 5 分钟 / 1 秒 = 300 点上限
        Assert.Equal(300, store.GetTrend("app:a", 0).Count);

        store.OnSettingsChanged(new AppSettings
        {
            MemoryRetentionMinutes = 10,
            SampleIntervalMs = 1000,
            EnableDiskHistory = false,
            TrendAppSlots = 4,
        });

        Assert.Equal(300, store.GetTrend("app:a", 0).Count);

        for (var i = 0; i < 400; i++)
        {
            store.Append(Frame(start.AddSeconds(400 + i), ("app:a", "应用A", 6, 64L * 1024 * 1024)));
        }

        Assert.Equal(600, store.GetTrend("app:a", 0).Count);
    }

    [Fact]
    public void TrendSlots_OnlyKeepConfiguredApplicationCount()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings
        {
            MemoryRetentionMinutes = 5,
            SampleIntervalMs = 1000,
            EnableDiskHistory = false,
            TrendAppSlots = 2,
        };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));
        var start = LocalTime(9, 0, 0);

        store.Append(Frame(
            start,
            ("app:hot", "热应用", 40, 512L * 1024 * 1024),
            ("app:warm", "温应用", 20, 256L * 1024 * 1024),
            ("app:cold", "冷应用", 1, 64L * 1024 * 1024)));

        Assert.Equal(2, store.GetTrend("app:hot", 0).Count + store.GetTrend("app:warm", 0).Count);
        Assert.Empty(store.GetTrend("app:cold", 0));
    }

    [Fact]
    public void MinuteAggregation_QueryDaySumsCpuSeconds()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 7 };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));

        // 第 1 分钟：10% / 30% → 平均 20%，峰值 30%
        store.Append(Frame(LocalTime(9, 10, 5), ("app:a", "应用A", 10, 200L * 1024 * 1024)));
        store.Append(Frame(LocalTime(9, 10, 35), ("app:a", "应用A", 30, 400L * 1024 * 1024)));
        // 第 2 分钟：50% / 50% → 平均 50%，峰值 50%
        store.Append(Frame(LocalTime(9, 11, 5), ("app:a", "应用A", 50, 300L * 1024 * 1024)));
        store.Append(Frame(LocalTime(9, 11, 35), ("app:a", "应用A", 50, 300L * 1024 * 1024)));

        var summaries = store.QueryDay(DateOnly.FromDateTime(DateTime.Today));

        var summary = Assert.Single(summaries);
        Assert.Equal("app:a", summary.AppKey);
        Assert.Equal("应用A", summary.DisplayName);
        Assert.Equal(20 * 60 + 50 * 60, summary.CpuPercentSeconds, 3);
        Assert.Equal(50, summary.PeakCpuPercent, 3);
        Assert.Equal(400L * 1024 * 1024, summary.PeakWorkingSetBytes);
        Assert.Equal(2, summary.SampleCount);
    }

    [Fact]
    public void QueryDay_ReturnsAppsOutsideTrendSlots()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 7, TrendAppSlots = 1, TopN = 10 };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));

        store.Append(Frame(
            LocalTime(11, 0, 10),
            ("app:one", "一号", 60, 600L * 1024 * 1024),
            ("app:two", "二号", 30, 300L * 1024 * 1024),
            ("app:three", "三号", 10, 120L * 1024 * 1024)));

        var summaries = store.QueryDay(DateOnly.FromDateTime(DateTime.Today));

        Assert.Equal(3, summaries.Count);
        Assert.Equal("app:one", summaries[0].AppKey);
        Assert.True(summaries[0].CpuPercentSeconds > summaries[1].CpuPercentSeconds);
        Assert.Single(store.GetTrend("app:one", 0));
        Assert.Empty(store.GetTrend("app:three", 0));
    }

    [Fact]
    public void DiskHistory_SurvivesRestart()
    {
        using var temp = new TempTestDirectory();
        var historyRoot = temp.Combine("history");
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 30 };
        var today = DateOnly.FromDateTime(DateTime.Today);
        IReadOnlyList<DailyAppSummary> before;

        using (var store = new HistoryStore(settings, NullLog.Instance, historyRoot))
        {
            store.Append(Frame(LocalTime(13, 0, 5), ("app:a", "应用A", 10, 100L * 1024 * 1024), ("app:b", "应用B", 5, 80L * 1024 * 1024)));
            store.Append(Frame(LocalTime(13, 1, 5), ("app:a", "应用A", 30, 200L * 1024 * 1024), ("app:b", "应用B", 15, 160L * 1024 * 1024)));
            before = store.QueryDay(today).ToArray();
        }

        Assert.NotEmpty(before);
        Assert.True(File.Exists(Path.Combine(historyRoot, "days", today.ToString("yyyy-MM-dd") + ".bin")));
        Assert.True(File.Exists(Path.Combine(historyRoot, "apps.json")));

        using var reopened = new HistoryStore(settings, NullLog.Instance, historyRoot);
        var after = reopened.QueryDay(today);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].AppKey, after[i].AppKey);
            Assert.Equal(before[i].DisplayName, after[i].DisplayName);
            Assert.Equal(before[i].CpuPercentSeconds, after[i].CpuPercentSeconds, 3);
            Assert.Equal(before[i].PeakCpuPercent, after[i].PeakCpuPercent, 3);
            Assert.Equal(before[i].PeakWorkingSetBytes, after[i].PeakWorkingSetBytes);
            Assert.Equal(before[i].SampleCount, after[i].SampleCount);
        }

        Assert.Contains(today, reopened.GetAvailableDays());
    }

    [Fact]
    public void Trim_RemovesFilesOlderThanRetention()
    {
        using var temp = new TempTestDirectory();
        var historyRoot = temp.Combine("history");
        var daysDirectory = Path.Combine(historyRoot, "days");
        Directory.CreateDirectory(daysDirectory);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var recent = today.AddDays(-2);
        var old = today.AddDays(-40);
        File.WriteAllBytes(Path.Combine(daysDirectory, recent.ToString("yyyy-MM-dd") + ".bin"), BuildRecord(1, 10, 5f, 6f, 1024));
        File.WriteAllBytes(Path.Combine(daysDirectory, old.ToString("yyyy-MM-dd") + ".bin"), BuildRecord(1, 10, 5f, 6f, 1024));
        File.WriteAllBytes(Path.Combine(daysDirectory, "不是日期.bin"), BuildRecord(1, 10, 5f, 6f, 1024));

        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 5 };
        using var store = new HistoryStore(settings, NullLog.Instance, historyRoot);

        var days = store.GetAvailableDays();

        Assert.Contains(recent, days);
        Assert.DoesNotContain(old, days);
        // 文件名无法解析的文件不参与清理，也不会被当成日期。
        Assert.True(File.Exists(Path.Combine(daysDirectory, "不是日期.bin")));
    }

    [Fact]
    public void QueryDay_IgnoresTruncatedTailAndGarbageRecords()
    {
        using var temp = new TempTestDirectory();
        var historyRoot = temp.Combine("history");
        var daysDirectory = Path.Combine(historyRoot, "days");
        Directory.CreateDirectory(daysDirectory);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var path = Path.Combine(daysDirectory, today.ToString("yyyy-MM-dd") + ".bin");
        var bytes = new List<byte>();
        bytes.AddRange(BuildRecord(1, 10, 10f, 10f, 100));
        bytes.AddRange(BuildRecord(1, 11, 20f, 20f, 200));
        bytes.AddRange(BuildRecord(1, 12, 30f, 30f, 300));
        bytes.AddRange(new byte[10]); // 模拟崩溃时写了一半的记录
        File.WriteAllBytes(path, bytes.ToArray());

        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 30 };
        using var store = new HistoryStore(settings, NullLog.Instance, historyRoot);

        var summaries = store.QueryDay(today);

        var summary = Assert.Single(summaries);
        Assert.Equal(10 * 60 + 20 * 60 + 30 * 60, summary.CpuPercentSeconds, 3);
        Assert.Equal(30, summary.PeakCpuPercent, 3);
        Assert.Equal(300, summary.PeakWorkingSetBytes);
        Assert.Equal(3, summary.SampleCount);
    }

    [Fact]
    public void QueryDay_WhenDiskHistoryDisabled_WritesNothing()
    {
        using var temp = new TempTestDirectory();
        var historyRoot = temp.Combine("history");
        var settings = new AppSettings { EnableDiskHistory = false, DiskRetentionDays = 30 };

        using (var store = new HistoryStore(settings, NullLog.Instance, historyRoot))
        {
            store.Append(Frame(LocalTime(15, 0, 5), ("app:a", "应用A", 50, 500L * 1024 * 1024)));
            store.Append(Frame(LocalTime(15, 1, 5), ("app:a", "应用A", 60, 500L * 1024 * 1024)));
            store.Flush();
        }

        var daysDirectory = Path.Combine(historyRoot, "days");
        Assert.False(Directory.Exists(daysDirectory) && Directory.GetFiles(daysDirectory, "*.bin").Length > 0);

        using var reopened = new HistoryStore(settings, NullLog.Instance, historyRoot);
        Assert.Empty(reopened.GetAvailableDays());
    }

    [Fact]
    public async Task ConcurrentAppendAndRead_DoesNotThrow()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 7, TrendAppSlots = 4 };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));
        var start = LocalTime(16, 0, 0);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 600; i++)
            {
                store.Append(Frame(
                    start.AddSeconds(i),
                    ("app:a", "应用A", 10 + (i % 30), 100L * 1024 * 1024),
                    ("app:b", "应用B", 5 + (i % 10), 200L * 1024 * 1024)));
            }
        });

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                _ = store.GetTrend("app:a", 60);
                _ = store.QueryDay(today);
                _ = store.GetAvailableDays();
            }
        });

        var all = Task.WhenAll(writer, reader);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(all, finished);
        await all;
    }

    [Fact]
    public void Append_AverageCostStaysBelowOneMillisecond()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 30, TrendAppSlots = 12, TopN = 30 };
        using var store = new HistoryStore(settings, NullLog.Instance, temp.Combine("history"));

        var apps = Enumerable.Range(0, 20)
            .Select(i => ($"dir:C:\\Apps\\App{i}", $"应用{i}", 30.0 - i, (long)(80 + i * 10) * 1024 * 1024))
            .ToArray();
        var start = LocalTime(17, 0, 0);

        // 预热
        for (var i = 0; i < 200; i++)
        {
            store.Append(Frame(start.AddSeconds(i), apps));
        }

        const int iterations = 2000;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            store.Append(Frame(start.AddSeconds(200 + i), apps));
        }

        stopwatch.Stop();
        var averageMicroseconds = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        _output.WriteLine($"单帧 Append 平均耗时：{averageMicroseconds:F2} 微秒（{iterations} 次，20 个应用/帧）");

        Assert.True(averageMicroseconds < 1000, $"单帧 Append 平均 {averageMicroseconds:F2} 微秒，超过 1 毫秒预算");
    }

    [Fact]
    public void DiskFootprint_PerMinuteIsPredictable()
    {
        using var temp = new TempTestDirectory();
        var settings = new AppSettings { EnableDiskHistory = true, DiskRetentionDays = 30, TrendAppSlots = 12, TopN = 30 };
        var historyRoot = temp.Combine("history");
        var today = DateOnly.FromDateTime(DateTime.Today);

        using var store = new HistoryStore(settings, NullLog.Instance, historyRoot);

        // 30 个活跃应用 + 大量空闲应用：空闲应用不应写盘。
        var frameApps = new List<(string, string, double, long)>();
        for (var i = 0; i < 30; i++)
        {
            frameApps.Add(($"dir:C:\\Games\\App{i}", $"应用{i}", 25.0 - i * 0.5, (long)(500 + i * 20) * 1024 * 1024));
        }

        for (var i = 0; i < 40; i++)
        {
            frameApps.Add(($"exe:idle{i}.exe", $"空闲{i}", 0.01, 5L * 1024 * 1024));
        }

        var apps = frameApps.ToArray();
        store.Append(Frame(LocalTime(18, 0, 5), apps));
        store.Append(Frame(LocalTime(18, 1, 5), apps)); // 触发上一分钟落盘
        store.Flush();

        var path = Path.Combine(historyRoot, "days", today.ToString("yyyy-MM-dd") + ".bin");
        var length = new FileInfo(path).Length;
        var recordCount = length / 32;
        _output.WriteLine($"每分钟落盘记录数：{recordCount} 条，文件 {length} 字节；按 30 天估算约 {length * 60L * 24 * 30 / 1024.0 / 1024.0:F1} MB");

        Assert.Equal(30, recordCount);
        Assert.Equal(30 * 32, length);
    }

    /// <summary>构造本地时间 <see cref="DateTimeKind.Local"/> 的时间戳。</summary>
    private static DateTime LocalTime(int hour, int minute, int second) =>
        DateTime.SpecifyKind(DateTime.Today.AddHours(hour).AddMinutes(minute).AddSeconds(second), DateTimeKind.Local);

    /// <summary>构造一帧：<c>apps</c> 需按 CPU 降序传入（与生产约定一致）。</summary>
    private static SnapshotFrame Frame(DateTime timestamp, params (string Key, string Name, double Cpu, long WorkingSet)[] apps)
    {
        var samples = apps.Select(app => new AppSample(
            app.Key,
            app.Name,
            null,
            AppKind.ExeDirectory,
            null,
            app.Cpu,
            app.WorkingSet,
            app.WorkingSet / 2,
            1,
            0,
            0,
            "测试归并",
            Array.Empty<AppProcessInfo>())).ToArray();

        return new SnapshotFrame(
            timestamp,
            12.5,
            40,
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            TimeSpan.FromSeconds(2),
            0.2,
            12L * 1024 * 1024,
            samples.Length + 120,
            samples);
    }

    /// <summary>按磁盘记录格式手写一条 32 字节记录（小端）。</summary>
    private static byte[] BuildRecord(int appId, int minuteOfDay, float cpuAvg, float cpuMax, long workingSetPeak)
    {
        var buffer = new byte[32];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), appId);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), minuteOfDay);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 4), cpuAvg);
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 4), cpuMax);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 8), workingSetPeak);
        return buffer;
    }
}
