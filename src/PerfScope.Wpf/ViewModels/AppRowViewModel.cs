using System.Collections.ObjectModel;
using System.Windows.Media;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.Core.Models;

namespace PerfScope.App.ViewModels;

/// <summary>
/// 应用列表的一行。同一个归并键（<see cref="Key"/>）在整个进程生命周期内复用同一个实例，
/// 每帧只更新数值并触发属性通知 —— 这是列表不掉帧的关键。
/// </summary>
public sealed class AppRowViewModel : ObservableObject
{
    private readonly Dictionary<int, ProcessRowViewModel> _childByPid = new();
    private string _displayName = string.Empty;
    private double _cpuPercent;
    private long _workingSetBytes;
    private long _privateBytes;
    private int _processCount;
    private double _readBytesPerSecond;
    private double _writeBytesPerSecond;
    private string _reason = string.Empty;
    private string? _rootPath;
    private ImageSource? _icon;
    private bool _isExpanded;
    private AppKind _kind;

    /// <summary>创建行（只在第一次见到该应用时调用）。</summary>
    public AppRowViewModel(string key)
    {
        Key = key;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>展开 / 收起子进程。</summary>
    public System.Windows.Input.ICommand ToggleExpandCommand { get; }

    /// <summary>稳定归并键。</summary>
    public string Key { get; }

    /// <summary>子进程行（展开时显示）。</summary>
    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    /// <summary>展示名。</summary>
    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetProperty(ref _displayName, value))
            {
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>归并来源类别。</summary>
    public AppKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    /// <summary>图标。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>CPU 百分比（已归一化）。</summary>
    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuText));
                OnPropertyChanged(nameof(CpuRatio));
            }
        }
    }

    /// <summary>CPU 文本。</summary>
    public string CpuText => FormatUtil.Percent(CpuPercent);

    /// <summary>CPU 在整机上的占比（0~1，用于条形长度）。</summary>
    public double CpuRatio => Math.Clamp(CpuPercent / 100d, 0d, 1d);

    /// <summary>工作集字节数。</summary>
    public long WorkingSetBytes
    {
        get => _workingSetBytes;
        private set
        {
            if (SetProperty(ref _workingSetBytes, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    /// <summary>私有字节。</summary>
    public long PrivateBytes
    {
        get => _privateBytes;
        private set => SetProperty(ref _privateBytes, value);
    }

    /// <summary>内存文本（按设置的口径显示）。</summary>
    public string MemoryText => FormatUtil.Bytes(MemoryBytes);

    /// <summary>当前口径下的内存字节数。</summary>
    public long MemoryBytes { get; private set; }

    /// <summary>进程数。</summary>
    public int ProcessCount
    {
        get => _processCount;
        private set
        {
            if (SetProperty(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    /// <summary>进程数文本。</summary>
    public string ProcessCountText => ProcessCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>读 IO 速率。</summary>
    public double ReadBytesPerSecond
    {
        get => _readBytesPerSecond;
        private set
        {
            if (SetProperty(ref _readBytesPerSecond, value))
            {
                OnPropertyChanged(nameof(IoText));
            }
        }
    }

    /// <summary>写 IO 速率。</summary>
    public double WriteBytesPerSecond
    {
        get => _writeBytesPerSecond;
        private set
        {
            if (SetProperty(ref _writeBytesPerSecond, value))
            {
                OnPropertyChanged(nameof(IoText));
            }
        }
    }

    /// <summary>IO 文本（读 / 写）。</summary>
    public string IoText => FormatUtil.BytesPerSecond(ReadBytesPerSecond) + " / " + FormatUtil.BytesPerSecond(WriteBytesPerSecond);

    /// <summary>归并依据（可解释性卖点，悬停可见）。</summary>
    public string Reason
    {
        get => _reason;
        private set
        {
            if (SetProperty(ref _reason, value))
            {
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>应用根目录。</summary>
    public string? RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    /// <summary>是否展开子进程。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
                OnPropertyChanged(nameof(RowHeight));
            }
        }
    }

    /// <summary>展开箭头的字形（Segoe MDL2 Assets）。</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>该行占用的高度（含展开的子进程）。</summary>
    public double RowHeight => IsExpanded ? 34 + (Processes.Count * 24) : 34;

    /// <summary>子进程是否可展开。</summary>
    public bool HasChildren => ProcessCount > 0;

    /// <summary>悬停提示：归并依据 + 根目录 + 进程数。</summary>
    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { DisplayName };
            if (!string.IsNullOrWhiteSpace(RootPath))
            {
                lines.Add(RootPath!);
            }

            lines.Add(Localization.Loc.T("归并依据") + "：" + Reason);
            lines.Add(Localization.Loc.T("进程数") + "：" + ProcessCountText);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>按一帧里的应用数据刷新本行（复用子进程实例）。</summary>
    public void Update(AppSample sample, bool preferPrivateMemory)
    {
        DisplayName = sample.DisplayName;
        Kind = sample.Kind;
        RootPath = sample.RootPath;
        Reason = sample.Reason;
        CpuPercent = sample.CpuPercent;
        WorkingSetBytes = sample.WorkingSetBytes;
        PrivateBytes = sample.PrivateBytes;
        MemoryBytes = preferPrivateMemory ? sample.PrivateBytes : sample.WorkingSetBytes;
        OnPropertyChanged(nameof(MemoryText));
        ProcessCount = sample.ProcessCount;
        ReadBytesPerSecond = sample.ReadBytesPerSecond;
        WriteBytesPerSecond = sample.WriteBytesPerSecond;

        // 子进程行按 PID 复用：只增删变化的部分，避免每次刷新都重建整棵可视化树
        var seen = new HashSet<int>();
        for (int i = 0; i < sample.Processes.Length; i++)
        {
            var info = sample.Processes[i];
            seen.Add(info.Pid);
            if (!_childByPid.TryGetValue(info.Pid, out var row))
            {
                row = new ProcessRowViewModel(info.Pid, info.Name);
                _childByPid[info.Pid] = row;

                // 插到按 CPU 降序的正确位置
                int insertAt = Processes.Count;
                for (int j = 0; j < Processes.Count; j++)
                {
                    if (_childByPid.TryGetValue(Processes[j].Pid, out var existing) && existing.CpuPercent < info.CpuPercent)
                    {
                        insertAt = j;
                        break;
                    }
                }

                Processes.Insert(Math.Min(insertAt, Processes.Count), row);
            }

            row.Update(info);
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Processes[i].Pid))
            {
                _childByPid.Remove(Processes[i].Pid);
                Processes.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(RowHeight));
    }
}
