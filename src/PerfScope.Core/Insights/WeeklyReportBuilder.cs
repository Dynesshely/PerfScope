using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;

namespace PerfScope.Core.Insights;

/// <summary>周维度汇总：最近 7 天与上一个 7 天的对比。</summary>
public sealed record WeeklyReport(
    string Schema,
    DateOnly CurrentStart,
    DateOnly CurrentEnd,
    DateOnly PreviousStart,
    DateOnly PreviousEnd,
    ReportSystem Current,
    ReportSystem Previous,
    IReadOnlyList<ReportApp> Apps,
    IReadOnlyList<string> Findings);

/// <summary>
/// 周报生成器：把两周的分钟聚合数据对比成「本周 vs 上周」，
/// 只输出结论与环比，方便直接读，也方便交给 AI 判断趋势。
/// </summary>
public static class WeeklyReportBuilder
{
    /// <summary>周报 schema 版本。</summary>
    public const string SchemaVersion = "perfscope.weekly/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>以 <paramref name="day"/> 为本周最后一天生成周报。</summary>
    public static WeeklyReport Build(DateOnly day, IHistoryStore history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var currentStart = day.AddDays(-6);
        var previousEnd = currentStart.AddDays(-1);
        var previousStart = previousEnd.AddDays(-6);

        var currentSummaries = Collect(history, currentStart, day);
        var previousSummaries = Collect(history, previousStart, previousEnd);

        var current = Summarize(currentSummaries, currentStart, day);
        var previous = Summarize(previousSummaries, previousStart, previousEnd);

        var apps = currentSummaries
            .GroupBy(item => item.AppKey)
            .Select(group => new ReportApp(
                group.Key,
                group.First().DisplayName,
                Math.Round(group.Sum(item => item.CpuPercentSeconds) / 100.0, 1),
                Math.Round(group.Sum(item => item.CpuPercentSeconds) / (Math.Max(1, group.Sum(item => item.SampleCount)) * 60.0), 3),
                Math.Round(group.Max(item => item.PeakCpuPercent), 2),
                Math.Round(group.Max(item => item.PeakWorkingSetBytes) / 1048576.0, 1),
                group.Sum(item => item.SampleCount)))
            .OrderByDescending(item => item.CpuSeconds)
            .ToArray();

        return new WeeklyReport(
            SchemaVersion,
            currentStart,
            day,
            previousStart,
            previousEnd,
            current,
            previous,
            apps,
            BuildFindings(apps, current, previous));
    }

    /// <summary>本周 vs 上周的 Markdown。</summary>
    public static string ToMarkdown(WeeklyReport report)
    {
        var builder = new StringBuilder(2048);
        builder.AppendLine("# PerfScope weekly report · " + report.CurrentStart.ToString("yyyy-MM-dd") + " → " + report.CurrentEnd.ToString("yyyy-MM-dd"));
        builder.AppendLine();
        builder.AppendLine("> schema " + report.Schema + " · previous week " + report.PreviousStart.ToString("yyyy-MM-dd") + " → " + report.PreviousEnd.ToString("yyyy-MM-dd"));
        builder.AppendLine();

        builder.AppendLine("## Week over week");
        builder.AppendLine();
        builder.AppendLine("| Metric | This week | Last week | Change |");
        builder.AppendLine("|---|---|---|---|");
        builder.AppendLine("| CPU average | " + Percent(report.Current.CpuAvgPercent) + " | " + Percent(report.Previous.CpuAvgPercent) + " | " + Delta(report.Current.CpuAvgPercent, report.Previous.CpuAvgPercent) + " |");
        builder.AppendLine("| CPU peak | " + Percent(report.Current.CpuPeakPercent) + " | " + Percent(report.Previous.CpuPeakPercent) + " | " + Delta(report.Current.CpuPeakPercent, report.Previous.CpuPeakPercent) + " |");
        builder.AppendLine("| Peak app memory | " + Megabytes(report.Apps.Count > 0 ? report.Apps.Max(item => item.MemoryPeakMb) : 0) + " | — | — |");
        builder.AppendLine("| Samples | " + report.Current.Samples + " | " + report.Previous.Samples + " | — |");
        builder.AppendLine();

        builder.AppendLine("## Top applications this week");
        builder.AppendLine();
        builder.AppendLine("| # | Application | CPU time | Avg CPU | Peak CPU | Peak memory |");
        builder.AppendLine("|---|---|---|---|---|---|");
        int index = 0;
        foreach (var app in report.Apps.Take(10))
        {
            index++;
            builder.AppendLine("| " + index + " | " + app.Name + " | " + app.CpuSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s | " +
                               app.CpuAvgPercent.ToString("F2", CultureInfo.InvariantCulture) + "% | " +
                               app.CpuPeakPercent.ToString("F2", CultureInfo.InvariantCulture) + "% | " +
                               app.MemoryPeakMb.ToString("F0", CultureInfo.InvariantCulture) + " MB |");
        }

        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine("- " + finding);
        }

        return builder.ToString();
    }

    /// <summary>周报 JSON。</summary>
    public static string ToJson(WeeklyReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>写入 analysis 目录，返回 (markdown, json)。</summary>
    public static (string MarkdownPath, string JsonPath) Write(WeeklyReport report, string? rootDirectory = null)
    {
        var root = Path.Combine(rootDirectory ?? AppPaths.DataDirectory, "analysis");
        Directory.CreateDirectory(root);
        string stamp = report.CurrentEnd.ToString("yyyy-MM-dd");
        var md = Path.Combine(root, stamp + "-weekly.md");
        var json = Path.Combine(root, stamp + "-weekly.json");
        File.WriteAllText(md, ToMarkdown(report), new UTF8Encoding(false));
        File.WriteAllText(json, ToJson(report), new UTF8Encoding(false));
        return (md, json);
    }

    private static List<DailyAppSummary> Collect(IHistoryStore history, DateOnly start, DateOnly end)
    {
        var list = new List<DailyAppSummary>(256);
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            try
            {
                list.AddRange(history.QueryDay(day));
            }
            catch
            {
                // 某一天读失败不影响整体周报
            }
        }

        return list;
    }

    private static ReportSystem Summarize(IReadOnlyList<DailyAppSummary> summaries, DateOnly start, DateOnly end)
    {
        // DailyAppSummary.SampleCount 是「该应用当日被聚合的分钟数」，各应用相加会重复计数，
        // 所以每天的分辨率取所有应用里的最大者，再乘以有数据的天数得到窗口总分钟数。
        int days = summaries.Select(item => item.Date).Distinct().Count();
        int minutesPerDay = summaries.Count == 0 ? 0 : summaries.Max(item => item.SampleCount);
        int totalMinutes = days * minutesPerDay;
        if (totalMinutes <= 0)
        {
            return new ReportSystem(0, 0, 0, 0, 0, 0, 0, 0);
        }

        double cpuPercentSeconds = summaries.Sum(item => item.CpuPercentSeconds);
        double cpuPeak = summaries.Count > 0 ? summaries.Max(item => item.PeakCpuPercent) : 0;
        double memoryPeakMb = summaries.Count > 0 ? summaries.Max(item => item.PeakWorkingSetBytes) / 1048576.0 : 0;
        return new ReportSystem(
            Math.Round(cpuPercentSeconds / (totalMinutes * 60.0), 2),
            Math.Round(cpuPeak, 2),
            0,
            Math.Round(memoryPeakMb, 1),
            0,
            totalMinutes,
            0,
            0);
    }

    private static IReadOnlyList<string> BuildFindings(IReadOnlyList<ReportApp> apps, ReportSystem current, ReportSystem previous)
    {
        var findings = new List<string>(4);
        if (apps.Count > 0)
        {
            findings.Add("Top application by CPU time this week: **" + apps[0].Name + "** (" + apps[0].CpuSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s).");
        }

        double delta = current.CpuAvgPercent - previous.CpuAvgPercent;
        if (Math.Abs(delta) >= 1)
        {
            findings.Add("Average CPU " + (delta > 0 ? "rose" : "fell") + " by " + Math.Abs(delta).ToString("F1", CultureInfo.InvariantCulture) + " percentage points week over week.");
        }
        else
        {
            findings.Add("Average CPU is roughly stable week over week.");
        }

        if (current.Samples == 0)
        {
            findings.Add("No samples this week — history may be disabled or retention too short.");
        }

        return findings;
    }

    private static string Percent(double value) => value.ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Megabytes(double value) => value >= 1024
        ? (value / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " GB"
        : value.ToString("F0", CultureInfo.InvariantCulture) + " MB";

    private static string Delta(double current, double previous)
    {
        double delta = current - previous;
        if (Math.Abs(delta) < 0.05)
        {
            return "±0";
        }

        return (delta > 0 ? "+" : string.Empty) + delta.ToString("F1", CultureInfo.InvariantCulture);
    }
}
