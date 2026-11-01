using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;

namespace PerfScope.Core.Insights;

/// <summary>报告里的系统级汇总。</summary>
public sealed record ReportSystem(
    double CpuAvgPercent,
    double CpuPeakPercent,
    double MemoryAvgPercent,
    double MemoryPeakPercent,
    double MinAvailableMb,
    int Samples);

/// <summary>报告里的单应用汇总（来自磁盘分钟聚合）。</summary>
public sealed record ReportApp(
    string Key,
    string Name,
    double CpuSeconds,
    double CpuAvgPercent,
    double CpuPeakPercent,
    double MemoryPeakMb,
    int Minutes);

/// <summary>
/// 机器可读的分析报告：字段稳定、单位明确，便于任何 AI / 脚本直接消费。
/// </summary>
public sealed record InsightReport(
    string Schema,
    DateOnly Date,
    DateTime GeneratedUtc,
    ReportSystem System,
    IReadOnlyList<ReportApp> Apps,
    IReadOnlyList<InsightEvent> Events,
    IReadOnlyList<string> Findings);

/// <summary>
/// 报告生成器：把「分钟聚合（磁盘历史）+ 事件流（JSONL）」合成一份结论型报告（Markdown + JSON）。
/// 报告只保留结论，不含原始采样，方便直接喂给模型阅读。
/// </summary>
public static class ReportBuilder
{
    /// <summary>当前报告 schema 版本。</summary>
    public const string SchemaVersion = "perfscope.report/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>生成某天的报告。</summary>
    public static InsightReport Build(DateOnly day, IHistoryStore history, InsightStore store)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);

        var summaries = history.QueryDay(day);
        var events = store.LoadDay(day);

        var apps = summaries
            .OrderByDescending(item => item.CpuPercentSeconds)
            .Select(item => new ReportApp(
                item.AppKey,
                item.DisplayName,
                Math.Round(item.CpuPercentSeconds / 100.0, 1),
                Math.Round(item.CpuPercentSeconds / (Math.Max(1, item.SampleCount) * 60.0), 3),
                Math.Round(item.PeakCpuPercent, 2),
                Math.Round(item.PeakWorkingSetBytes / 1048576.0, 1),
                item.SampleCount))
            .ToArray();

        var digests = events.Where(e => e.Kind == InsightKinds.Digest).ToArray();
        double cpuSum = 0, cpuPeak = 0, memSum = 0, memPeak = 0, minAvailable = double.MaxValue;
        int samples = 0;
        foreach (var digest in digests)
        {
            int count = Math.Max(1, digest.SampleCount);
            cpuSum += digest.AvgCpuPercent * count;
            cpuPeak = Math.Max(cpuPeak, digest.PeakCpuPercent);
            memSum += digest.AvgMemoryMb * count;
            memPeak = Math.Max(memPeak, digest.AvgMemoryMb);
            samples += count;
        }

        var system = new ReportSystem(
            samples > 0 ? Math.Round(cpuSum / samples, 2) : 0,
            Math.Round(cpuPeak, 2),
            samples > 0 ? Math.Round(memSum / samples, 2) : 0,
            Math.Round(memPeak, 2),
            minAvailable == double.MaxValue ? 0 : Math.Round(minAvailable, 1),
            samples);

        var findings = BuildFindings(apps, events, system);
        return new InsightReport(SchemaVersion, day, DateTime.UtcNow, system, apps, events, findings);
    }

    /// <summary>输出 Markdown（给人看，也给模型读）。</summary>
    public static string ToMarkdown(InsightReport report)
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("# PerfScope 资源分析报告 · " + report.Date.ToString("yyyy-MM-dd"));
        builder.AppendLine();
        builder.AppendLine("> 生成时间：" + report.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · schema " + report.Schema);
        builder.AppendLine();

        builder.AppendLine("## 一、总体");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine("| 整机 CPU 平均 | " + report.System.CpuAvgPercent.ToString("F1") + "% |");
        builder.AppendLine("| 整机 CPU 峰值 | " + report.System.CpuPeakPercent.ToString("F1") + "% |");
        builder.AppendLine("| 内存占用平均 | " + report.System.MemoryAvgPercent.ToString("F1") + "% |");
        string availableText = report.System.MinAvailableMb > 0
            ? (report.System.MinAvailableMb / 1024.0).ToString("F1") + " GB"
            : "—";
        builder.AppendLine("| 可用内存最低 | " + availableText + " |");
        builder.AppendLine("| 采样帧数 | " + report.System.Samples + " |");
        builder.AppendLine();

        builder.AppendLine("## 二、应用排行（按 CPU 时间）");
        builder.AppendLine();
        builder.AppendLine("| # | 应用 | CPU 时间 | 平均 CPU | 峰值 CPU | 峰值内存 | 有数据分钟 |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        int index = 0;
        foreach (var app in report.Apps.Take(15))
        {
            index++;
            builder.AppendLine("| " + index + " | " + Escape(app.Name) + " | " + app.CpuSeconds.ToString("F1") + " s | " +
                               app.CpuAvgPercent.ToString("F2") + "% | " + app.CpuPeakPercent.ToString("F2") + "% | " +
                               app.MemoryPeakMb.ToString("F0") + " MB | " + app.Minutes + " |");
        }

        builder.AppendLine();
        builder.AppendLine("## 三、值得关注的事件");
        builder.AppendLine();
        var notable = report.Events
            .Where(e => e.Kind != InsightKinds.Digest && e.Kind != InsightKinds.SessionStart)
            .OrderByDescending(e => e.TimestampUtc)
            .Take(20)
            .ToArray();

        if (notable.Length == 0)
        {
            builder.AppendLine("_这一天没有触发任何阈值事件（CPU 片段 / 内存增长 / 系统高负载）。_");
        }
        else
        {
            builder.AppendLine("| 时间 | 类型 | 对象 | 持续 | 均值 | 峰值 | 说明 |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var item in notable)
            {
                string start = item.StartUtc.HasValue ? item.StartUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "--";
                builder.AppendLine("| " + start + " | " + KindText(item.Kind) + " | " + Escape(item.AppName ?? "整机") + " | " +
                                   item.DurationSeconds.ToString("F0") + "s | " + item.AvgCpuPercent.ToString("F1") + "% | " +
                                   item.PeakCpuPercent.ToString("F1") + "% | " + Escape(item.Note ?? string.Empty) + " |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 四、结论与线索");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine("- " + finding);
        }

        builder.AppendLine();
        builder.AppendLine("## 五、给 AI 的提问建议");
        builder.AppendLine();
        builder.AppendLine("- 哪个应用在哪个时间段出现 CPU 尖峰？是固定周期（定时任务）还是持续增长？");
        builder.AppendLine("- 内存持续增长的应用，增长速率与峰值是多少？是否伴随进程数增加（可能是子进程泄漏）？");
        builder.AppendLine("- 整机高负载时段与哪个应用的片段重合？能否排除后台索引 / 编译 / 容器？");
        builder.AppendLine();
        builder.AppendLine("## 六、口径与数据说明");
        builder.AppendLine();
        builder.AppendLine("- 百分比均为**整机口径**（0~100，等于任务管理器 CPU 列）；内存单位 MB / GB（1 MB = 1024 x 1024 字节）。");
        builder.AppendLine("- 应用已按「版本信息 + 安装目录 + 系统功能模块」归并：WSL、图形、内核、安全等系统模块与普通应用同样统计。");
        builder.AppendLine("- 数据来源：分钟级聚合 history/days/yyyy-MM-dd.bin + 事件流 insights/yyyy-MM-dd.jsonl；报告不含原始采样，体积小，适合直接交给模型。");
        return builder.ToString();
    }

    /// <summary>输出 JSON（机器可读，字段稳定）。</summary>
    public static string ToJson(InsightReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>把报告写到 analysis 目录，返回 (markdown, json) 路径。</summary>
    public static (string MarkdownPath, string JsonPath) Write(InsightReport report, string? rootDirectory = null)
    {
        var root = Path.Combine(rootDirectory ?? AppPaths.DataDirectory, "analysis");
        Directory.CreateDirectory(root);
        var md = Path.Combine(root, report.Date.ToString("yyyy-MM-dd") + "-report.md");
        var json = Path.Combine(root, report.Date.ToString("yyyy-MM-dd") + "-report.json");
        string markdown = ToMarkdown(report);
        string jsonText = ToJson(report);
        File.WriteAllText(md, markdown, new UTF8Encoding(false));
        File.WriteAllText(json, jsonText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "latest-report.md"), markdown, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "latest-report.json"), jsonText, new UTF8Encoding(false));
        return (md, json);
    }

    private static IReadOnlyList<string> BuildFindings(
        IReadOnlyList<ReportApp> apps,
        IReadOnlyList<InsightEvent> events,
        ReportSystem system)
    {
        var findings = new List<string>(8);

        if (apps.Count > 0)
        {
            var top = apps[0];
            findings.Add("CPU 消耗最多：**" + top.Name + "**，累计 " + top.CpuSeconds.ToString("F0") + " 秒（平均 " +
                         top.CpuAvgPercent.ToString("F2") + "%）。");
        }

        if (apps.Count > 0)
        {
            var memoryTop = apps.OrderByDescending(a => a.MemoryPeakMb).First();
            findings.Add("内存峰值最高：**" + memoryTop.Name + "**，峰值 " + memoryTop.MemoryPeakMb.ToString("F0") + " MB。");
        }

        var growths = events.Where(e => e.Kind == InsightKinds.AppMemoryGrowth).ToArray();
        if (growths.Length > 0)
        {
            var worst = growths.OrderByDescending(e => e.GrowthMbPerMinute).First();
            findings.Add("检测到 " + growths.Length + " 次内存持续增长，最快的是 **" + worst.AppName + "**（约 " +
                         worst.GrowthMbPerMinute.ToString("F0") + " MB/分钟，持续 " +
                         (worst.DurationSeconds / 60).ToString("F0") + " 分钟）。");
        }

        var cpuEpisodes = events.Where(e => e.Kind == InsightKinds.AppCpuEpisode).ToArray();
        if (cpuEpisodes.Length > 0)
        {
            var busiest = cpuEpisodes.OrderByDescending(e => e.AvgCpuPercent).First();
            string start = busiest.StartUtc.HasValue ? busiest.StartUtc.Value.ToLocalTime().ToString("HH:mm") : "--";
            findings.Add("共 " + cpuEpisodes.Length + " 段 CPU 高占用片段，最重的一段来自 **" + busiest.AppName + "**（" +
                         start + " 起，持续 " + (busiest.DurationSeconds / 60).ToString("F1") + " 分钟，均值 " +
                         busiest.AvgCpuPercent.ToString("F1") + "%）。");
        }

        int systemEpisodes = events.Count(e => e.Kind == InsightKinds.SystemCpuEpisode);
        if (systemEpisodes > 0)
        {
            findings.Add("整机 CPU 有 " + systemEpisodes + " 次持续高负载（阈值以上），建议对照同时段的应用片段。");
        }

        int pressure = events.Count(e => e.Kind == InsightKinds.SystemMemoryPressure);
        if (pressure > 0)
        {
            findings.Add("出现 " + pressure + " 次内存压力（可用内存低于阈值），注意常驻应用的内存增长趋势。");
        }

        findings.Add("整机 CPU 平均 " + system.CpuAvgPercent.ToString("F1") + "%、峰值 " +
                     system.CpuPeakPercent.ToString("F1") + "%；若峰值远高于均值，说明存在短时突发负载。");

        return findings;
    }

    private static string KindText(string kind) => kind switch
    {
        InsightKinds.AppCpuEpisode => "CPU 高占用",
        InsightKinds.AppMemoryGrowth => "内存增长",
        InsightKinds.SystemCpuEpisode => "系统高负载",
        InsightKinds.SystemMemoryPressure => "内存压力",
        _ => kind,
    };

    private static string Escape(string text) => text.Replace("|", " / ", StringComparison.Ordinal);
}