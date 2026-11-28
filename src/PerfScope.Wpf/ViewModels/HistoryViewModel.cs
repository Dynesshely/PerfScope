using System.Collections.ObjectModel;
using System.Windows.Input;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.Core;
using PerfScope.Core.Models;

namespace PerfScope.App.ViewModels;

/// <summary>历史页里的一行：某天某个应用的聚合摘要。</summary>
public sealed class HistoryRowViewModel
{
    /// <summary>从 Core 的摘要构造。</summary>
    public HistoryRowViewModel(DailyAppSummary summary)
    {
        Name = summary.DisplayName;
        CpuSecondsText = (summary.CpuPercentSeconds / 100d).ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + " s";
        PeakCpuText = FormatUtil.Percent(summary.PeakCpuPercent);
        PeakMemoryText = FormatUtil.Bytes(summary.PeakWorkingSetBytes);
        SamplesText = summary.SampleCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>应用名。</summary>
    public string Name { get; }

    /// <summary>当日累计 CPU 秒数。</summary>
    public string CpuSecondsText { get; }

    /// <summary>当日峰值 CPU。</summary>
    public string PeakCpuText { get; }

    /// <summary>当日峰值内存。</summary>
    public string PeakMemoryText { get; }

    /// <summary>采样点数。</summary>
    public string SamplesText { get; }
}

/// <summary>历史页 ViewModel：按天查看落盘的应用聚合结果。</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;
    private DateOnly? _selectedDay;
    private string _statusText = string.Empty;

    /// <summary>创建历史页 ViewModel。</summary>
    public HistoryViewModel(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        RefreshCommand = new RelayCommand(Refresh);
        WriteWeeklyReportCommand = new RelayCommand(WriteWeeklyReport);
        OpenAnalysisFolderCommand = new RelayCommand(OpenAnalysisFolder);
        Refresh();
    }

    /// <summary>可用日期（倒序）。</summary>
    public ObservableCollection<DateOnly> Days { get; } = new();

    /// <summary>选中日期的应用摘要。</summary>
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = new();

    /// <summary>刷新命令。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>生成周报。</summary>
    public ICommand WriteWeeklyReportCommand { get; }

    /// <summary>打开分析目录。</summary>
    public ICommand OpenAnalysisFolderCommand { get; }

    /// <summary>选中的日期。</summary>
    public DateOnly? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value))
            {
                LoadDay(value);
            }
        }
    }

    /// <summary>状态栏文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>是否没有历史数据。</summary>
    public bool IsEmpty => Days.Count == 0;

    private void Refresh()
    {
        try
        {
            Days.Clear();
            foreach (var day in _host.History.GetAvailableDays().Reverse())
            {
                Days.Add(day);
            }

            OnPropertyChanged(nameof(IsEmpty));
            SelectedDay = Days.Count > 0 ? Days[0] : null;
            if (Days.Count == 0)
            {
                StatusText = Localization.Loc.T("还没有落盘的历史：确认「历史」里已开启写磁盘，并等几分钟。");
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void LoadDay(DateOnly? day)
    {
        Rows.Clear();
        if (day is null)
        {
            return;
        }

        try
        {
            foreach (var summary in _host.History.QueryDay(day.Value))
            {
                Rows.Add(new HistoryRowViewModel(summary));
            }

            StatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Localization.Loc.T("{0}：{1} 个应用"), day.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture), Rows.Count);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void WriteWeeklyReport()
    {
        try
        {
            string? path = _host.WriteWeeklyReport();
            if (string.IsNullOrEmpty(path))
            {
                StatusText = Localization.Loc.T("生成周报失败（可能没有足够的历史数据）");
                return;
            }

            StatusText = Localization.Loc.T("周报已生成") + "：" + path;
            OpenFolder(path);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void OpenAnalysisFolder()
    {
        try
        {
            Directory.CreateDirectory(_host.AnalysisDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_host.AnalysisDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开资源管理器失败不影响结果提示
        }
    }
}
