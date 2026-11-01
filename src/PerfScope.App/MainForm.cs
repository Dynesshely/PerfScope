using PerfScope.App.Controls;
using PerfScope.App.Forms;
using PerfScope.App.Infrastructure;
using PerfScope.Core;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App;

/// <summary>
/// 主窗口：顶部整机指标 + 中部应用列表 + 底部状态栏。
/// 数据来自引擎的最新帧，按 <see cref="AppSettings.UiRefreshIntervalMs"/> 节流刷新；
/// 窗口不可见时停掉 UI 定时器并通知引擎降频。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppHost _host;
    private readonly IconProvider _icons = new();
    private readonly TableLayoutPanel _layout;
    private readonly SystemHeaderControl _header;
    private readonly AppListControl _list;
    private readonly DistributionView _distribution;
    private readonly SplitGripContainer _split;
    private ToolStripButton _treemapButton = null!;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _pauseButton;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _intervalLabel;
    private readonly ToolStripStatusLabel _frameLabel;
    private readonly ToolStripStatusLabel _selfLabel;
    private readonly ToolStripStatusLabel _processLabel;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly TrendRing _cpuRing = new(120);
    private readonly TrendRing _memoryRing = new(120);
    private readonly Dictionary<string, TrendRing> _appTrends = new(StringComparer.Ordinal);
    private readonly Action _refreshAction;
    private readonly UiState _uiState;

    private Theme _theme;
    private float _themeScale;
    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private long _lastUiRefreshTicks;
    private long _frameCount;
    private bool _forceExit;
    private bool _disposed;
    private bool _applyingUiState;

    /// <summary>创建主窗口。</summary>
    public MainForm(AppHost host)
    {
        _host = host;
        _uiState = UiStateStore.Load();
        _themeScale = Theme.DetectDpiScale();
        _theme = Theme.Create(host.Settings.Theme, _themeScale);

        Text = "PerfScope — 应用级 CPU / 内存监控";
        var windowIcon = AppIcon.Get(32);
        if (windowIcon is not null)
        {
            Icon = windowIcon;
        }
        Name = "MainForm";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = Scaled(880, 540);
        Size = Scaled(1120, 760);
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _header = new SystemHeaderControl(_theme)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        _list = new AppListControl(_theme, _icons)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        _distribution = new DistributionView(_theme, _icons)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        _split = new SplitGripContainer(_theme)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_distribution);

        _toolbar = BuildToolbar(out _pauseButton);
        _status = BuildStatusBar(out _intervalLabel, out _frameLabel, out _selfLabel, out _processLabel);
        _status.Tag = "v" + AppPaths.Version;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = _theme.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Scaled(148)));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.Controls.Add(_toolbar, 0, 0);
        _layout.Controls.Add(_header, 0, 1);
        _layout.Controls.Add(_split, 0, 2);
        _layout.Controls.Add(_status, 0, 3);

        Controls.Add(_layout);

        _refreshAction = RefreshView;
        _uiTimer = new System.Windows.Forms.Timer { Interval = ComputeUiInterval(host.Settings) };
        _uiTimer.Tick += OnUiTimerTick;

        _list.SetTrendSource(GetAppTrend);
        ApplyListSettings(host.Settings);
        RestoreWindowState();
        UpdateStatusVisibility(host.Settings);

        FormStyler.ApplyCaptionTheme(this, _theme);

        host.Engine.FrameProduced += OnFrameProduced;
        host.SettingsChanged += OnSettingsChanged;
        host.PauseChanged += OnPauseChanged;

        _pauseButton.Text = host.IsPaused ? "继续监控" : "暂停监控";
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FormStyler.ApplyCaptionTheme(this, _theme);
    }

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitState();
    }

    /// <summary>按保存的界面状态恢复左右分栏比例与树图可见性。</summary>
    private void ApplySplitState()
    {
        _applyingUiState = true;
        try
        {
            bool visible = _uiState.TreemapVisible;
            _treemapButton.Checked = visible;
            _split.Panel2Collapsed = !visible;
            _distribution.SelectedIndex = _uiState.DistributionTab;
            if (!visible || _split.Width <= 0)
            {
                return;
            }

            double ratio = _uiState.SplitRatio;
            if (ratio <= 0.2 || ratio >= 0.9)
            {
                ratio = 0.62;
            }

            int total = _split.Width - _split.SplitterWidth;
            if (total <= Scaled(200))
            {
                return;
            }

            // 先把分割位置放到合法区间，再设最小尺寸：SplitContainer 每次赋值都会做区间校验
            int distance = Math.Clamp((int)Math.Round(total * ratio), Scaled(120), total - Scaled(120));
            _split.SplitterDistance = distance;
            _split.Panel1MinSize = Math.Min(Scaled(360), Math.Max(0, distance - Scaled(40)));
            _split.Panel2MinSize = Math.Min(Scaled(200), Math.Max(0, total - distance - Scaled(40)));
        }
        finally
        {
            _applyingUiState = false;
        }
    }

    /// <summary>显示 / 隐藏右侧内存树图。</summary>
    private void ToggleTreemap(bool visible)
    {
        _split.Panel2Collapsed = !visible;
        if (visible)
        {
            ApplySplitState();
        }

        _uiState.TreemapVisible = visible;
    }

    /// <summary>用户关闭窗口且行为为「退出」时触发，由托盘上下文收尾。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>界面是否处于活跃状态（可见且未最小化）；托盘态为 <c>false</c>。</summary>
    internal bool IsUiActive => Visible && WindowState != FormWindowState.Minimized;

    /// <summary>当前主题（供托盘菜单复用配色）。</summary>
    internal Theme CurrentTheme => _theme;

    /// <summary>退出前调用：允许窗口真正关闭并保存界面状态。</summary>
    public void PrepareForExit()
    {
        _forceExit = true;
        SaveWindowState();
    }

    /// <summary>从托盘唤起主窗口。</summary>
    public void ShowFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
        RefreshView();
    }

    /// <summary>暂停 / 继续监控（供托盘菜单与工具条共用）。</summary>
    public void TogglePause()
    {
        _host.SetPaused(!_host.IsPaused);
    }

    /// <summary>立即采样并刷新界面。</summary>
    public void ForceRefresh()
    {
        _host.ForceSample();
        RefreshView();
    }

    /// <summary>打开设置页。</summary>
    public void OpenSettings()
    {
        using var form = new SettingsForm(_host);
        form.ShowDialog(this);
    }

    /// <summary>生成分析报告并打开所在目录。</summary>
    private void OpenAnalysis()
    {
        try
        {
            _host.WriteAnalysisReport();
            var directory = _host.AnalysisDirectory;
            if (Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, "打开分析报告失败", ex);
        }
    }

    /// <summary>打开当天历史汇总。</summary>
    public void OpenHistory()
    {
        using var form = new HistoryForm(_host);
        form.ShowDialog(this);
    }

    private ToolStrip BuildToolbar(out ToolStripButton pauseButton)
    {
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = new FlatToolStripRenderer(_theme),
            BackColor = _theme.Surface,
            ForeColor = _theme.TextPrimary,
            Font = _theme.UiFont,
            Padding = new Padding(Scaled(8), Scaled(4), Scaled(8), Scaled(4)),
            Margin = Padding.Empty,
            CanOverflow = false,
        };

        var title = new ToolStripLabel("PerfScope")
        {
            Font = _theme.UiBoldFont,
            Tag = _theme.Accent,
            Margin = new Padding(Scaled(2), 0, Scaled(12), 0),
        };
        toolbar.Items.Add(title);

        pauseButton = new ToolStripButton("暂停监控")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(0, 0, Scaled(2), 0),
        };
        pauseButton.Click += (_, _) => TogglePause();
        toolbar.Items.Add(pauseButton);

        var refreshButton = new ToolStripButton("立即刷新")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(0, 0, Scaled(2), 0),
        };
        refreshButton.Click += (_, _) => ForceRefresh();
        toolbar.Items.Add(refreshButton);

        toolbar.Items.Add(new ToolStripSeparator());

        var settingsButton = new ToolStripButton("设置")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(0, 0, Scaled(2), 0),
        };
        settingsButton.Click += (_, _) => OpenSettings();
        toolbar.Items.Add(settingsButton);

        var historyButton = new ToolStripButton("历史")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Margin = new Padding(0, 0, Scaled(2), 0),
        };
        historyButton.Click += (_, _) => OpenHistory();
        toolbar.Items.Add(historyButton);

        var analysisButton = new ToolStripButton("分析")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "生成并打开 AI 可读的资源分析报告",
        };
        analysisButton.Click += (_, _) => OpenAnalysis();
        toolbar.Items.Add(analysisButton);

        _treemapButton = new ToolStripButton("分布视图")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = true,
            Checked = true,
            ToolTipText = "显示 / 隐藏右侧内存分布图（面积 = 内存占用）",
        };
        _treemapButton.CheckedChanged += (_, _) =>
        {
            if (!_applyingUiState)
            {
                ToggleTreemap(_treemapButton.Checked);
            }
        };
        toolbar.Items.Add(_treemapButton);

        if (_host.IsDemo)
        {
            toolbar.Items.Add(new ToolStripLabel("演示模式")
            {
                Tag = _theme.Warning,
                Font = _theme.UiBoldFont,
            });
        }

        var selfUsageHint = new ToolStripLabel("双击托盘图标唤醒窗口")
        {
            Alignment = ToolStripItemAlignment.Right,
            Tag = _theme.TextMuted,
            Font = _theme.SmallFont,
        };
        toolbar.Items.Add(selfUsageHint);

        return toolbar;
    }

    private StatusStrip BuildStatusBar(
        out ToolStripStatusLabel intervalLabel,
        out ToolStripStatusLabel frameLabel,
        out ToolStripStatusLabel selfLabel,
        out ToolStripStatusLabel processLabel)
    {
        var strip = new StatusStrip
        {
            Dock = DockStyle.Fill,
            Renderer = _toolbar.Renderer,
            BackColor = _theme.Surface,
            ForeColor = _theme.TextSecondary,
            Font = _theme.SmallFont,
            SizingGrip = false,
            Padding = new Padding(Scaled(8), 0, Scaled(8), 0),
            Margin = Padding.Empty,
        };

        intervalLabel = new ToolStripStatusLabel("采样间隔 —");
        frameLabel = new ToolStripStatusLabel("帧 —");
        selfLabel = new ToolStripStatusLabel("自身 —");
        processLabel = new ToolStripStatusLabel("进程 —") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        var versionLabel = new ToolStripStatusLabel("v" + AppPaths.Version) { ForeColor = _theme.TextMuted };

        ToolStripStatusLabel Divider() => new("│")
        {
            ForeColor = _theme.TextMuted,
            Margin = new Padding(Scaled(6), 0, Scaled(6), 0),
        };

        strip.Items.AddRange(new ToolStripItem[]
        {
            intervalLabel,
            Divider(),
            frameLabel,
            Divider(),
            selfLabel,
            processLabel,
            Divider(),
            versionLabel,
        });
        return strip;
    }

    private int Scaled(int value) => (int)Math.Round(value * _themeScale);

    private Size Scaled(int width, int height) => new(Scaled(width), Scaled(height));

    private static int ComputeUiInterval(AppSettings settings) => Math.Clamp(settings.UiRefreshIntervalMs, 250, 10000);

    private void ApplyListSettings(AppSettings settings)
    {
        _list.TopN = settings.TopN;
        _list.MemoryMode = settings.MemoryMode;
        _list.SetFilter(app => settings.ShowSystemProcesses ||
                                (app.Kind != AppKind.SystemProcess && app.Kind != AppKind.SystemModule));
        _list.RefreshRows();
    }

    private void RestoreWindowState()
    {
        if (_uiState.Width >= 320 && _uiState.Height >= 240)
        {
            var bounds = new Rectangle(_uiState.X, _uiState.Y, _uiState.Width, _uiState.Height);
            if (IsVisibleOnAnyScreen(bounds))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }

        if (_uiState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        _list.RestoreSort(_uiState.SortColumn, _uiState.SortDescending);
    }

    private static bool IsVisibleOnAnyScreen(Rectangle bounds)
    {
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(bounds))
            {
                return true;
            }
        }

        return false;
    }

    private void SaveWindowState()
    {
        try
        {
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var state = new UiState
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == FormWindowState.Maximized,
                SortColumn = _list.SortColumnName,
                SortDescending = _list.SortDescending,
                HiddenToTray = !Visible,
                TreemapVisible = !_split.Panel2Collapsed,
                DistributionTab = _distribution.SelectedIndex,
                SplitRatio = _split.Panel2Collapsed || _split.Width <= 0
                    ? _uiState.SplitRatio
                    : Math.Clamp(
                        (double)_split.SplitterDistance / Math.Max(1, _split.Width - _split.SplitterWidth),
                        0.2,
                        0.9),
            };
            UiStateStore.Save(state);
        }
        catch
        {
            // 界面状态保存失败不影响退出
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        RefreshView();
    }

    /// <summary>引擎帧回调（后台线程）：节流后再投递到 UI 线程，避免每帧都刷。</summary>
    private void OnFrameProduced(object? sender, SnapshotFrame frame)
    {
        if (_disposed || !IsHandleCreated || !Visible)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastUiRefreshTicks) < Math.Max(120, ComputeUiInterval(_host.Settings)))
        {
            // 距上次刷新太近：交给下一次 UI 定时器 tick 处理
            return;
        }

        try
        {
            BeginInvoke(_refreshAction);
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁或句柄已释放
        }
    }

    private void RefreshView()
    {
        if (_disposed || IsDisposed)
        {
            return;
        }

        Interlocked.Exchange(ref _lastUiRefreshTicks, Environment.TickCount64);

        SnapshotFrame? frame = _host.Latest;
        if (frame is null)
        {
            _intervalLabel.Text = "等待首帧采样…";
            return;
        }

        if (frame.TimestampUtc != _lastFrameTimestamp)
        {
            _lastFrameTimestamp = frame.TimestampUtc;
            _frameCount++;

            _cpuRing.Push(frame.SystemCpuPercent);
            _memoryRing.Push(frame.SystemMemoryPercent);
            PushAppTrends(frame);

            _header.Push(
                frame.SystemCpuPercent,
                frame.SystemMemoryPercent,
                frame.TotalPhysicalBytes,
                frame.AvailablePhysicalBytes,
                frame.ProcessCount,
                frame.SelfCpuPercent,
                frame.SelfWorkingSetBytes);

            _list.SetPhysicalMemory(frame.TotalPhysicalBytes);
            _list.SetApps(frame.Apps ?? Array.Empty<AppSample>());

            _distribution.SetPhysicalMemory(frame.TotalPhysicalBytes);
            _distribution.SetUsedMemory(frame.TotalPhysicalBytes - frame.AvailablePhysicalBytes);
            _distribution.SetApps(frame.Apps ?? Array.Empty<AppSample>());
        }

        UpdateStatusText(frame);
    }

    private void PushAppTrends(SnapshotFrame frame)
    {
        if (frame.Apps is null)
        {
            return;
        }

        int limit = Math.Min(frame.Apps.Length, 48);
        for (int i = 0; i < limit; i++)
        {
            var app = frame.Apps[i];
            if (app is null)
            {
                continue;
            }

            GetAppTrend(app.Key).Push(app.CpuPercent);
        }
    }

    private TrendRing GetAppTrend(string key)
    {
        if (_appTrends.TryGetValue(key, out var ring))
        {
            return ring;
        }

        if (_appTrends.Count > 200)
        {
            // 应用集合变化很大时整体重建，避免无限增长；40 秒内自然恢复
            _appTrends.Clear();
        }

        ring = new TrendRing(40);
        _appTrends[key] = ring;
        return ring;
    }

    private void UpdateStatusText(SnapshotFrame frame)
    {
        var settings = _host.Settings;
        int interval = _host.EffectiveIntervalMs;
        string pauseHint = _host.IsPaused ? " · 已暂停" : string.Empty;
        _intervalLabel.Text = "采样间隔 " + interval.ToString(System.Globalization.CultureInfo.CurrentCulture) + " ms" + pauseHint;
        _frameLabel.Text = "帧 " + _frameCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _processLabel.Text = "进程 " + frame.ProcessCount.ToString(System.Globalization.CultureInfo.CurrentCulture) +
                             " 个 · 应用 " + frame.Apps.Length.ToString(System.Globalization.CultureInfo.CurrentCulture) + " 个";
        _selfLabel.Text = "自身 " + FormatUtil.Percent(frame.SelfCpuPercent) + " / " + FormatUtil.Bytes(frame.SelfWorkingSetBytes);
        _selfLabel.Visible = settings.ShowSelfUsage;
    }

    private void UpdateStatusVisibility(AppSettings settings)
    {
        _selfLabel.Visible = settings.ShowSelfUsage;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        _uiTimer.Interval = ComputeUiInterval(settings);
        ApplyListSettings(settings);
        UpdateStatusVisibility(settings);

        bool wantDark = !string.Equals(settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
        bool needTheme = _theme.IsDark != wantDark || Math.Abs(Theme.DetectDpiScale() - _themeScale) > 0.01f;
        if (needTheme)
        {
            RebuildTheme();
        }

        _intervalLabel.Text = "采样间隔 " + _host.EffectiveIntervalMs.ToString(System.Globalization.CultureInfo.CurrentCulture) + " ms";
        RefreshView();
    }

    private void OnPauseChanged(object? sender, bool paused)
    {
        if (_disposed)
        {
            return;
        }

        _pauseButton.Text = paused ? "继续监控" : "暂停监控";
        RefreshView();
    }

    private void RebuildTheme()
    {
        var previous = _theme;
        _themeScale = Theme.DetectDpiScale();
        _theme = Theme.Create(_host.Settings.Theme, _themeScale);

        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        _layout.BackColor = _theme.Background;
        _toolbar.Renderer = new FlatToolStripRenderer(_theme);
        _toolbar.BackColor = _theme.Surface;
        _toolbar.Font = _theme.UiFont;
        _status.Renderer = _toolbar.Renderer;
        _status.BackColor = _theme.Surface;
        _status.Font = _theme.SmallFont;
        _header.SetTheme(_theme);
        _list.SetTheme(_theme);
        _distribution.SetTheme(_theme);
        _split.SetTheme(_theme);
        _layout.RowStyles[1].Height = Scaled(148);

        foreach (ToolStripItem item in _toolbar.Items)
        {
            if (item is ToolStripLabel { Text: "PerfScope" })
            {
                item.Tag = _theme.Accent;
                item.Font = _theme.UiBoldFont;
            }
            else if (item is ToolStripLabel { Text: "演示模式" })
            {
                item.Tag = _theme.Warning;
                item.Font = _theme.UiBoldFont;
            }
            else if (item is ToolStripLabel { Tag: Color })
            {
                item.Tag = _theme.TextMuted;
                item.Font = _theme.SmallFont;
            }
        }

        previous.Dispose();
        Invalidate(true);
    }

    /// <inheritdoc />
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        bool active = Visible && WindowState != FormWindowState.Minimized;
        _host.SetUiVisible(active);
        if (active)
        {
            if (!_uiTimer.Enabled)
            {
                _uiTimer.Start();
            }

            RefreshView();
        }
        else
        {
            _uiTimer.Stop();
        }
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            _uiTimer.Stop();
            _host.SetUiVisible(false);
        }
        else if (Visible)
        {
            _host.SetUiVisible(true);
            if (!_uiTimer.Enabled)
            {
                _uiTimer.Start();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_forceExit)
        {
            SaveWindowState();
            return;
        }

        if (e.CloseReason == CloseReason.UserClosing && _host.Settings.CloseBehavior == CloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveWindowState();
    }

    /// <inheritdoc />
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (!_forceExit)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _host.Engine.FrameProduced -= OnFrameProduced;
            _host.SettingsChanged -= OnSettingsChanged;
            _host.PauseChanged -= OnPauseChanged;
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _icons.Dispose();
            _theme.Dispose();
        }

        base.Dispose(disposing);
    }
}