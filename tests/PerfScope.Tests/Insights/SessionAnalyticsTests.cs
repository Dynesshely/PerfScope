using PerfScope.Core.Abstractions;
using PerfScope.Core.Insights;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Insights;

/// <summary>分析引擎：CPU 片段、内存增长、digest 的行为约束。</summary>
public sealed class SessionAnalyticsTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SnapshotFrame Frame(int index, double cpu, long memoryBytes, double systemCpu = 20, long available = 8L << 30)
    {
        var app = new AppSample(
            "product:x|demo", "Demo App", null, AppKind.ProductName, null,
            cpu, memoryBytes, memoryBytes / 2, 3, 0, 0, "测试",
            Array.Empty<AppProcessInfo>());

        return new SnapshotFrame(
            Start.AddSeconds(index * 2),
            systemCpu,
            50,
            64L << 30,
            available,
            TimeSpan.FromSeconds(2),
            0.5,
            30L << 20,
            3,
            new[] { app });
    }

    [Fact]
    public void CPU_片段在阈值持续足够久后产生事件()
    {
        var settings = new AppSettings { InsightAppCpuPercent = 20, InsightAppCpuSeconds = 10 };
        var analytics = new SessionAnalytics(settings);

        // 先低后高，持续 20 秒
        for (int i = 0; i < 5; i++) analytics.Feed(Frame(i, 5, 100L << 20));
        for (int i = 5; i < 20; i++) analytics.Feed(Frame(i, 40, 100L << 20));
        for (int i = 20; i < 25; i++) analytics.Feed(Frame(i, 5, 100L << 20));

        var episodes = analytics.DrainEvents().Where(e => e.Kind == InsightKinds.AppCpuEpisode).ToArray();
        Assert.Single(episodes);
        Assert.Equal("Demo App", episodes[0].AppName);
        Assert.True(episodes[0].DurationSeconds >= 10);
        Assert.True(episodes[0].PeakCpuPercent >= 40);
    }

    [Fact]
    public void 内存持续增长会产生增长事件()
    {
        var settings = new AppSettings { InsightMemoryGrowthMbPerMinute = 50 };
        var analytics = new SessionAnalytics(settings);

        long baseBytes = 200L << 20;
        for (int i = 0; i < 90; i++)
        {
            // 每 2 秒涨 8 MB → 240 MB/分钟
            analytics.Feed(Frame(i, 2, baseBytes + i * (8L << 20)));
        }

        analytics.Complete(); // 会话结束时应把仍在增长的片段收尾
        var events = analytics.DrainEvents().Where(e => e.Kind == InsightKinds.AppMemoryGrowth).ToArray();
        Assert.NotEmpty(events);
        Assert.True(events[0].GrowthMbPerMinute > 50);
        Assert.True(events[0].PeakMemoryMb > events[0].AvgMemoryMb);
    }

    [Fact]
    public void 摘要事件按窗口产出且包含应用排行()
    {
        var settings = new AppSettings { InsightDigestMinutes = 1 };
        var analytics = new SessionAnalytics(settings);

        for (int i = 0; i < 40; i++) analytics.Feed(Frame(i, 10, 300L << 20));

        var digests = analytics.DrainEvents().Where(e => e.Kind == InsightKinds.Digest).ToArray();
        Assert.NotEmpty(digests);
        Assert.NotNull(digests[0].Apps);
        Assert.NotEmpty(digests[0].Apps!);
        Assert.Equal("Demo App", digests[0].Apps![0].Name);
    }

    [Fact]
    public void 关闭分析后不再产生事件()
    {
        var analytics = new SessionAnalytics(new AppSettings { InsightEnabled = false });
        for (int i = 0; i < 20; i++) analytics.Feed(Frame(i, 90, 100L << 20));

        Assert.Empty(analytics.DrainEvents());
    }
}