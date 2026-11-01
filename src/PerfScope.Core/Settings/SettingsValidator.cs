namespace PerfScope.Core.Settings;

/// <summary>
/// 设置项静态校验器：把任意来源（手改的 JSON、旧版本文件、UI 输入）的 <see cref="AppSettings"/>
/// 修正到合法范围内，并返回人类可读的中文修正说明，供日志与 UI 提示使用。
/// <para>
/// 规则：数值越界 → 钳制到边界；非法枚举 → 回退默认值；非法字符串 → 回退默认文案；
/// 自定义规则 → 去掉空模式与重复项，并保证枚举值合法。本类不抛异常。
/// </para>
/// </summary>
public static class SettingsValidator
{
    /// <summary>采样间隔下限（毫秒）。</summary>
    public const int SampleIntervalMsMin = 500;

    /// <summary>采样间隔上限（毫秒）。</summary>
    public const int SampleIntervalMsMax = 10000;

    /// <summary>界面刷新间隔下限（毫秒）。</summary>
    public const int UiRefreshIntervalMsMin = 250;

    /// <summary>界面刷新间隔上限（毫秒）。</summary>
    public const int UiRefreshIntervalMsMax = 10000;

    /// <summary>隐藏时采样间隔下限（毫秒）。</summary>
    public const int HiddenSampleIntervalMsMin = 1000;

    /// <summary>隐藏时采样间隔上限（毫秒）。</summary>
    public const int HiddenSampleIntervalMsMax = 60000;

    /// <summary>平滑窗口下限（采样点数）。</summary>
    public const int SmoothingWindowMin = 1;

    /// <summary>平滑窗口上限（采样点数）。</summary>
    public const int SmoothingWindowMax = 15;

    /// <summary>内存保留分钟数下限。</summary>
    public const int MemoryRetentionMinutesMin = 5;

    /// <summary>内存保留分钟数上限。</summary>
    public const int MemoryRetentionMinutesMax = 1440;

    /// <summary>磁盘历史保留天数下限（0 = 不落盘）。</summary>
    public const int DiskRetentionDaysMin = 0;

    /// <summary>磁盘历史保留天数上限。</summary>
    public const int DiskRetentionDaysMax = 365;

    /// <summary>趋势槽位数下限。</summary>
    public const int TrendAppSlotsMin = 1;

    /// <summary>趋势槽位数上限。</summary>
    public const int TrendAppSlotsMax = 64;

    /// <summary>列表行数下限。</summary>
    public const int TopNMin = 5;

    /// <summary>列表行数上限。</summary>
    public const int TopNMax = 200;

    /// <summary>CPU 告警阈值下限（百分比）。</summary>
    public const int AlertCpuPercentMin = 10;

    /// <summary>CPU 告警阈值上限（百分比）。</summary>
    public const int AlertCpuPercentMax = 100;

    /// <summary>内存告警阈值下限（百分比）。</summary>
    public const int AlertMemoryPercentMin = 10;

    /// <summary>内存告警阈值上限（百分比）。</summary>
    public const int AlertMemoryPercentMax = 100;

    /// <summary>告警冷却时间下限（秒）。</summary>
    public const int AlertCooldownSecondsMin = 30;

    /// <summary>告警冷却时间上限（秒）。</summary>
    public const int AlertCooldownSecondsMax = 3600;

    /// <summary>日志文件大小上限下限（KB）。</summary>
    public const int LogMaxKilobytesMin = 64;

    /// <summary>日志文件大小上限上限（KB）。</summary>
    public const int LogMaxKilobytesMax = 10240;

    /// <summary>自定义规则最多保留条数，防止配置文件被刷爆。</summary>
    public const int MaxRuleCount = 200;

    /// <summary>组名最大长度（字符）。</summary>
    private const int MaxGroupNameLength = 64;

    /// <summary>规则匹配模式最大长度（字符）。</summary>
    private const int MaxPatternLength = 260;

    /// <summary>合法主题值。</summary>
    public static readonly string[] AllowedThemes = { "dark", "light" };

    /// <summary>
    /// 就地修正 <paramref name="settings"/>，返回修正说明列表（空列表表示无需修正）。
    /// </summary>
    /// <param name="settings">待校验的设置实例，会被原地修改。</param>
    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var fixes = new List<string>(8);

        settings.SchemaVersion = ClampInt(settings.SchemaVersion, 1, int.MaxValue, "配置结构版本", fixes);

        // ===== 采样 =====
        settings.SampleIntervalMs = ClampInt(settings.SampleIntervalMs, SampleIntervalMsMin, SampleIntervalMsMax, "采样间隔", fixes);
        settings.UiRefreshIntervalMs = ClampInt(settings.UiRefreshIntervalMs, UiRefreshIntervalMsMin, UiRefreshIntervalMsMax, "界面刷新间隔", fixes);
        settings.HiddenSampleIntervalMs = ClampInt(settings.HiddenSampleIntervalMs, HiddenSampleIntervalMsMin, HiddenSampleIntervalMsMax, "隐藏时采样间隔", fixes);
        settings.SmoothingWindow = ClampInt(settings.SmoothingWindow, SmoothingWindowMin, SmoothingWindowMax, "平滑窗口", fixes);

        // ===== 计算口径 =====
        if (!Enum.IsDefined(settings.CpuMode))
        {
            fixes.Add($"CPU 口径 {settings.CpuMode} 非法，已回退为 {CpuNormalization.MachinePercent}");
            settings.CpuMode = CpuNormalization.MachinePercent;
        }

        if (!Enum.IsDefined(settings.MemoryMode))
        {
            fixes.Add($"内存口径 {settings.MemoryMode} 非法，已回退为 {MemoryDisplay.WorkingSet}");
            settings.MemoryMode = MemoryDisplay.WorkingSet;
        }

        // ===== 应用归并 =====
        settings.UnknownGroupName = NormalizeGroupName(settings.UnknownGroupName, "其他", "兜底组名", fixes);
        settings.SystemGroupName = NormalizeGroupName(settings.SystemGroupName, "Windows 系统", "系统进程组名", fixes);
        NormalizeRules(settings, fixes);

        // ===== 历史留存 =====
        settings.MemoryRetentionMinutes = ClampInt(settings.MemoryRetentionMinutes, MemoryRetentionMinutesMin, MemoryRetentionMinutesMax, "内存保留分钟数", fixes);
        settings.DiskRetentionDays = ClampInt(settings.DiskRetentionDays, DiskRetentionDaysMin, DiskRetentionDaysMax, "磁盘历史保留天数", fixes);
        settings.TrendAppSlots = ClampInt(settings.TrendAppSlots, TrendAppSlotsMin, TrendAppSlotsMax, "趋势应用槽位", fixes);

        // ===== 界面 =====
        settings.TopN = ClampInt(settings.TopN, TopNMin, TopNMax, "列表显示行数", fixes);
        if (!Enum.IsDefined(settings.CloseBehavior))
        {
            fixes.Add($"关闭行为 {settings.CloseBehavior} 非法，已回退为 {CloseBehavior.MinimizeToTray}");
            settings.CloseBehavior = CloseBehavior.MinimizeToTray;
        }

        settings.Theme = NormalizeTheme(settings.Theme, fixes);

        // ===== 告警 =====
        settings.AlertCpuPercent = ClampInt(settings.AlertCpuPercent, AlertCpuPercentMin, AlertCpuPercentMax, "CPU 告警阈值", fixes);
        settings.AlertMemoryPercent = ClampInt(settings.AlertMemoryPercent, AlertMemoryPercentMin, AlertMemoryPercentMax, "内存告警阈值", fixes);
        settings.AlertCooldownSeconds = ClampInt(settings.AlertCooldownSeconds, AlertCooldownSecondsMin, AlertCooldownSecondsMax, "告警冷却时间", fixes);

        // ===== 诊断 =====
        if (!Enum.IsDefined(settings.LogLevel))
        {
            fixes.Add($"日志级别 {settings.LogLevel} 非法，已回退为 {LogLevel.Warn}");
            settings.LogLevel = LogLevel.Warn;
        }

        settings.LogMaxKilobytes = ClampInt(settings.LogMaxKilobytes, LogMaxKilobytesMin, LogMaxKilobytesMax, "日志大小上限", fixes);

        return fixes;
    }

    /// <summary>把数值钳制到 [min, max]，越界时记录一条中文说明。</summary>
    private static int ClampInt(int value, int min, int max, string fieldName, List<string> fixes)
    {
        if (value < min)
        {
            fixes.Add($"{fieldName} {value} 小于允许下限，已修正为 {min}");
            return min;
        }

        if (value > max)
        {
            fixes.Add($"{fieldName} {value} 超过允许上限，已修正为 {max}");
            return max;
        }

        return value;
    }

    /// <summary>组名：去空白，空值回退默认，过长截断。</summary>
    private static string NormalizeGroupName(string? value, string fallback, string fieldName, List<string> fixes)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            fixes.Add($"{fieldName}为空，已回退为「{fallback}」");
            return fallback;
        }

        if (text.Length > MaxGroupNameLength)
        {
            fixes.Add($"{fieldName}过长（{text.Length} 字符），已截断为 {MaxGroupNameLength} 字符");
            return text[..MaxGroupNameLength];
        }

        return text;
    }

    /// <summary>主题：只接受 dark / light（大小写不敏感），其它值回退 dark。</summary>
    private static string NormalizeTheme(string? value, List<string> fixes)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (text is "dark" or "light")
        {
            return text;
        }

        fixes.Add($"主题「{value}」非法，已回退为「dark」");
        return "dark";
    }

    /// <summary>自定义规则：去掉空模式与重复项，修正非法枚举，限制总条数。</summary>
    private static void NormalizeRules(AppSettings settings, List<string> fixes)
    {
        if (settings.Rules is null)
        {
            settings.Rules = new List<AppRule>();
            fixes.Add("自定义规则列表缺失，已重置为空列表");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<AppRule>(settings.Rules.Count);
        var emptyCount = 0;
        var duplicateCount = 0;
        var overflowCount = 0;

        foreach (var rule in settings.Rules)
        {
            if (rule is null)
            {
                emptyCount++;
                continue;
            }

            var pattern = (rule.Pattern ?? string.Empty).Trim();
            if (pattern.Length == 0)
            {
                emptyCount++;
                continue;
            }

            if (pattern.Length > MaxPatternLength)
            {
                pattern = pattern[..MaxPatternLength];
                fixes.Add($"规则「{pattern[..16]}…」匹配模式过长，已截断为 {MaxPatternLength} 字符");
            }

            if (!Enum.IsDefined(rule.Match))
            {
                fixes.Add($"规则「{pattern}」的匹配方式 {rule.Match} 非法，已回退为 {AppRuleMatch.ExeName}");
                rule.Match = AppRuleMatch.ExeName;
            }

            if (!Enum.IsDefined(rule.Action))
            {
                fixes.Add($"规则「{pattern}」的动作 {rule.Action} 非法，已回退为 {AppRuleAction.MergeToName}");
                rule.Action = AppRuleAction.MergeToName;
            }

            rule.Pattern = pattern;
            rule.Target = string.IsNullOrWhiteSpace(rule.Target) ? null : rule.Target.Trim();
            if (rule.Action == AppRuleAction.Exclude)
            {
                rule.Target = null;
            }

            var dedupeKey = string.Concat(
                ((int)rule.Match).ToString(),
                "|",
                pattern.ToLowerInvariant(),
                "|",
                ((int)rule.Action).ToString(),
                "|",
                (rule.Target ?? string.Empty).ToLowerInvariant());

            if (!seen.Add(dedupeKey))
            {
                duplicateCount++;
                continue;
            }

            if (accepted.Count >= MaxRuleCount)
            {
                overflowCount++;
                continue;
            }

            accepted.Add(rule);
        }

        if (emptyCount > 0)
        {
            fixes.Add($"已丢弃 {emptyCount} 条无匹配模式的自定义规则");
        }

        if (duplicateCount > 0)
        {
            fixes.Add($"已去重 {duplicateCount} 条重复的自定义规则");
        }

        if (overflowCount > 0)
        {
            fixes.Add($"自定义规则超过 {MaxRuleCount} 条上限，已丢弃 {overflowCount} 条");
        }

        settings.Rules = accepted;
    }
}
