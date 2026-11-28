using System.Windows;
using System.Windows.Threading;
using PerfScope.App.Bootstrap;
using PerfScope.App.Infrastructure;
using PerfScope.App.Services;
using PerfScope.App.ViewModels;
using PerfScope.App.Views;
using PerfScope.Core;

namespace PerfScope.App.Wpf;

/// <summary>
/// WPF 入口。与 WinForms 入口并存于重写过渡期（csproj 的 StartupObject 指向这里），
/// 过渡期结束后删除 WinForms 的 Program.cs / MainForm / TrayAppContext。
/// </summary>
internal static class WpfBootstrap
{
    /// <summary>进程入口。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(
                "PerfScope 启动失败：\n\n" + ex.Message,
                "PerfScope",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        options.ApplyDataDirectory();
        ApplyDebugSwitches(args);
        UiDebug.LoadDiagnosticSwitches();

        bool demo = options.Demo || options.Mock;

        // 默认以管理员身份运行：非管理员读不到受保护进程的路径与 CPU 时间
        if (!demo && !options.Elevated && !Elevation.IsElevated()
            && Elevation.TryRelaunchElevated(args, out _))
        {
            return 0;
        }

        using var guard = SingleInstanceGuard.TryAcquire(demo);
        if (guard is null)
        {
            return 0;
        }

        var app = new App();
        app.InitializeComponent();

        using var host = AppHost.Create(demo, options.Mock, options.Language);
        var theme = new ThemeService();
        theme.Apply(host.Settings.Theme);

        var state = UiStateStore.Load();
        using var icons = new IconService();
        var dialogs = new DialogService(host);
        var dispatcher = new WpfUiDispatcher(app.Dispatcher);
        var viewModel = new MainViewModel(host, dispatcher, theme, icons, dialogs, state);

        var window = new MainWindow { DataContext = viewModel };
        app.MainWindow = window;
        dialogs.AttachWindow(window);
        window.RestorePlacement(state);

        using var tray = new TrayService("PerfScope");
        using var shell = new ShellController(host, viewModel, window, tray, dialogs);
        using var notifications = new NotificationCoordinator(host, tray);

        guard.ActivationRequested += (_, _) => dispatcher.Post(window.BringToFront);
        window.Closed += (_, _) => app.Shutdown();

        host.Start();

        // 演示 / 截图模式总是显示窗口：它们的目的就是给人看
        if (demo)
        {
            window.Show();
        }
        else
        {
            shell.Start();
            _ = notifications.StartAsync();
        }

        // 提示类窗口要等消息循环跑起来再弹（在 Run 之前 ShowDialog 会缺少 dispatcher 上下文）
        app.Dispatcher.BeginInvoke(
            new Action(() => ShowStartupDialogs(host, dialogs, demo)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (options.SelfTestSeconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.SelfTestSeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                app.Shutdown();
            };
            timer.Start();
        }

        app.Run();

        viewModel.Dispose();
        return 0;
    }

    /// <summary>启动后的提示类窗口：首次引导、设置页、接入 Agent（后两者供截图与自测用）。</summary>
    private static void ShowStartupDialogs(AppHost host, IDialogService dialogs, bool isDemo)
    {
        // 演示 / 截图模式不自动弹首次引导，只能靠 --tips-demo 显式触发
        if ((!isDemo && !host.Settings.FirstRunTipsShown) || UiDebug.ShowFirstRunTips)
        {
            dialogs.ShowFirstRunTips();
        }

        if (UiDebug.OpenSettingsOnStart)
        {
            dialogs.ShowSettings(host.Settings.Clone());
        }

        if (UiDebug.OpenAgentOnStart)
        {
            dialogs.ShowAgentHandoff();
        }
    }

    private static void ApplyDebugSwitches(string[] args)
    {
        UiDebug.HoverDemo = HasFlag(args, "--hover-demo");
        UiDebug.OpenAgentOnStart = HasFlag(args, "--open-agent");
        UiDebug.OpenSettingsOnStart = HasFlag(args, "--open-settings");
        UiDebug.ExpandFirstGroup = HasFlag(args, "--expand-first");
        UiDebug.ForceListTooltip = HasFlag(args, "--list-tip-demo");
        UiDebug.ShowFirstRunTips = HasFlag(args, "--tips-demo");
        UiDebug.ForceHighContrast = HasFlag(args, "--force-high-contrast");

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--settings-tab", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out int tab))
            {
                UiDebug.SettingsTabIndex = tab;
                break;
            }
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
