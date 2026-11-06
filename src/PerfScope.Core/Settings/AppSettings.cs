namespace PerfScope.Core.Settings;

/// <summary>CPU 百分比口径。</summary>
public enum CpuNormalization
{
    /// <summary>整机口径：0~100，等于「任务管理器」的 CPU 列。</summary>
    MachinePercent = 0,

    /// <summary>单核口径：0~100*逻辑核心数，单线程跑满 = 100。</summary>
    PerCorePercent = 1,
}

/// <summary>内存列展示哪种口径。</summary>
public enum MemoryDisplay
{
    /// <summary>工作集（驻留物理内存）。</summary>
    WorkingSet = 0,

    /// <summary>私有提交字节（更接近「这个应用真正占用的内存」）。</summary>
    PrivateWorkingSet = 1,
}

/// <summary>点击关闭按钮时的行为。</summary>
public enum CloseBehavior
{
    /// <summary>最小化到托盘继续监控。</summary>
    MinimizeToTray = 0,

    /// <summary>直接退出。</summary>
    Exit = 1,
}

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
}

/// <summary>自定义归并规则的匹配方式。</summary>
public enum AppRuleMatch
{
    /// <summary>按映像名精确匹配（不区分大小写），例如 <c>chrome.exe</c>。</summary>
    ExeName = 0,

    /// <summary>可执行文件路径包含指定子串（不区分大小写）。</summary>
    PathContains = 1,

    /// <summary>按版本信息里的 ProductName 匹配（不区分大小写，支持包含关系的模糊匹配由规则引擎决定）。</summary>
    ProductName = 2,
}

/// <summary>自定义归并规则的动作。</summary>
public enum AppRuleAction
{
    /// <summary>把这些进程合并到名为 <see cref="AppRule.Target"/> 的应用下。</summary>
    MergeToName = 0,

    /// <summary>只改显示名，不改变归并关系。</summary>
    Rename = 1,

    /// <summary>从列表与统计中排除（例如自己的监控进程、系统闲杂进程）。</summary>
    Exclude = 2,
}

/// <summary>一条用户自定义归并规则，按列表顺序优先匹配。</summary>
public sealed class AppRule
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>匹配方式。</summary>
    public AppRuleMatch Match { get; set; } = AppRuleMatch.ExeName;

    /// <summary>匹配模式（映像名 / 路径片段 / 产品名）。</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>命中后的动作。</summary>
    public AppRuleAction Action { get; set; } = AppRuleAction.MergeToName;

    /// <summary>目标应用名（<see cref="AppRuleAction.Rename"/> 与 <see cref="AppRuleAction.MergeToName"/> 使用）。</summary>
    public string? Target { get; set; }

    /// <summary>深拷贝。</summary>
    public AppRule Clone() => new()
    {
        Enabled = Enabled,
        Match = Match,
        Pattern = Pattern,
        Action = Action,
        Target = Target,
    };
}

/// <summary>
/// 全部可调参数。所有参数都会持久化到 <see cref="PerfScope.Core.AppPaths.SettingsFile"/>，
/// 并在设置页面修改后热生效（无需重启）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>配置结构版本，用于后续升级迁移。</summary>
    public int SchemaVersion { get; set; } = 1;

    // ================= 采样 =================

    /// <summary>采样间隔（毫秒）。范围 500~10000，默认 2000。间隔越大自身开销越低。</summary>
    public int SampleIntervalMs { get; set; } = 2000;

    /// <summary>界面刷新间隔（毫秒）。范围 250~10000，默认 1000。只影响重绘频率，不影响采样。</summary>
    public int UiRefreshIntervalMs { get; set; } = 1000;

    /// <summary>主窗口隐藏时自动降频采样（用 <see cref="HiddenSampleIntervalMs"/>），进一步降低常驻开销。</summary>
    public bool AdaptiveThrottleWhenHidden { get; set; } = true;

    /// <summary>隐藏状态下的采样间隔（毫秒）。范围 1000~60000，默认 5000。</summary>
    public int HiddenSampleIntervalMs { get; set; } = 5000;

    /// <summary>平滑窗口（采样点数，1 表示不平滑）。范围 1~15，默认 3；用于抑制单帧毛刺。</summary>
    public int SmoothingWindow { get; set; } = 3;

    // ================= 计算口径 =================

    /// <summary>CPU 百分比口径。</summary>
    public CpuNormalization CpuMode { get; set; } = CpuNormalization.MachinePercent;

    /// <summary>内存口径。</summary>
    public MemoryDisplay MemoryMode { get; set; } = MemoryDisplay.WorkingSet;

    // ================= 应用归并 =================

    /// <summary>按 exe 版本信息里的 ProductName + CompanyName 归并（最能代表「一个应用」）。</summary>
    public bool GroupByProductName { get; set; } = true;

    /// <summary>按安装根目录归并（Program Files、LOCALAPPDATA\Programs、卸载器目录探测等）。</summary>
    public bool GroupByInstallRoot { get; set; } = true;

    /// <summary>子进程在自身无法判定时，继承父进程的应用归属。</summary>
    public bool InheritParentGroup { get; set; } = true;

    /// <summary>把 Windows 系统进程（System32 下的系统组件、svchost 等）合并为单独一组，避免刷屏。</summary>
    public bool MergeWindowsSystemProcesses { get; set; } = true;

    /// <summary>缓存 exe 的版本信息与目录解析结果，避免每次采样都读盘。默认开启。</summary>
    public bool UseVersionInfoCache { get; set; } = true;

    /// <summary>无法归类时的兜底组名。</summary>
    public string UnknownGroupName { get; set; } = "其他";

    /// <summary>Windows 系统进程组的组名。</summary>
    public string SystemGroupName { get; set; } = "Windows 系统";

    /// <summary>用户自定义规则，按顺序优先匹配。</summary>
    public List<AppRule> Rules { get; set; } = new();

    // ================= 历史留存 =================

    /// <summary>内存中保留的分钟数（趋势曲线用）。范围 5~1440，默认 60。</summary>
    public int MemoryRetentionMinutes { get; set; } = 60;

    /// <summary>磁盘历史保留天数。范围 1~365，默认 30；0 表示不落盘。</summary>
    public int DiskRetentionDays { get; set; } = 30;

    /// <summary>记录趋势曲线的应用个数（按 CPU 排名取前 N）。范围 1~64，默认 12。</summary>
    public int TrendAppSlots { get; set; } = 12;

    /// <summary>是否把历史写入磁盘。</summary>
    public bool EnableDiskHistory { get; set; } = true;

    // ================= 界面 =================

    /// <summary>列表显示的应用行数。范围 5~200，默认 30。</summary>
    public int TopN { get; set; } = 30;

    /// <summary>启动时最小化到托盘。</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>开机自动启动（写入 HKCU Run 键，不需要管理员权限）。</summary>
    public bool RunAtStartup { get; set; } = false;

    /// <summary>关闭按钮行为。</summary>
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

    /// <summary>在状态栏显示监控器自身开销（CPU / 内存）。</summary>
    public bool ShowSelfUsage { get; set; } = true;

    /// <summary>在列表中显示 Windows 系统进程组。</summary>
    public bool ShowSystemProcesses { get; set; } = true;

    /// <summary>主题：<c>dark</c> 或 <c>light</c>。</summary>
    public string Theme { get; set; } = "dark";

    // ================= 告警（托盘气泡） =================

    /// <summary>是否启用阈值告警。</summary>
    public bool EnableAlerts { get; set; } = false;

    /// <summary>整机 CPU 告警阈值（百分比）。范围 10~100，默认 85。</summary>
    public int AlertCpuPercent { get; set; } = 85;

    /// <summary>整机内存告警阈值（百分比）。范围 10~100，默认 90。</summary>
    public int AlertMemoryPercent { get; set; } = 90;

    /// <summary>同一告警的最小间隔（秒），防止刷屏。范围 30~3600，默认 300。</summary>
    public int AlertCooldownSeconds { get; set; } = 300;

    /// <summary>首次启动引导是否已经展示过。</summary>
    public bool FirstRunTipsShown { get; set; }

    // ================= 自动更新 =================

    /// <summary>启动后自动检查更新（唯一的网络访问，可在设置里关闭）。</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    // ================= 界面语言 =================

    /// <summary>界面语言：system（跟随系统）/ zh（中文）/ en（English）。</summary>
    public string Language { get; set; } = "system";

    // ================= 智能分析 =================

    /// <summary>是否持续产出分析历史（事件流 + 窗口摘要 + 可读报告）。</summary>
    public bool InsightEnabled { get; set; } = true;

    /// <summary>应用 CPU 片段阈值（整机百分比）。范围 1~100，默认 25。</summary>
    public int InsightAppCpuPercent { get; set; } = 25;

    /// <summary>应用 CPU 片段最短持续秒数。范围 5~600，默认 20。</summary>
    public int InsightAppCpuSeconds { get; set; } = 20;

    /// <summary>内存增长判定阈值（MB/分钟）。范围 5~10000，默认 50。</summary>
    public int InsightMemoryGrowthMbPerMinute { get; set; } = 50;

    /// <summary>整机 CPU 高负载阈值（百分比）。范围 10~100，默认 80。</summary>
    public int InsightSystemCpuPercent { get; set; } = 80;

    /// <summary>系统级片段最短持续秒数。范围 5~600，默认 30。</summary>
    public int InsightSystemCpuSeconds { get; set; } = 30;

    /// <summary>可用内存低于该百分比视为内存压力。范围 1~50，默认 8。</summary>
    public int InsightLowMemoryPercent { get; set; } = 8;

    /// <summary>摘要窗口分钟数。范围 1~60，默认 5。</summary>
    public int InsightDigestMinutes { get; set; } = 5;

    // ================= 诊断 =================

    /// <summary>日志级别。</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Warn;

    /// <summary>日志文件大小上限（KB），超出后轮转。范围 64~10240，默认 512。</summary>
    public int LogMaxKilobytes { get; set; } = 512;

    /// <summary>深拷贝（<see cref="Rules"/> 也会复制）。</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Rules = new List<AppRule>(Rules.Count);
        foreach (var rule in Rules)
        {
            copy.Rules.Add(rule.Clone());
        }

        return copy;
    }

    /// <summary>恢复出厂默认值（保留 SchemaVersion）。</summary>
    public void ResetToDefaults()
    {
        var defaults = new AppSettings();
        var schema = SchemaVersion;
        SchemaVersion = schema;
        CopyFrom(defaults);
    }

    /// <summary>把 <paramref name="source"/> 的全部字段拷贝到当前实例（保持引用不变）。</summary>
    public void CopyFrom(AppSettings source)
    {
        SchemaVersion = source.SchemaVersion;
        SampleIntervalMs = source.SampleIntervalMs;
        UiRefreshIntervalMs = source.UiRefreshIntervalMs;
        AdaptiveThrottleWhenHidden = source.AdaptiveThrottleWhenHidden;
        HiddenSampleIntervalMs = source.HiddenSampleIntervalMs;
        SmoothingWindow = source.SmoothingWindow;
        CpuMode = source.CpuMode;
        MemoryMode = source.MemoryMode;
        GroupByProductName = source.GroupByProductName;
        GroupByInstallRoot = source.GroupByInstallRoot;
        InheritParentGroup = source.InheritParentGroup;
        MergeWindowsSystemProcesses = source.MergeWindowsSystemProcesses;
        UseVersionInfoCache = source.UseVersionInfoCache;
        UnknownGroupName = source.UnknownGroupName;
        SystemGroupName = source.SystemGroupName;
        Rules = new List<AppRule>(source.Rules.Count);
        foreach (var rule in source.Rules)
        {
            Rules.Add(rule.Clone());
        }

        MemoryRetentionMinutes = source.MemoryRetentionMinutes;
        DiskRetentionDays = source.DiskRetentionDays;
        TrendAppSlots = source.TrendAppSlots;
        EnableDiskHistory = source.EnableDiskHistory;
        TopN = source.TopN;
        StartMinimized = source.StartMinimized;
        RunAtStartup = source.RunAtStartup;
        CloseBehavior = source.CloseBehavior;
        ShowSelfUsage = source.ShowSelfUsage;
        ShowSystemProcesses = source.ShowSystemProcesses;
        Theme = source.Theme;
        EnableAlerts = source.EnableAlerts;
        AlertCpuPercent = source.AlertCpuPercent;
        AlertMemoryPercent = source.AlertMemoryPercent;
        AlertCooldownSeconds = source.AlertCooldownSeconds;
        FirstRunTipsShown = source.FirstRunTipsShown;
        UpdateCheckEnabled = source.UpdateCheckEnabled;
        Language = source.Language;
        InsightEnabled = source.InsightEnabled;
        InsightAppCpuPercent = source.InsightAppCpuPercent;
        InsightAppCpuSeconds = source.InsightAppCpuSeconds;
        InsightMemoryGrowthMbPerMinute = source.InsightMemoryGrowthMbPerMinute;
        InsightSystemCpuPercent = source.InsightSystemCpuPercent;
        InsightSystemCpuSeconds = source.InsightSystemCpuSeconds;
        InsightLowMemoryPercent = source.InsightLowMemoryPercent;
        InsightDigestMinutes = source.InsightDigestMinutes;
        LogLevel = source.LogLevel;
        LogMaxKilobytes = source.LogMaxKilobytes;
    }
}