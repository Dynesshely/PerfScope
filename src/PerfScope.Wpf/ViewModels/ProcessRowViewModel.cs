using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.Core.Models;

namespace PerfScope.App.ViewModels;

/// <summary>应用展开后的一行子进程。数据驱动：文本全部由 Core 的 <see cref="AppProcessInfo"/> 计算而来。</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private double _cpuPercent;
    private long _workingSetBytes;
    private string? _executablePath;

    /// <summary>创建子进程行。</summary>
    public ProcessRowViewModel(int pid, string name)
    {
        Pid = pid;
        Name = name;
    }

    /// <summary>进程 ID。</summary>
    public int Pid { get; private set; }

    /// <summary>映像名。</summary>
    public string Name { get; private set; }

    /// <summary>完整路径（可能为空：权限不足时读不到）。</summary>
    public string? ExecutablePath
    {
        get => _executablePath;
        private set
        {
            if (SetProperty(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(PathText));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>CPU 百分比（已按设置口径归一化）。</summary>
    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuText));
            }
        }
    }

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

    /// <summary>CPU 文本。</summary>
    public string CpuText => FormatUtil.Percent(CpuPercent);

    /// <summary>内存文本。</summary>
    public string MemoryText => FormatUtil.Bytes(WorkingSetBytes);

    /// <summary>路径文本（空的路径显示为「路径不可用」）。</summary>
    public string PathText => string.IsNullOrWhiteSpace(ExecutablePath)
        ? Localization.Loc.T("路径不可用（权限不足）")
        : ExecutablePath!;

    /// <summary>屏幕阅读器读到的子进程摘要。</summary>
    public string AccessibilitySummary
        => Name + " · PID " + Pid + " · CPU " + CpuText + " · " + Localization.Loc.T("内存") + " " + MemoryText;

    /// <summary>悬停提示：完整路径 + PID。</summary>
    public string ToolTipText => Name + "  ·  PID " + Pid + Environment.NewLine + PathText;

    /// <summary>按新数据刷新（同一个 PID 复用实例，避免每帧重建行）。</summary>
    public void Update(AppProcessInfo info)
    {
        Pid = info.Pid;
        Name = info.Name;
        ExecutablePath = info.ExecutablePath;
        CpuPercent = info.CpuPercent;
        WorkingSetBytes = info.WorkingSetBytes;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccessibilitySummary));
    }
}
