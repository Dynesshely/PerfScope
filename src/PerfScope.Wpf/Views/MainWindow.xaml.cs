using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PerfScope.App.Chrome;
using PerfScope.App.Infrastructure;
using PerfScope.App.ViewModels;

namespace PerfScope.App.Views;

/// <summary>
/// 主窗口。视图层只做三件事：初始化 XAML、把窗口生命周期事件转给 ViewModel、注册键盘快捷键；
/// 任何业务判断都不写在这里。
/// </summary>
public partial class MainWindow : ChromeWindow
{
    private MainViewModel? _viewModel;
    private bool _allowClose;

    /// <summary>
    /// 关闭按钮的行为判定（由外壳注入）：返回 true 表示这次关闭应当「最小化到托盘」而不是退出。
    /// 视图不自己读设置，行为策略由 ShellController 决定。
    /// </summary>
    public Func<bool>? ShouldHideToTray { get; set; }

    /// <summary>创建主窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = AppVersionInfo.Display;

        if (Environment.GetEnvironmentVariable("PERFSCOPE_DIAG_ROWS") == "1")
        {
            StartRowDiagnostics();
        }
    }

    /// <summary>诊断：定期把「列表里已实例化的 ListBoxItem 数」写进临时日志，用于验证虚拟化是否生效。</summary>
    private void StartRowDiagnostics()
    {
        string path = Path.Combine(Path.GetTempPath(), "perfscope-ui-diag.log");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            try
            {
                int realized = CountVisuals<System.Windows.Controls.ListBoxItem>(AppList);
                string extra = string.Empty;
                if (_viewModel is not null)
                {
                    var (frames, rowsMs, treemapMs, posts) = _viewModel.DrainDiagnostics();
                    extra = " frames=" + frames + " posts=" + posts
                        + " rowsMs=" + rowsMs.ToString("F1") + " treemapMs=" + treemapMs.ToString("F1");
                }

                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " items=" + AppList.Items.Count
                    + " realized=" + realized + extra + Environment.NewLine);
            }
            catch
            {
                // 诊断失败不影响主流程
            }
        };
        timer.Start();
    }

    private static int CountVisuals<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = 0;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T)
            {
                count++;
            }

            count += CountVisuals<T>(child);
        }

        return count;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel = e.NewValue as MainViewModel;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel?.SetWindowVisible(IsVisible);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel?.SaveState();
        SavePlacement();

        if (_allowClose)
        {
            return;
        }

        if (ShouldHideToTray?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>真正退出时关闭窗口（绕开「关闭到托盘」策略）。</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>把窗口位置与大小记进 ui.json，下次启动还原。</summary>
    private void SavePlacement()
    {
        var state = UiStateStore.Load();
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        state.X = (int)Math.Round(bounds.X);
        state.Y = (int)Math.Round(bounds.Y);
        state.Width = (int)Math.Round(bounds.Width);
        state.Height = (int)Math.Round(bounds.Height);
        state.Maximized = WindowState == WindowState.Maximized;
        UiStateStore.Save(state);
    }

    /// <summary>按 ui.json 还原窗口位置与大小（越界时回落到居中）。</summary>
    public void RestorePlacement(UiState state)
    {
        if (state.Width >= (int)MinWidth && state.Height >= (int)MinHeight)
        {
            Width = state.Width;
            Height = state.Height;
        }

        if (state.X != int.MinValue && state.Y != int.MinValue)
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            var bounds = new Rect(state.X, state.Y, width, height);

            // 只还原仍落在虚拟桌面内的位置：换了显示器 / 分辨率后不至于把窗口丢到屏幕外
            bool onScreen = SystemParameters.VirtualScreenLeft <= bounds.Left
                       && SystemParameters.VirtualScreenTop <= bounds.Top
                       && SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth >= bounds.Right
                       && SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight >= bounds.Bottom;

            if (onScreen)
            {
                Left = state.X;
                Top = state.Y;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        if (state.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>窗口激活（托盘双击 / 二次启动）时把窗口带到前台。</summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.F5:
                _viewModel.RefreshNowCommand.Execute(null);
                break;
            case Key.F6:
                _viewModel.TogglePauseCommand.Execute(null);
                break;
            case Key.OemComma when ctrl:
                _viewModel.OpenSettingsCommand.Execute(null);
                break;
            case Key.H when ctrl:
                _viewModel.OpenHistoryCommand.Execute(null);
                break;
            case Key.A when ctrl && shift:
                _viewModel.OpenAgentCommand.Execute(null);
                break;
            case Key.D when ctrl && shift:
                _viewModel.ToggleTreemapCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
