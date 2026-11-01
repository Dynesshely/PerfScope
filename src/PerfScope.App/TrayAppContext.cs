using PerfScope.App.Controls;
using PerfScope.App.Forms;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App;

/// <summary>
/// 托盘常驻上下文：承载 <see cref="NotifyIcon"/>、主窗口、右键菜单、告警气泡与单实例激活。
/// 关闭主窗口只是隐藏，采样与告警继续在托盘里工作。
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private const string ActivateEventName = @"Local\PerfScope.Activate";
    private const string ActivateEventNameDemo = @"Local\PerfScope.Activate.Demo";

    private readonly AppHost _host;
    private readonly bool _demo;
    private readonly MainForm _mainForm;
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _alertTimer;
    private readonly System.Windows.Forms.Timer? _selfTestTimer;
    private readonly EventWaitHandle? _activateEvent;
    private readonly RegisteredWaitHandle? _activateWait;

    private int _activateRequested;
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(3);

    private DateTime _lastCpuAlertUtc = DateTime.MinValue;
    private DateTime _lastTrimUtc = DateTime.MinValue;
    private bool _wasVisible = true;
    private DateTime _lastMemoryAlertUtc = DateTime.MinValue;
    private bool _exiting;
    private bool _disposed;

    /// <summary>创建托盘上下文并启动采样。</summary>
    /// <param name="host">应用服务组合根。</param>
    /// <param name="demo">是否演示模式。</param>
    /// <param name="selfTestSeconds">大于 0 时在指定秒数后自动退出（自检用）。</param>
    public TrayAppContext(AppHost host, bool demo, int selfTestSeconds)
    {
        _host = host;
        _demo = demo;

        _mainForm = new MainForm(host);
        _mainForm.ExitRequested += (_, _) => ExitApplication();

        var theme = _mainForm.CurrentTheme;
        _trayIcon = AppIcon.Get(32) ?? TrayIconFactory.Create(32);
        _showItem = new ToolStripMenuItem("显示主窗口") { Font = theme.UiBoldFont };
        _showItem.Click += (_, _) => ShowMainWindow();

        _pauseItem = new ToolStripMenuItem("暂停监控");
        _pauseItem.Click += (_, _) => _mainForm.TogglePause();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) =>
        {
            ShowMainWindow();
            _mainForm.OpenSettings();
        };

        var historyItem = new ToolStripMenuItem("历史…");
        historyItem.Click += (_, _) =>
        {
            ShowMainWindow();
            _mainForm.OpenHistory();
        };

        _startupItem = new ToolStripMenuItem("开机启动") { CheckOnClick = false };
        _startupItem.Click += (_, _) => ToggleStartup();

        var aboutItem = new ToolStripMenuItem("关于 PerfScope…");
        aboutItem.Click += (_, _) =>
        {
            using var form = new AboutForm(_host);
            form.ShowDialog(_mainForm);
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        _menu = new ContextMenuStrip
        {
            Renderer = new FlatToolStripRenderer(theme),
            BackColor = theme.Surface,
            ForeColor = theme.TextPrimary,
            Font = theme.UiFont,
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _showItem,
            new ToolStripSeparator(),
            _pauseItem,
            settingsItem,
            historyItem,
            new ToolStripSeparator(),
            _startupItem,
            new ToolStripSeparator(),
            aboutItem,
            exitItem,
        });

        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = demo ? "PerfScope（演示模式）" : "PerfScope — 应用级 CPU / 内存监控",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();

        _alertTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _alertTimer.Tick += OnAlertTimerTick;
        _alertTimer.Start();

        _host.PauseChanged += OnPauseChanged;
        _host.SettingsChanged += OnSettingsChanged;

        // 单实例激活：第二个实例通过命名事件唤醒已运行的实例
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventNameFor(demo));
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, OnActivateSignal, null, -1, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "创建单实例激活事件失败", ex);
        }

        SyncMenuState();
        _host.Start();

        bool showWindow = demo || !_host.Settings.StartMinimized;
        if (showWindow)
        {
            _mainForm.Show();
            _mainForm.ShowFromTray();
        }
        else
        {
            _host.SetUiVisible(false);
        }

        if (selfTestSeconds > 0)
        {
            _selfTestTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, selfTestSeconds) * 1000 };
            _selfTestTimer.Tick += OnSelfTestTick;
            _selfTestTimer.Start();
        }
    }

    /// <summary>当前模式对应的单实例激活事件名（Program 里第二个实例用它唤醒已有实例）。</summary>
    public static string ActivateEventNameFor(bool demo) => demo ? ActivateEventNameDemo : ActivateEventName;

    private void OnSelfTestTick(object? sender, EventArgs e)
    {
        _selfTestTimer?.Stop();
        ExitApplication();
    }

    private void OnActivateSignal(object? state, bool timedOut)
    {
        if (!timedOut)
        {
            Interlocked.Exchange(ref _activateRequested, 1);
        }
    }

    private void OnAlertTimerTick(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        if (Interlocked.Exchange(ref _activateRequested, 0) == 1)
        {
            ShowMainWindow();
        }

        // 托盘常驻时定期修剪工作集（只影响物理页占用，不影响功能）
        bool visibleNow = _mainForm.IsUiActive;
        if (_wasVisible && !visibleNow)
        {
            _lastTrimUtc = DateTime.UtcNow - TrimInterval;
        }

        _wasVisible = visibleNow;
        if (!visibleNow && DateTime.UtcNow - _lastTrimUtc >= TrimInterval)
        {
            _lastTrimUtc = DateTime.UtcNow;
            MemoryTrimmer.Trim();
        }

        var settings = _host.Settings;
        if (!settings.EnableAlerts)
        {
            return;
        }

        SnapshotFrame? frame = _host.Latest;
        if (frame is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(settings.AlertCooldownSeconds, 30, 3600));

        if (frame.SystemCpuPercent >= settings.AlertCpuPercent && now - _lastCpuAlertUtc >= cooldown)
        {
            _lastCpuAlertUtc = now;
            ShowBalloon(
                "CPU 占用告警",
                "整机 CPU " + FormatUtil.Percent(frame.SystemCpuPercent) + "，已超过阈值 " +
                settings.AlertCpuPercent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%。");
        }

        if (frame.SystemMemoryPercent >= settings.AlertMemoryPercent && now - _lastMemoryAlertUtc >= cooldown)
        {
            _lastMemoryAlertUtc = now;
            ShowBalloon(
                "内存占用告警",
                "物理内存 " + FormatUtil.Percent(frame.SystemMemoryPercent) + "，已超过阈值 " +
                settings.AlertMemoryPercent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%。");
        }
    }

    private void ShowBalloon(string title, string message)
    {
        try
        {
            _tray.ShowBalloonTip(6000, title, message, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "显示托盘气泡失败", ex);
        }
    }

    private void ShowMainWindow()
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            _mainForm.ShowFromTray();
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "显示主窗口失败", ex);
        }
    }

    private void ToggleStartup()
    {
        bool enable = !_host.Settings.RunAtStartup;
        if (!_demo && !StartupRegistration.TryApply(enable, out string? error))
        {
            ShowBalloon("开机启动设置失败", error ?? "无法写入注册表 Run 项。");
            SyncMenuState();
            return;
        }

        var next = _host.Settings.Clone();
        next.RunAtStartup = enable;
        if (!_host.ApplySettings(next))
        {
            ShowBalloon("保存设置失败", _host.LastError ?? "详情见日志。");
        }

        SyncMenuState();
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        if (_exiting)
        {
            return;
        }

        _pauseItem.Text = paused ? "继续监控" : "暂停监控";
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (_exiting)
        {
            return;
        }

        SyncMenuState();
    }

    private void SyncMenuState()
    {
        _pauseItem.Text = _host.IsPaused ? "继续监控" : "暂停监控";
        _pauseItem.Checked = _host.IsPaused;
        _startupItem.Checked = _host.Settings.RunAtStartup;
        _showItem.Text = "显示主窗口";
        _tray.Text = _demo ? "PerfScope（演示模式）" : "PerfScope — 应用级 CPU / 内存监控";
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        try
        {
            _alertTimer.Stop();
            _selfTestTimer?.Stop();
        }
        catch
        {
            // 忽略定时器停止失败
        }

        _mainForm.PrepareForExit();
        _tray.Visible = false;

        try
        {
            _host.Engine.Stop();
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "退出时停止采样失败", ex);
        }

        try
        {
            _mainForm.Close();
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "退出时关闭主窗口失败", ex);
        }

        ExitThread();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _host.PauseChanged -= OnPauseChanged;
            _host.SettingsChanged -= OnSettingsChanged;

            try
            {
                _alertTimer.Stop();
                _alertTimer.Dispose();
            }
            catch
            {
                // 忽略
            }

            if (_selfTestTimer is not null)
            {
                _selfTestTimer.Stop();
                _selfTestTimer.Dispose();
            }

            if (_activateWait is not null)
            {
                try
                {
                    _activateWait.Unregister(null);
                }
                catch
                {
                    // 忽略
                }
            }

            _activateEvent?.Dispose();

            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon.Dispose();
            _menu.Dispose();

            _mainForm.Dispose();
            _host.Dispose();
        }

        base.Dispose(disposing);
    }
}