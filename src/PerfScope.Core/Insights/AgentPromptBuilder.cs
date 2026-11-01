using System.Globalization;
using System.Text;

namespace PerfScope.Core.Insights;

/// <summary>构造「交给 Agent 的分析任务」所需的上下文。</summary>
public sealed record AgentPromptContext(
    DateOnly Date,
    string ReportJsonPath,
    string ReportMarkdownPath,
    string EventsPath,
    string AnalysisDirectory,
    int AppCount,
    int EventCount,
    int DigestCount,
    double SampleIntervalSeconds,
    double TotalPhysicalGb,
    IReadOnlyList<string> Highlights);

/// <summary>
/// 生成一段「接入 Agent」的 Prompt：把真实文件路径、数据口径、事件类型和已经算好的结论直接写进去，
/// 用户复制给任意模型/Agent 即可，不需要再手写上下文。
/// </summary>
public static class AgentPromptBuilder
{
    /// <summary>生成 Prompt 文本。</summary>
    public static string Build(AgentPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder(2048);
        string date = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string interval = context.SampleIntervalSeconds > 0
            ? context.SampleIntervalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒"
            : "未知";
        string physical = context.TotalPhysicalGb > 0
            ? context.TotalPhysicalGb.ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : "未知";

        builder.AppendLine("你是一名 Windows 资源占用分析助手。下面是一台真实机器上由 PerfScope 采集并整理的监控数据，");
        builder.AppendLine("请基于这些**本地文件**分析资源占用情况，找出异常、可疑增长点和可优化项。");
        builder.AppendLine();
        builder.AppendLine("## 数据文件（本机绝对路径，可直接读取）");
        builder.AppendLine("- 分析报告（结论型，建议先读）：" + context.ReportJsonPath);
        builder.AppendLine("  同内容的 Markdown 版：" + context.ReportMarkdownPath);
        builder.AppendLine("- 原始事件流（JSONL，每行一个事件）：" + context.EventsPath);
        builder.AppendLine("- 分析目录（含 latest-report.*）：" + context.AnalysisDirectory);
        builder.AppendLine();
        builder.AppendLine("## 数据口径（务必遵守，否则结论会错）");
        builder.AppendLine("- 采集日期：" + date + "；采样间隔约 " + interval + "；物理内存 " + physical + "。");
        builder.AppendLine("- 所有百分比都是**整机口径**（0~100，与任务管理器一致），不是单核口径；内存单位是 MB。");
        builder.AppendLine("- 报告 schema：perfscope.report/v1。主要字段：");
        builder.AppendLine("  - system：cpuAvgPercent / cpuPeakPercent / memoryAvgPercent / minAvailableMb / samples");
        builder.AppendLine("  - apps[]：name、cpuSeconds（CPU 秒累计）、cpuAvgPercent、cpuPeakPercent、memoryPeakMb、minutes");
        builder.AppendLine("  - events[]：kind、startUtc、durationSeconds、avgCpuPercent、peakCpuPercent、appName、growthMbPerMinute、note");
        builder.AppendLine("  - findings[]：PerfScope 已经算好的结论，可作为线索，但请自行验证一致性。");
        builder.AppendLine("- events[].kind 取值：");
        builder.AppendLine("  - app_cpu_episode：某应用 CPU 持续高于阈值的一段（含均值/峰值/进程数）");
        builder.AppendLine("  - app_memory_growth：某应用内存持续增长的一段（growthMbPerMinute 是拟合斜率）");
        builder.AppendLine("  - system_cpu_episode：整机 CPU 持续高负载");
        builder.AppendLine("  - system_memory_pressure：可用内存持续偏低");
        builder.AppendLine("  - digest：5 分钟窗口摘要，apps[] 是该窗口内的 Top 应用");
        builder.AppendLine("- 应用已按「版本信息 + 安装目录 + 系统功能模块」归并：WSL、图形驱动、内核、安全等系统模块也在 apps 里，");
        builder.AppendLine("  名字是中文模块名（如「WSL 子系统」「内核与系统核心」），不是单个进程。");
        builder.AppendLine();
        builder.AppendLine("## 本次数据概览");
        builder.AppendLine("- 应用条目：" + context.AppCount + " 个");
        builder.AppendLine("- 事件条数：" + context.EventCount + " 条（其中窗口摘要 " + context.DigestCount + " 条）");
        if (context.Highlights.Count > 0)
        {
            builder.AppendLine("- PerfScope 自动结论：");
            foreach (var highlight in context.Highlights)
            {
                builder.AppendLine("  - " + highlight);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 请输出");
        builder.AppendLine("1. 总体判断：3~5 条，按重要性排序，先说结论。");
        builder.AppendLine("2. 异常与可疑项：每条都必须给出证据（应用名 + 时间点 + 具体数值），并说明为什么可疑。");
        builder.AppendLine("3. 可能原因与验证方法：需要补充采集什么数据、怎么复现或排除。");
        builder.AppendLine("4. 优化建议：区分「立刻可做」与「需要长期观察」，标明预期收益和风险。");
        builder.AppendLine("5. 数据局限：哪些结论受采样间隔、归并口径或可见进程范围限制。");
        builder.AppendLine();
        builder.AppendLine("要求：只基于给定数据推断，不要编造应用名或数值；不确定就明确说不确定；用简体中文回答，表格优先。");
        return builder.ToString();
    }
}
