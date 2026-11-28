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

    // ===== 性能诊断开关（只用于定位"谁在空转"，正常使用永远全开）=====
    // 用法：set PERFSCOPE_DISABLE_UI=list,treemap,trend,hover,icons 后启动，逐项排除。

    /// <summary>关闭应用列表的逐帧刷新（保留头部指标）。</summary>
    public static bool DisableListRefresh { get; private set; }

    /// <summary>关闭分布视图（树图）刷新。</summary>
    public static bool DisableTreemap { get; private set; }

    /// <summary>关闭趋势曲线采样。</summary>
    public static bool DisableTrend { get; private set; }

    /// <summary>关闭窗口壳的 60ms 最大化键悬停轮询。</summary>
    public static bool DisableHoverTimer { get; private set; }

    /// <summary>关闭应用图标加载。</summary>
    public static bool DisableIcons { get; private set; }

    /// <summary>不再通知可访问性摘要（用于验证 UIA 绑定的成本）。</summary>
    public static bool DisableUiaNotifications { get; private set; }

    /// <summary>不再通知行高 / 子进程可见性（用于验证布局失效的成本）。</summary>
    public static bool DisableRowLayoutNotifications { get; private set; }

    /// <summary>不再逐帧更新子进程行（用于验证嵌套列表的成本）。</summary>
    public static bool DisableProcessRows { get; private set; }

    /// <summary>从环境变量解析诊断开关（启动时调用一次）。</summary>
    public static void LoadDiagnosticSwitches()
    {
        string? value = Environment.GetEnvironmentVariable("PERFSCOPE_DISABLE_UI");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "list":
                    DisableListRefresh = true;
                    break;
                case "treemap":
                    DisableTreemap = true;
                    break;
                case "trend":
                    DisableTrend = true;
                    break;
                case "hover":
                    DisableHoverTimer = true;
                    break;
                case "icons":
                    DisableIcons = true;
                    break;
                case "nouia":
                    DisableUiaNotifications = true;
                    break;
                case "norowheight":
                    DisableRowLayoutNotifications = true;
                    break;
                case "noprocess":
                    DisableProcessRows = true;
                    break;
            }
        }
    }
}
