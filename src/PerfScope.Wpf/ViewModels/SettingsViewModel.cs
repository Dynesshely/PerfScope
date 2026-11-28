using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.App.Services;
using PerfScope.Core;
using PerfScope.Core.Diagnostics;
using PerfScope.Core.Settings;

namespace PerfScope.App.ViewModels;

/// <summary>
/// 设置页 ViewModel：所有字段都绑定到一份 <see cref="AppSettings"/> 草稿拷贝，
/// 点「保存」时统一校验并整体应用（落盘 + 热更新），点「取消」原样丢弃。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly AppSettings _draft;
    private RuleRowViewModel? _selectedRule;
    private int _selectedTab;
    private string _statusText = string.Empty;

    /// <summary>创建设置页 ViewModel。</summary>
    public SettingsViewModel(AppHost host, AppSettings current, IDialogService dialogs, int initialTab = 0)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(current);
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _draft = current.Clone();
        _selectedTab = Math.Clamp(initialTab, 0, 6);

        Rules = new ObservableCollection<RuleRowViewModel>(_draft.Rules.Select(r => new RuleRowViewModel(r)));

        CpuModeOptions = new[]
        {
            new OptionItem<CpuNormalization>(CpuNormalization.MachinePercent, Localization.Loc.T("整机百分比（与任务管理器一致）")),
            new OptionItem<CpuNormalization>(CpuNormalization.PerCorePercent, Localization.Loc.T("单核百分比（可超过 100%）")),
        };
        MemoryModeOptions = new[]
        {
            new OptionItem<MemoryDisplay>(MemoryDisplay.WorkingSet, Localization.Loc.T("工作集")),
            new OptionItem<MemoryDisplay>(MemoryDisplay.PrivateWorkingSet, Localization.Loc.T("私有字节")),
        };
        ThemeOptions = new[]
        {
            new OptionItem<string>("dark", Localization.Loc.T("深色")),
            new OptionItem<string>("light", Localization.Loc.T("浅色")),
        };
        LanguageOptions = new[]
        {
            new OptionItem<string>("system", Localization.Loc.T("跟随系统")),
            new OptionItem<string>("zh", "中文"),
            new OptionItem<string>("en", "English"),
        };
        CloseBehaviorOptions = new[]
        {
            new OptionItem<CloseBehavior>(CloseBehavior.MinimizeToTray, Localization.Loc.T("最小化到托盘")),
            new OptionItem<CloseBehavior>(CloseBehavior.Exit, Localization.Loc.T("直接退出")),
        };
        LogLevelOptions = new[]
        {
            new OptionItem<LogLevel>(LogLevel.Error, Localization.Loc.T("仅错误")),
            new OptionItem<LogLevel>(LogLevel.Warn, Localization.Loc.T("警告及以上")),
            new OptionItem<LogLevel>(LogLevel.Info, Localization.Loc.T("信息及以上")),
            new OptionItem<LogLevel>(LogLevel.Debug, Localization.Loc.T("调试（最详细）")),
        };
        RuleMatchOptions = new[]
        {
            new OptionItem<AppRuleMatch>(AppRuleMatch.ExeName, Localization.Loc.T("映像名")),
            new OptionItem<AppRuleMatch>(AppRuleMatch.PathContains, Localization.Loc.T("路径包含")),
            new OptionItem<AppRuleMatch>(AppRuleMatch.ProductName, Localization.Loc.T("产品名")),
        };
        RuleActionOptions = new[]
        {
            new OptionItem<AppRuleAction>(AppRuleAction.MergeToName, Localization.Loc.T("合并到指定应用")),
            new OptionItem<AppRuleAction>(AppRuleAction.Rename, Localization.Loc.T("仅重命名")),
            new OptionItem<AppRuleAction>(AppRuleAction.Exclude, Localization.Loc.T("排除")),
        };

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(ResetToDefaults);
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => _selectedRule is not null);
        MoveRuleUpCommand = new RelayCommand(() => MoveRule(-1), () => _selectedRule is not null);
        MoveRuleDownCommand = new RelayCommand(() => MoveRule(1), () => _selectedRule is not null);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
    }

    /// <summary>对话框服务。</summary>
    public IDialogService Dialogs { get; }

    /// <summary>规则列表。</summary>
    public ObservableCollection<RuleRowViewModel> Rules { get; }

    /// <summary>CPU 口径选项。</summary>
    public OptionItem<CpuNormalization>[] CpuModeOptions { get; }

    /// <summary>内存口径选项。</summary>
    public OptionItem<MemoryDisplay>[] MemoryModeOptions { get; }

    /// <summary>主题选项。</summary>
    public OptionItem<string>[] ThemeOptions { get; }

    /// <summary>语言选项。</summary>
    public OptionItem<string>[] LanguageOptions { get; }

    /// <summary>关闭行为选项。</summary>
    public OptionItem<CloseBehavior>[] CloseBehaviorOptions { get; }

    /// <summary>日志级别选项。</summary>
    public OptionItem<LogLevel>[] LogLevelOptions { get; }

    /// <summary>规则匹配方式选项。</summary>
    public OptionItem<AppRuleMatch>[] RuleMatchOptions { get; }

    /// <summary>规则动作选项。</summary>
    public OptionItem<AppRuleAction>[] RuleActionOptions { get; }

    /// <summary>保存命令。</summary>
    public ICommand SaveCommand { get; }

    /// <summary>恢复默认值。</summary>
    public ICommand ResetCommand { get; }

    /// <summary>新增规则。</summary>
    public ICommand AddRuleCommand { get; }

    /// <summary>删除选中规则。</summary>
    public ICommand RemoveRuleCommand { get; }

    /// <summary>规则上移。</summary>
    public ICommand MoveRuleUpCommand { get; }

    /// <summary>规则下移。</summary>
    public ICommand MoveRuleDownCommand { get; }

    /// <summary>打开数据目录。</summary>
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>导出脱敏诊断包。</summary>
    public ICommand ExportDiagnosticsCommand { get; }

    /// <summary>选中的标签页索引。</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    /// <summary>当前选中的规则。</summary>
    public RuleRowViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value))
            {
                OnPropertyChanged(nameof(HasSelectedRule));
                ((RelayCommand)RemoveRuleCommand).RaiseCanExecuteChanged();
                ((RelayCommand)MoveRuleUpCommand).RaiseCanExecuteChanged();
                ((RelayCommand)MoveRuleDownCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>是否有选中的规则。</summary>
    public bool HasSelectedRule => _selectedRule is not null;

    /// <summary>状态提示。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>保存成功后触发（窗口据此关闭）。</summary>
    public event EventHandler? Saved;

    /// <summary>采样间隔（毫秒）。</summary>
    public int SampleIntervalMs { get => _draft.SampleIntervalMs; set { _draft.SampleIntervalMs = value; OnPropertyChanged(); } }

    /// <summary>界面刷新间隔（毫秒）。</summary>
    public int UiRefreshIntervalMs { get => _draft.UiRefreshIntervalMs; set { _draft.UiRefreshIntervalMs = value; OnPropertyChanged(); } }

    /// <summary>平滑窗口。</summary>
    public int SmoothingWindow { get => _draft.SmoothingWindow; set { _draft.SmoothingWindow = value; OnPropertyChanged(); } }

    /// <summary>隐藏时降频。</summary>
    public bool AdaptiveThrottleWhenHidden { get => _draft.AdaptiveThrottleWhenHidden; set { _draft.AdaptiveThrottleWhenHidden = value; OnPropertyChanged(); } }

    /// <summary>隐藏时采样间隔。</summary>
    public int HiddenSampleIntervalMs { get => _draft.HiddenSampleIntervalMs; set { _draft.HiddenSampleIntervalMs = value; OnPropertyChanged(); } }

    /// <summary>CPU 口径。</summary>
    public CpuNormalization CpuMode { get => _draft.CpuMode; set { _draft.CpuMode = value; OnPropertyChanged(); } }

    /// <summary>内存口径。</summary>
    public MemoryDisplay MemoryMode { get => _draft.MemoryMode; set { _draft.MemoryMode = value; OnPropertyChanged(); } }

    /// <summary>主题。</summary>
    public string Theme { get => _draft.Theme; set { _draft.Theme = value; OnPropertyChanged(); } }

    /// <summary>语言。</summary>
    public string Language { get => _draft.Language; set { _draft.Language = value; OnPropertyChanged(); } }

    /// <summary>列表行数。</summary>
    public int TopN { get => _draft.TopN; set { _draft.TopN = value; OnPropertyChanged(); } }

    /// <summary>启动最小化。</summary>
    public bool StartMinimized { get => _draft.StartMinimized; set { _draft.StartMinimized = value; OnPropertyChanged(); } }

    /// <summary>显示自身开销。</summary>
    public bool ShowSelfUsage { get => _draft.ShowSelfUsage; set { _draft.ShowSelfUsage = value; OnPropertyChanged(); } }

    /// <summary>显示系统进程组。</summary>
    public bool ShowSystemProcesses { get => _draft.ShowSystemProcesses; set { _draft.ShowSystemProcesses = value; OnPropertyChanged(); } }

    /// <summary>关闭行为。</summary>
    public CloseBehavior CloseBehavior { get => _draft.CloseBehavior; set { _draft.CloseBehavior = value; OnPropertyChanged(); } }

    /// <summary>按产品名归并。</summary>
    public bool GroupByProductName { get => _draft.GroupByProductName; set { _draft.GroupByProductName = value; OnPropertyChanged(); } }

    /// <summary>按安装根归并。</summary>
    public bool GroupByInstallRoot { get => _draft.GroupByInstallRoot; set { _draft.GroupByInstallRoot = value; OnPropertyChanged(); } }

    /// <summary>继承父进程归属。</summary>
    public bool InheritParentGroup { get => _draft.InheritParentGroup; set { _draft.InheritParentGroup = value; OnPropertyChanged(); } }

    /// <summary>合并系统进程。</summary>
    public bool MergeWindowsSystemProcesses { get => _draft.MergeWindowsSystemProcesses; set { _draft.MergeWindowsSystemProcesses = value; OnPropertyChanged(); } }

    /// <summary>缓存版本信息。</summary>
    public bool UseVersionInfoCache { get => _draft.UseVersionInfoCache; set { _draft.UseVersionInfoCache = value; OnPropertyChanged(); } }

    /// <summary>兜底分组名。</summary>
    public string UnknownGroupName { get => _draft.UnknownGroupName; set { _draft.UnknownGroupName = value; OnPropertyChanged(); } }

    /// <summary>系统进程组名。</summary>
    public string SystemGroupName { get => _draft.SystemGroupName; set { _draft.SystemGroupName = value; OnPropertyChanged(); } }

    /// <summary>内存保留分钟数。</summary>
    public int MemoryRetentionMinutes { get => _draft.MemoryRetentionMinutes; set { _draft.MemoryRetentionMinutes = value; OnPropertyChanged(); } }

    /// <summary>磁盘保留天数。</summary>
    public int DiskRetentionDays { get => _draft.DiskRetentionDays; set { _draft.DiskRetentionDays = value; OnPropertyChanged(); } }

    /// <summary>趋势槽位。</summary>
    public int TrendAppSlots { get => _draft.TrendAppSlots; set { _draft.TrendAppSlots = value; OnPropertyChanged(); } }

    /// <summary>写磁盘历史。</summary>
    public bool EnableDiskHistory { get => _draft.EnableDiskHistory; set { _draft.EnableDiskHistory = value; OnPropertyChanged(); } }

    /// <summary>启用告警。</summary>
    public bool EnableAlerts { get => _draft.EnableAlerts; set { _draft.EnableAlerts = value; OnPropertyChanged(); } }

    /// <summary>整机 CPU 阈值。</summary>
    public int AlertCpuPercent { get => _draft.AlertCpuPercent; set { _draft.AlertCpuPercent = value; OnPropertyChanged(); } }

    /// <summary>整机内存阈值。</summary>
    public int AlertMemoryPercent { get => _draft.AlertMemoryPercent; set { _draft.AlertMemoryPercent = value; OnPropertyChanged(); } }

    /// <summary>应用 CPU 阈值。</summary>
    public int AlertAppCpuPercent { get => _draft.AlertAppCpuPercent; set { _draft.AlertAppCpuPercent = value; OnPropertyChanged(); } }

    /// <summary>告警冷却秒数。</summary>
    public int AlertCooldownSeconds { get => _draft.AlertCooldownSeconds; set { _draft.AlertCooldownSeconds = value; OnPropertyChanged(); } }

    /// <summary>启用智能分析。</summary>
    public bool InsightEnabled { get => _draft.InsightEnabled; set { _draft.InsightEnabled = value; OnPropertyChanged(); } }

    /// <summary>应用 CPU 片段阈值。</summary>
    public int InsightAppCpuPercent { get => _draft.InsightAppCpuPercent; set { _draft.InsightAppCpuPercent = value; OnPropertyChanged(); } }

    /// <summary>应用片段最短秒数。</summary>
    public int InsightAppCpuSeconds { get => _draft.InsightAppCpuSeconds; set { _draft.InsightAppCpuSeconds = value; OnPropertyChanged(); } }

    /// <summary>内存增长阈值。</summary>
    public int InsightMemoryGrowthMbPerMinute { get => _draft.InsightMemoryGrowthMbPerMinute; set { _draft.InsightMemoryGrowthMbPerMinute = value; OnPropertyChanged(); } }

    /// <summary>整机高负载阈值。</summary>
    public int InsightSystemCpuPercent { get => _draft.InsightSystemCpuPercent; set { _draft.InsightSystemCpuPercent = value; OnPropertyChanged(); } }

    /// <summary>系统片段最短秒数。</summary>
    public int InsightSystemCpuSeconds { get => _draft.InsightSystemCpuSeconds; set { _draft.InsightSystemCpuSeconds = value; OnPropertyChanged(); } }

    /// <summary>低内存阈值。</summary>
    public int InsightLowMemoryPercent { get => _draft.InsightLowMemoryPercent; set { _draft.InsightLowMemoryPercent = value; OnPropertyChanged(); } }

    /// <summary>摘要窗口分钟数。</summary>
    public int InsightDigestMinutes { get => _draft.InsightDigestMinutes; set { _draft.InsightDigestMinutes = value; OnPropertyChanged(); } }

    /// <summary>日志级别。</summary>
    public LogLevel LogLevel { get => _draft.LogLevel; set { _draft.LogLevel = value; OnPropertyChanged(); } }

    /// <summary>日志上限（KB）。</summary>
    public int LogMaxKilobytes { get => _draft.LogMaxKilobytes; set { _draft.LogMaxKilobytes = value; OnPropertyChanged(); } }

    /// <summary>自动检查更新。</summary>
    public bool UpdateCheckEnabled { get => _draft.UpdateCheckEnabled; set { _draft.UpdateCheckEnabled = value; OnPropertyChanged(); } }

    /// <summary>开机自启。</summary>
    public bool RunAtStartup { get => _draft.RunAtStartup; set { _draft.RunAtStartup = value; OnPropertyChanged(); } }

    private void Save()
    {
        _draft.Rules = Rules.Select(r => r.Rule).ToList();
        SettingsValidator.Normalize(_draft);

        if (!_host.ApplySettings(_draft))
        {
            StatusText = _host.LastError ?? Localization.Loc.T("保存设置失败");
            return;
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void ResetToDefaults()
    {
        _draft.ResetToDefaults();
        Rules.Clear();
        RefreshAllProperties();
        StatusText = Localization.Loc.T("已恢复默认值，点「保存」后生效");
    }

    /// <summary>把草稿的所有值重新推给界面（反射遍历本类的公开读写属性，省去几十行手写通知）。</summary>
    private void RefreshAllProperties()
    {
        foreach (var property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0
                && property.Name is not (nameof(SelectedRule) or nameof(SelectedTab) or nameof(StatusText)))
            {
                OnPropertyChanged(property.Name);
            }
        }

        OnPropertyChanged(nameof(HasSelectedRule));
    }

    private void AddRule()
    {
        var row = new RuleRowViewModel(new AppRule { Pattern = "*", Target = Localization.Loc.T("自定义分组") });
        Rules.Add(row);
        SelectedRule = row;
    }

    private void RemoveRule()
    {
        if (_selectedRule is null)
        {
            return;
        }

        Rules.Remove(_selectedRule);
        SelectedRule = null;
    }

    private void MoveRule(int delta)
    {
        if (_selectedRule is null)
        {
            return;
        }

        int index = Rules.IndexOf(_selectedRule);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Rules.Count)
        {
            return;
        }

        Rules.Move(index, target);
        SelectedRule = _selectedRule;
    }

    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureDirectories();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            string? path = DiagnosticBundle.Create();
            if (string.IsNullOrEmpty(path))
            {
                StatusText = Localization.Loc.T("导出诊断包失败");
                return;
            }

            StatusText = Localization.Loc.T("诊断包已导出") + "：" + path;
            string argument = "/select," + (char)34 + path + (char)34;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }
}

/// <summary>规则列表里的一行（包装 Core 的 <see cref="AppRule"/>，修改直接写回）。</summary>
public sealed class RuleRowViewModel : ObservableObject
{
    /// <summary>创建行。</summary>
    public RuleRowViewModel(AppRule rule)
    {
        Rule = rule;
    }

    /// <summary>被包装的规则。</summary>
    public AppRule Rule { get; }

    /// <summary>是否启用。</summary>
    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value)
            {
                return;
            }

            Rule.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>匹配方式。</summary>
    public AppRuleMatch Match
    {
        get => Rule.Match;
        set
        {
            if (Rule.Match == value)
            {
                return;
            }

            Rule.Match = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>匹配模式。</summary>
    public string Pattern
    {
        get => Rule.Pattern;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(Rule.Pattern, next, StringComparison.Ordinal))
            {
                return;
            }

            Rule.Pattern = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>动作。</summary>
    public AppRuleAction Action
    {
        get => Rule.Action;
        set
        {
            if (Rule.Action == value)
            {
                return;
            }

            Rule.Action = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>目标应用名。</summary>
    public string? Target
    {
        get => Rule.Target;
        set
        {
            if (string.Equals(Rule.Target, value, StringComparison.Ordinal))
            {
                return;
            }

            Rule.Target = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>列表里的一句话摘要。</summary>
    public string Summary
    {
        get
        {
            string match = Rule.Match switch
            {
                AppRuleMatch.ExeName => Localization.Loc.T("映像名") + " = ",
                AppRuleMatch.PathContains => Localization.Loc.T("路径包含") + " ",
                _ => Localization.Loc.T("产品名") + " = ",
            };
            string action = Rule.Action switch
            {
                AppRuleAction.MergeToName => Localization.Loc.T("合并到") + " " + (Rule.Target ?? "?"),
                AppRuleAction.Rename => Localization.Loc.T("重命名为") + " " + (Rule.Target ?? "?"),
                _ => Localization.Loc.T("排除"),
            };
            return (Rule.Enabled ? string.Empty : "(" + Localization.Loc.T("停用") + ") ") + match + Rule.Pattern + " → " + action;
        }
    }
}
