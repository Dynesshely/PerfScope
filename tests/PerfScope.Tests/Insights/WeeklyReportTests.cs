using PerfScope.Core.Abstractions;
using PerfScope.Core.Insights;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Insights;

/// <summary>周报：本周 vs 上周的聚合与环比。</summary>
public sealed class WeeklyReportTests
{
    private static readonly DateOnly Thursday = new(2026, 11, 5);

    [Fact]
    public void 周报按最近七天与上一个七天分组()
    {
        var history = new FakeHistory(new[]
        {
            // 本周：11-04、11-05
            Summary(new DateOnly(2026, 11, 4), "a", "Alpha", 6000, 30, 512, 10),
            Summary(new DateOnly(2026, 11, 5), "a", "Alpha", 3000, 20, 256, 5),
            // 上周：10-29
            Summary(new DateOnly(2026, 10, 29), "a", "Alpha", 1200, 15, 128, 4),
        });

        var report = WeeklyReportBuilder.Build(Thursday, history);

        Assert.Equal(new DateOnly(2026, 10, 30), report.CurrentStart);
        Assert.Equal(Thursday, report.CurrentEnd);
        Assert.Equal(new DateOnly(2026, 10, 23), report.PreviousStart);
        Assert.Equal(new DateOnly(2026, 10, 29), report.PreviousEnd);

        Assert.Equal(15, report.Current.Samples);
        Assert.Equal(4, report.Previous.Samples);
        Assert.Single(report.Apps);
        Assert.Equal("Alpha", report.Apps[0].Name);
    }

    [Fact]
    public void Markdown包含环比与排行()
    {
        var history = new FakeHistory(new[]
        {
            Summary(new DateOnly(2026, 11, 4), "a", "Alpha", 6000, 30, 512, 10),
            Summary(new DateOnly(2026, 10, 29), "b", "Beta", 1200, 15, 128, 4),
        });

        string markdown = WeeklyReportBuilder.ToMarkdown(WeeklyReportBuilder.Build(Thursday, history));

        Assert.Contains("Week over week", markdown, StringComparison.Ordinal);
        Assert.Contains("Top applications this week", markdown, StringComparison.Ordinal);
        Assert.Contains("Alpha", markdown, StringComparison.Ordinal);
        Assert.Contains("perfscope.weekly/v1", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有数据时不抛异常()
    {
        var report = WeeklyReportBuilder.Build(Thursday, new FakeHistory(Array.Empty<DailyAppSummary>()));

        Assert.Empty(report.Apps);
        Assert.NotEmpty(report.Findings);
        Assert.Equal(0, report.Current.Samples);
    }

    private static DailyAppSummary Summary(DateOnly day, string key, string name, double cpuPercentSeconds, double peakCpu, long peakBytes, int samples)
        => new(day, key, name, cpuPercentSeconds, peakCpu, peakBytes * 1024L * 1024L, samples);

    private sealed class FakeHistory : IHistoryStore
    {
        private readonly IReadOnlyList<DailyAppSummary> _summaries;

        public FakeHistory(IReadOnlyList<DailyAppSummary> summaries) => _summaries = summaries;

        public void Append(SnapshotFrame frame)
        {
        }

        public void Flush()
        {
        }

        public IReadOnlyList<TrendPoint> GetTrend(string appKey, int maxPoints) => Array.Empty<TrendPoint>();

        public IReadOnlyList<DailyAppSummary> QueryDay(DateOnly date)
            => _summaries.Where(item => item.Date == date).ToArray();

        public IReadOnlyList<DateOnly> GetAvailableDays() => _summaries.Select(item => item.Date).Distinct().OrderBy(item => item).ToArray();

        public void Trim()
        {
        }

        public void OnSettingsChanged(AppSettings settings)
        {
        }

        public void Dispose()
        {
        }
    }
}
