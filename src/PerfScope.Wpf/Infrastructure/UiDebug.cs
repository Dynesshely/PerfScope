namespace PerfScope.App.Infrastructure;

/// <summary>
/// 界面调试开关（仅用于截图/自测，不影响正常使用）。
/// <c>--hover-demo</c>：让顶部趋势卡与分布树图默认处于「悬停」状态，
/// 方便在无人值守时截到坐标轴与数值卡片的画面。
/// </summary>
internal static class UiDebug
{
    /// <summary>是否强制显示悬停态。</summary>
    public static bool HoverDemo { get; set; }

    /// <summary>启动后是否自动打开「接入 Agent」窗口（仅截图 / 自动化验证用）。</summary>
    public static bool OpenAgentOnStart { get; set; }

    /// <summary>启动后是否自动打开设置窗口（仅截图 / 自动化验证用）。</summary>
    public static bool OpenSettingsOnStart { get; set; }

    /// <summary>打开设置窗口时默认选中的标签页索引（-1 表示不指定）。</summary>
    public static int SettingsTabIndex { get; set; } = -1;

    /// <summary>启动后自动展开第一个应用分组（仅截图 / 自动化验证用）。</summary>
    public static bool ExpandFirstGroup { get; set; }

    /// <summary>强制使用系统高对比度配色（仅截图 / 自动化验证用）。</summary>
    public static bool ForceHighContrast { get; set; }

    /// <summary>强制显示首次启动引导（仅截图 / 自动化验证用）。</summary>
    public static bool ShowFirstRunTips { get; set; }

    /// <summary>强制显示列表子项的悬停提示（仅截图 / 自动化验证用）。</summary>
    public static bool ForceListTooltip { get; set; }
}
