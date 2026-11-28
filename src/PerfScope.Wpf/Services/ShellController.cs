using System.Windows;
using System.Windows.Controls;
using PerfScope.App.Infrastructure;
using PerfScope.App.ViewModels;
using PerfScope.App.Views;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Services;

/// <summary>
/// 外壳协调者：把「托盘」「主窗口」「ViewModel」连起来，并处理关闭到托盘与退出流程。
/// ViewModel 不认识托盘，视图不认识设置，所有跨模块的粘合都收敛在这里。
/// </summary>
internal sealed class ShellController : IDisposable
{
    private readonly AppHost _host;
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _window;
    private readonly TrayService _tray;
    private readonly IDialogService _dialogs;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _startupItem;
    private readonly System.Windows.Threading.DispatcherTimer _trimTimer;
    private bool _exiting;
    private DateTime _lastTrayUpdateUtc = DateTime.MinValue;

    /// <summary>创建外壳协调者并挂好托盘菜单。</summary>
    public ShellController(AppHost host, MainViewModel viewModel, MainWindow window, TrayService tray, IDialogService dialogs)
    {
        _host = host;
        _viewModel = viewModel;
        _window = window;
        _tray = tray;
        _dialogs = dialogs;

        _tray.Menu = BuildMenu(out _pauseItem, out _startupItem);
        _tray.ActivateRequested += (_, _) => _window.BringToFront();
        _tray.MenuRequested += (_, _) => RefreshMenuState();

        _window.ShouldHideToTray = ShouldHideToTray;
        _window.IsVisibleChanged += (_, _) => MemoryTrimmer.Trim();
        _host.Engine.FrameProduced += OnFrameProduced;

        // WPF 的工作集比 WinForms 高：常驻托盘时定期归还空闲页，保证"任务管理器里看到的内存"守住预算
        // 30 秒一次：WPF 的工作集主要是渲染用的原生内存，定期归还空闲页，
        // 让任务管理器里看到的数字稳定在低水位（实测 5 分钟 → 60 秒峰值降 40~60 MB，
        // 60 秒 → 30 秒再降约 15~20 MB；再密就会造成反复换页，得不偿失）
        _trimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trimTimer.Tick += (_, _) => MemoryTrimmer.Trim();
        _trimTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => MemoryTrimmer.TrimWithGc(), TaskScheduler.Default);
    }

    /// <summary>启动后的收尾：按设置决定显示窗口还是留在托盘。</summary>
    public void Start()
    {
        if (_host.Settings.StartMinimized)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
        }

        UpdateTrayTip(_host.Latest);
    }

    /// <summary>退出应用（保存状态、释放服务，由 Bootstrap 结束消息循环）。</summary>
    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _viewModel.SaveState();
        try
        {
            _window.CloseForExit();
        }
        catch
        {
            // 窗口已关闭时忽略
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _trimTimer.Stop();
        _host.Engine.FrameProduced -= OnFrameProduced;
        _tray.Dispose();
    }

    private bool ShouldHideToTray()
        => !_exiting && _host.Settings.CloseBehavior == CloseBehavior.MinimizeToTray;

    private ContextMenu BuildMenu(out MenuItem pauseItem, out MenuItem startupItem)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var show = new MenuItem { Header = Localization.Loc.T("显示主窗口"), FontWeight = FontWeights.SemiBold };
        show.Click += (_, _) => _window.BringToFront();

        pauseItem = new MenuItem();
        pauseItem.Click += (_, _) => _viewModel.TogglePauseCommand.Execute(null);

        var refresh = new MenuItem { Header = Localization.Loc.T("立即刷新") };
        refresh.Click += (_, _) => _viewModel.RefreshNowCommand.Execute(null);

        startupItem = new MenuItem { Header = Localization.Loc.T("开机自动启动"), IsCheckable = true };
        startupItem.Click += (_, _) => ToggleStartup();

        var settings = new MenuItem { Header = Localization.Loc.T("设置…") };
        settings.Click += (_, _) => _viewModel.OpenSettingsCommand.Execute(null);

        var about = new MenuItem { Header = Localization.Loc.T("关于") };
        about.Click += (_, _) => _dialogs.ShowAbout();

        var exit = new MenuItem { Header = Localization.Loc.T("退出") };
        exit.Click += (_, _) => Exit();

        menu.Items.Add(show);
        menu.Items.Add(pauseItem);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(startupItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(about);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        RefreshMenuState(pauseItem, startupItem);
        return menu;
    }

    private void RefreshMenuState() => RefreshMenuState(_pauseItem, _startupItem);

    private void RefreshMenuState(MenuItem pauseItem, MenuItem startupItem)
    {
        pauseItem.Header = _viewModel.IsPaused ? Localization.Loc.T("继续监控") : Localization.Loc.T("暂停监控");
        startupItem.IsChecked = _host.Settings.RunAtStartup;
    }

    private void ToggleStartup()
    {
        var next = _host.Settings.Clone();
        next.RunAtStartup = !next.RunAtStartup;
        if (!_host.ApplySettings(next))
        {
            MessageBoxService.ShowError(_host.LastError ?? Localization.Loc.T("保存设置失败"), Localization.Loc.T("开机自启"));
        }

        RefreshMenuState();
    }

    private void OnFrameProduced(object? sender, SnapshotFrame frame)
    {
        // 托盘提示每秒最多更新一次，避免高频 P/Invoke
        if ((DateTime.UtcNow - _lastTrayUpdateUtc).TotalMilliseconds < 1000)
        {
            return;
        }

        _lastTrayUpdateUtc = DateTime.UtcNow;
        UpdateTrayTip(frame);
    }

    private void UpdateTrayTip(SnapshotFrame? frame)
    {
        if (frame is null)
        {
            _tray.UpdateToolTip("PerfScope");
            return;
        }

        _tray.UpdateToolTip(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "PerfScope · CPU {0} / 内存 {1}",
            FormatUtil.Percent(frame.SystemCpuPercent),
            FormatUtil.Percent(frame.SystemMemoryPercent)));
    }
}
