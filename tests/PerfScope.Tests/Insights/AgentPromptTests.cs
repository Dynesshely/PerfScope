using PerfScope.Core.Abstractions;
using PerfScope.Core.Insights;
using PerfScope.Core.Models;

namespace PerfScope.Tests.Insights;

/// <summary>报告与 Agent Prompt 的生成约束（路径、口径、结论都必须真实写入）。</summary>
public sealed class AgentPromptTests
{
    private static readonly DateOnly Day = new(2026, 11, 1);

    [Fact]
    public void Prompt_包含真实路径与数据口径()
    {
        var context = new AgentPromptContext(
            Day,
            @"C:\data\analysis\2026-11-01-report.json",
            @"C:\data\analysis\2026-11-01-report.md",
            @"C:\data\insights\2026-11-01.jsonl",
            @"C:\data\analysis",
            17,
            42,
            8,
            2.0,
            125.7,
            new[] { "CPU 消耗最多：**Microsoft Edge**，累计 1089 秒。" });

        string prompt = AgentPromptBuilder.Build(context);

        Assert.Contains(@"C:\data\analysis\2026-11-01-report.json", prompt);
        Assert.Contains(@"C:\data\insights\2026-11-01.jsonl", prompt);
        Assert.Contains("perfscope.report/v1", prompt);
        Assert.Contains("整机口径", prompt);
        Assert.Contains("17 个", prompt);
        Assert.Contains("42 条", prompt);
        Assert.Contains("125.7 GB", prompt);
        Assert.Contains("Microsoft Edge", prompt);
        Assert.Contains("app_memory_growth", prompt);
    }

    [Fact]
    public void 报告按CPU时间排序并生成结论()
    {
        var history = new FakeHistory(new[]
        {
            new DailyAppSummary(Day, "a", "Alpha", 500, 12.5, 1024L * 1024 * 100, 10),
            new DailyAppSummary(Day, "b", "Beta", 9000, 30, 1024L * 1024 * 900, 50),
        });
        using var store = new InsightStore(CreateTempDirectory());

        var report = ReportBuilder.Build(Day, history, store);

        Assert.Equal("perfscope.report/v1", report.Schema);
        Assert.Equal(2, report.Apps.Count);
        Assert.Equal("Beta", report.Apps[0].Name);
        Assert.Equal(90.0, report.Apps[0].CpuSeconds, 1);
        Assert.NotEmpty(report.Findings);

        string markdown = ReportBuilder.ToMarkdown(report);
        Assert.Contains("## 二、应用排行", markdown);
        Assert.Contains("Beta", markdown);
        Assert.Contains("口径与数据说明", markdown);

        string json = ReportBuilder.ToJson(report);
        Assert.Contains("\"schema\": \"perfscope.report/v1\"", json);
        Assert.Contains("\"cpuSeconds\"", json);
    }

    private static string CreateTempDirectory()
    {
        // 用工作区内的临时目录：受限环境里 %TEMP% 可能不可写
        var path = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

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

        public IReadOnlyList<DailyAppSummary> QueryDay(DateOnly date) => _summaries;

        public IReadOnlyList<DateOnly> GetAvailableDays() => new[] { Day };

        public void Trim()
        {
        }

        public void OnSettingsChanged(PerfScope.Core.Settings.AppSettings settings)
        {
        }

        public void Dispose()
        {
        }
    }
}
