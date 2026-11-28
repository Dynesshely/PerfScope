using System.Collections.ObjectModel;
using System.Windows.Input;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.App.Services;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.ViewModels;

/// <summary>应用列表的排序列。</summary>
public enum AppSortColumn
{
    Cpu = 0,
    Memory = 1,
    Name = 2,
    ProcessCount = 3,
    Io = 4,
}

/// <summary>
/// 主界面 ViewModel：唯一的「数据装配点」。
/// 帧从采样线程进来 → 投递到 UI 线程 → 原地更新行对象与头部指标，视图只做绑定。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly ThemeService _theme;
    private readonly IconService _icons;
    private readonly IDialogService _dialogs;
    private readonly UiState _state;
    private readonly Dictionary<string, AppRowViewModel> _rows = new(StringComparer.Ordinal);

    private DateTime _lastUiUpdateUtc = DateTime.MinValue;
    private bool _isPaused;
    private bool _isTreemapVisible;
    private string _sortColumnName = "cpu";
    private bool _sortDescending = true;
    private int _distributionTab;
    private string _statusText = string.Empty;
    private string _systemCpuText = "-";
    private string _systemMemoryText = "-";
    private string _memoryDetailText = string.Empty;
    private string _processCountText = "-";
    private string _selfUsageText = string.Empty;
    private string _intervalText = string.Empty;
    private string _lastUpdateText = string.Empty;
    private bool _hasData;
    private bool _isPermissionLimited;
    private int _logicalProcessors = Environment.ProcessorCount;

    /// <summary>创建主界面 ViewModel。</summary>
    public MainViewModel(
        AppHost host,
        IUiDispatcher dispatcher,
        ThemeService theme,
        IconService icons,
        IDialogService dialogs,
        UiState state)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        _isTreemapVisible = state.TreemapVisible;
        _distributionTab = state.DistributionTab;
        _sortColumnName = state.SortColumn;
        _sortDescending = state.SortDescending;

        TogglePauseCommand = new RelayCommand(TogglePause);
        RefreshNowCommand = new RelayCommand(RefreshNow);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenHistoryCommand = new RelayCommand(() => _dialogs.ShowHistory());
        OpenAboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        OpenAgentCommand = new RelayCommand(() => _dialogs.ShowAgentHandoff());
        ToggleTreemapCommand = new RelayCommand(() => IsTreemapVisible = !IsTreemapVisible);
        ShowMemoryTabCommand = new RelayCommand(() => DistributionTab = 0);
        ShowCpuTabCommand = new RelayCommand(() => DistributionTab = 1);
        SortByCommand = new RelayCommand(parameter =>
        {
            if (parameter is string name)
            {
                SortBy(name);
            }
        });

        Treemap.Metric = _distributionTab == 0 ? TreemapMetric.Memory : TreemapMetric.Cpu;
        Treemap.SetTheme(theme.IsDark);
        Treemap.SetMemoryMode(_host.Settings.MemoryMode == MemoryDisplay.PrivateWorkingSet);
        theme.ThemeChanged += OnThemeChanged;

        _host.Engine.FrameProduced += OnFrameProduced;
        _host.PauseChanged += OnPauseChanged;
        _host.SettingsChanged += OnSettingsChanged;

        IsPaused = _host.IsPaused;
        IntervalText = FormatInterval(_host.EffectiveIntervalMs);
        ApplySettings(_host.Settings);

        if (_host.Latest is { } latest)
        {
            ApplyFrame(latest);
        }

        if (UiDebug.ExpandFirstGroup && Apps.Count > 0)
        {
            Apps[0].IsExpanded = true;
        }
    }

    /// <summary>可见的应用行（已排序、已按 TopN 截断）。</summary>
    public ObservableCollection<AppRowViewModel> Apps { get; } = new();

    /// <summary>分布视图（树图）。</summary>
    public TreemapPanelViewModel Treemap { get; } = new();

    /// <summary>系统 CPU 趋势（最近 60 个采样点，百分比）。</summary>
    public TrendSeriesViewModel CpuTrend { get; } = new(60);

    /// <summary>系统内存趋势（最近 60 个采样点，百分比）。</summary>
    public TrendSeriesViewModel MemoryTrend { get; } = new(60);

    /// <summary>暂停 / 继续监控。</summary>
    public ICommand TogglePauseCommand { get; }

    /// <summary>立即采样一次。</summary>
    public ICommand RefreshNowCommand { get; }

    /// <summary>打开设置。</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>打开历史。</summary>
    public ICommand OpenHistoryCommand { get; }

    /// <summary>打开关于。</summary>
    public ICommand OpenAboutCommand { get; }

    /// <summary>打开「接入 Agent」。</summary>
    public ICommand OpenAgentCommand { get; }

    /// <summary>显示 / 隐藏右侧分布视图。</summary>
    public ICommand ToggleTreemapCommand { get; }

    /// <summary>切换到内存分布。</summary>
    public ICommand ShowMemoryTabCommand { get; }

    /// <summary>切换到 CPU 分布。</summary>
    public ICommand ShowCpuTabCommand { get; }

    /// <summary>按列名（cpu / memory / name / process / io）排序。</summary>
    public ICommand SortByCommand { get; }

    /// <summary>是否暂停。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseGlyph));
                OnPropertyChanged(nameof(PauseText));
            }
        }
    }

    /// <summary>暂停按钮字形。</summary>
    public string PauseGlyph => IsPaused ? "\uE768" : "\uE769";

    /// <summary>暂停按钮文案。</summary>
    public string PauseText => IsPaused ? Localization.Loc.T("继续") : Localization.Loc.T("暂停");

    /// <summary>右侧分布视图是否显示。</summary>
    public bool IsTreemapVisible
    {
        get => _isTreemapVisible;
        set
        {
            if (SetProperty(ref _isTreemapVisible, value))
            {
                _state.TreemapVisible = value;
                OnPropertyChanged(nameof(TreemapColumnWidth));
            }
        }
    }

    /// <summary>分布视图列宽（隐藏时为 0）。</summary>
    public System.Windows.GridLength TreemapColumnWidth
        => IsTreemapVisible
            ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            : new System.Windows.GridLength(0);

    /// <summary>分布视图标签：0 = 内存，1 = CPU。</summary>
    public int DistributionTab
    {
        get => _distributionTab;
        set
        {
            if (SetProperty(ref _distributionTab, value))
            {
                _state.DistributionTab = value;
                Treemap.Metric = value == 0 ? TreemapMetric.Memory : TreemapMetric.Cpu;
                OnPropertyChanged(nameof(IsMemoryTab));
                OnPropertyChanged(nameof(IsCpuTab));
                OnPropertyChanged(nameof(TreemapMetricText));
            }
        }
    }

    /// <summary>当前是否内存分布。</summary>
    public bool IsMemoryTab => DistributionTab == 0;

    /// <summary>当前是否 CPU 分布。</summary>
    public bool IsCpuTab => DistributionTab == 1;

    /// <summary>分布视图的度量名称。</summary>
    public string TreemapMetricText => IsMemoryTab ? Localization.Loc.T("内存") : Localization.Loc.T("CPU");

    /// <summary>整机 CPU 文本。</summary>
    public string SystemCpuText
    {
        get => _systemCpuText;
        private set => SetProperty(ref _systemCpuText, value);
    }

    /// <summary>整机内存占用文本。</summary>
    public string SystemMemoryText
    {
        get => _systemMemoryText;
        private set => SetProperty(ref _systemMemoryText, value);
    }

    /// <summary>内存明细（已用 / 总量）。</summary>
    public string MemoryDetailText
    {
        get => _memoryDetailText;
        private set => SetProperty(ref _memoryDetailText, value);
    }

    /// <summary>进程总数文本。</summary>
    public string ProcessCountText
    {
        get => _processCountText;
        private set => SetProperty(ref _processCountText, value);
    }

    /// <summary>监控器自身开销文本。</summary>
    public string SelfUsageText
    {
        get => _selfUsageText;
        private set => SetProperty(ref _selfUsageText, value);
    }

    /// <summary>当前采样间隔文本。</summary>
    public string IntervalText
    {
        get => _intervalText;
        private set => SetProperty(ref _intervalText, value);
    }

    /// <summary>最近一次刷新时刻文本。</summary>
    public string LastUpdateText
    {
        get => _lastUpdateText;
        private set => SetProperty(ref _lastUpdateText, value);
    }

    /// <summary>是否已经有数据（空状态用）。</summary>
    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (SetProperty(ref _hasData, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>是否处于空状态。</summary>
    public bool IsEmpty => !HasData;

    /// <summary>权限受限提示是否显示。</summary>
    public bool IsPermissionLimited
    {
        get => _isPermissionLimited;
        private set => SetProperty(ref _isPermissionLimited, value);
    }

    /// <summary>状态栏右侧文案（最近一次操作的提示或错误）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>当前排序列名。</summary>
    public string SortColumn => _sortColumnName;

    /// <summary>列头文案：带当前排序方向箭头。</summary>
    public string CpuHeaderText => HeaderText("cpu", "CPU");

    /// <summary>内存列头文案。</summary>
    public string MemoryHeaderText => HeaderText("memory", Localization.Loc.T("内存"));

    /// <summary>名称列头文案。</summary>
    public string NameHeaderText => HeaderText("name", Localization.Loc.T("应用"));

    /// <summary>进程数列头文案。</summary>
    public string ProcessHeaderText => HeaderText("process", Localization.Loc.T("进程"));

    /// <summary>IO 列头文案。</summary>
    public string IoHeaderText => HeaderText("io", "IO");

    private string HeaderText(string column, string label)
        => string.Equals(_sortColumnName, column, StringComparison.Ordinal)
            ? label + (_sortDescending ? " ↓" : " ↑")
            : label;

    /// <summary>是否降序。</summary>
    public bool SortDescending => _sortDescending;

    /// <summary>把窗口可见性告知引擎（隐藏时按设置降频）。</summary>
    public void SetWindowVisible(bool visible)
    {
        _host.SetUiVisible(visible);
        if (!visible)
        {
            SaveState();
        }
    }

    /// <summary>保存界面状态（窗口关闭 / 隐藏时调用）。</summary>
    public void SaveState()
    {
        _state.SortColumn = _sortColumnName;
        _state.SortDescending = _sortDescending;
        _state.DistributionTab = DistributionTab;
        _state.TreemapVisible = IsTreemapVisible;
        UiStateStore.Save(_state);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _host.Engine.FrameProduced -= OnFrameProduced;
        _host.PauseChanged -= OnPauseChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Treemap.SetTheme(_theme.IsDark);
        _icons.ClearFallbacks();
        foreach (var row in _rows.Values)
        {
            row.Icon = null;
        }
    }

    private void OnFrameProduced(object? sender, SnapshotFrame frame)
    {
        // 采样线程不能碰 UI：投递到 UI 线程；并按设置里的界面刷新间隔做节流
        _dispatcher.Post(() =>
        {
            int interval = Math.Max(200, _host.Settings.UiRefreshIntervalMs);
            if ((DateTime.UtcNow - _lastUiUpdateUtc).TotalMilliseconds < interval * 0.6)
            {
                return;
            }

            ApplyFrame(frame);
        });
    }

    private void ApplyFrame(SnapshotFrame frame)
    {
        _lastUiUpdateUtc = DateTime.UtcNow;
        _logicalProcessors = Environment.ProcessorCount;

        SystemCpuText = FormatUtil.Percent(frame.SystemCpuPercent);
        SystemMemoryText = FormatUtil.Percent(frame.SystemMemoryPercent);
        MemoryDetailText = FormatUtil.Bytes((double)(frame.TotalPhysicalBytes - frame.AvailablePhysicalBytes))
                           + " / " + FormatUtil.Bytes((double)frame.TotalPhysicalBytes);
        ProcessCountText = frame.ProcessCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        SelfUsageText = FormatUtil.Percent(frame.SelfCpuPercent) + " · " + FormatUtil.Bytes(frame.SelfWorkingSetBytes);
        IntervalText = FormatInterval(_host.EffectiveIntervalMs);
        LastUpdateText = frame.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        HasData = true;
        IsPermissionLimited = !_host.Settings.ShowSelfUsage ? IsPermissionLimited : IsPermissionLimited;

        if (ShowSelfUsage)
        {
            CpuTrend.Push(frame.SystemCpuPercent);
            MemoryTrend.Push(frame.SystemMemoryPercent);
        }

        UpdateRows(frame);
        Treemap.Update(frame.Apps);
    }

    private void UpdateRows(SnapshotFrame frame)
    {
        var settings = _host.Settings;
        bool preferPrivate = settings.MemoryMode == MemoryDisplay.PrivateWorkingSet;
        var visible = new List<AppRowViewModel>(frame.Apps.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sample in frame.Apps)
        {
            if (!settings.ShowSystemProcesses && sample.Kind == AppKind.SystemProcess)
            {
                continue;
            }

            if (!_rows.TryGetValue(sample.Key, out var row))
            {
                row = new AppRowViewModel(sample.Key);
                _rows[sample.Key] = row;
            }

            row.Update(sample, preferPrivate);
            if (row.Icon is null)
            {
                row.Icon = _icons.GetIcon(sample.IconSourcePath, sample.DisplayName);
            }

            seen.Add(sample.Key);
            visible.Add(row);
        }

        // 清理本帧消失的应用行（避免进程退出后越积越多）
        if (_rows.Count > seen.Count)
        {
            var stale = _rows.Keys.Where(key => !seen.Contains(key)).ToArray();
            foreach (string key in stale)
            {
                _rows.Remove(key);
            }
        }

        SortRows(visible);
        int limit = Math.Clamp(settings.TopN, 5, 200);
        if (visible.Count > limit)
        {
            visible.RemoveRange(limit, visible.Count - limit);
        }

        Synchronize(Apps, visible);
    }

    private void SortRows(List<AppRowViewModel> rows)
    {
        Comparison<AppRowViewModel> comparison = _sortColumnName switch
        {
            "memory" => (a, b) => a.MemoryBytes.CompareTo(b.MemoryBytes),
            "name" => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
            "process" => (a, b) => a.ProcessCount.CompareTo(b.ProcessCount),
            "io" => (a, b) => (a.ReadBytesPerSecond + a.WriteBytesPerSecond).CompareTo(b.ReadBytesPerSecond + b.WriteBytesPerSecond),
            _ => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
        };

        rows.Sort(comparison);
        if (_sortDescending)
        {
            rows.Reverse();
        }
    }

    /// <summary>把目标列表同步到可观察集合：相同前缀原样保留，只替换差异部分。</summary>
    private static void Synchronize(ObservableCollection<AppRowViewModel> target, List<AppRowViewModel> source)
    {
        // 目标与源完全一致时不做任何操作（最常见的情况：只是数值变了，排序没变）
        if (target.Count == source.Count)
        {
            bool same = true;
            for (int i = 0; i < source.Count; i++)
            {
                if (!ReferenceEquals(target[i], source[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        int common = 0;
        while (common < target.Count && common < source.Count && ReferenceEquals(target[common], source[common]))
        {
            common++;
        }

        for (int i = target.Count - 1; i >= common; i--)
        {
            target.RemoveAt(i);
        }

        for (int i = common; i < source.Count; i++)
        {
            target.Add(source[i]);
        }
    }

    private void SortBy(string column)
    {
        if (string.Equals(_sortColumnName, column, StringComparison.Ordinal))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumnName = column;
            _sortDescending = true;
        }

        OnPropertiesChanged(nameof(SortColumn), nameof(SortDescending), nameof(CpuHeaderText),
            nameof(MemoryHeaderText), nameof(NameHeaderText), nameof(ProcessHeaderText), nameof(IoHeaderText));

        if (_host.Latest is { } latest)
        {
            UpdateRows(latest);
        }
    }

    private void TogglePause()
    {
        bool next = !IsPaused;
        if (!_host.SetPaused(next))
        {
            StatusText = _host.LastError ?? Localization.Loc.T("切换暂停状态失败");
            return;
        }

        StatusText = next ? Localization.Loc.T("已暂停监控") : Localization.Loc.T("已继续监控");
    }

    private void RefreshNow()
    {
        if (!_host.ForceSample())
        {
            StatusText = _host.LastError ?? Localization.Loc.T("立即采样失败");
        }
    }

    private void OpenSettings()
    {
        var current = _host.Settings.Clone();
        if (_dialogs.ShowSettings(current))
        {
            StatusText = _host.LastError ?? Localization.Loc.T("设置已保存");
        }
    }

    private void OnPauseChanged(object? sender, bool paused) => _dispatcher.Post(() => IsPaused = paused);

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() => ApplySettings(settings));

    private void ApplySettings(AppSettings settings)
    {
        Treemap.SetMemoryMode(settings.MemoryMode == MemoryDisplay.PrivateWorkingSet);
        OnPropertyChanged(nameof(ShowSelfUsage));
        IsPaused = _host.IsPaused;
        IntervalText = FormatInterval(_host.EffectiveIntervalMs);
    }

    /// <summary>是否在状态栏显示自身开销。</summary>
    public bool ShowSelfUsage => _host.Settings.ShowSelfUsage;

    private static string FormatInterval(int milliseconds)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Localization.Loc.T("采样间隔 {0} 秒"), (milliseconds / 1000d).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
}
