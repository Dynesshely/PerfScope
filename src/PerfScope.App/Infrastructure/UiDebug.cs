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
}