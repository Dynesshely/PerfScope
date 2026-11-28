using PerfScope.Core.Settings;

namespace PerfScope.App.Services;

/// <summary>
/// 对话框服务：ViewModel 不直接 new 窗口，只声明「要打开哪个对话框」，
/// 由实现层（WPF）负责窗口生命周期。所有窗口都是模态。
/// </summary>
public interface IDialogService
{
    /// <summary>打开设置页，保存成功返回 <c>true</c>。</summary>
    bool ShowSettings(AppSettings current);

    /// <summary>打开历史页。</summary>
    void ShowHistory();

    /// <summary>打开关于页。</summary>
    void ShowAbout();

    /// <summary>打开「接入 Agent」页。</summary>
    void ShowAgentHandoff();

    /// <summary>打开首次启动引导。</summary>
    void ShowFirstRunTips();

    /// <summary>询问用户是否继续（取消返回 false）。</summary>
    bool Confirm(string message, string title);

    /// <summary>显示错误信息。</summary>
    void ShowError(string message, string title);
}
