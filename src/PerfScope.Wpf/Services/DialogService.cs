using System.Windows;
using PerfScope.App.Infrastructure;
using PerfScope.App.ViewModels;
using PerfScope.App.Views;
using PerfScope.Core.Settings;

namespace PerfScope.App.Services;

/// <summary>
/// 对话框服务实现：每个方法创建一个窗口、挂上对应 ViewModel、以模态方式打开。
/// 所有数据都来自传入的 AppHost，窗口本身不碰 Core。
/// </summary>
internal sealed class DialogService : IDialogService
{
    private readonly AppHost _host;
    private Window? _owner;

    /// <summary>创建对话框服务。</summary>
    public DialogService(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>指定父窗口（用于居中与模态归属）。</summary>
    public void AttachWindow(Window owner) => _owner = owner;

    /// <inheritdoc />
    public bool ShowSettings(AppSettings current)
    {
        var window = new SettingsWindow(new SettingsViewModel(_host, current, this, UiDebug.SettingsTabIndex));
        return ShowDialog(window) == true;
    }

    /// <inheritdoc />
    public void ShowHistory()
    {
        var window = new HistoryWindow { DataContext = new HistoryViewModel(_host) };
        ShowDialog(window);
    }

    /// <inheritdoc />
    public void ShowAbout()
    {
        ShowDialog(new AboutWindow());
    }

    /// <inheritdoc />
    public void ShowAgentHandoff()
    {
        ShowDialog(new AgentHandoffWindow(() => _host.BuildAgentPrompt()));
    }

    /// <inheritdoc />
    public void ShowFirstRunTips()
    {
        var window = new OnboardingWindow();
        if (_owner is not null && _owner.IsVisible)
        {
            window.Owner = _owner;
        }

        window.ShowDialog();

        // 一次性引导：展示过就记为已看过（设置页里仍可重新打开）
        if (!_host.Settings.FirstRunTipsShown)
        {
            var next = _host.Settings.Clone();
            next.FirstRunTipsShown = true;
            _host.ApplySettings(next);
        }
    }

    /// <inheritdoc />
    public bool Confirm(string message, string title) => MessageBoxService.Confirm(message, title);

    /// <inheritdoc />
    public void ShowError(string message, string title) => MessageBoxService.ShowError(message, title);

    private bool? ShowDialog(Window window)
    {
        if (_owner is not null && _owner.IsVisible)
        {
            window.Owner = _owner;
        }

        return window.ShowDialog();
    }

}
