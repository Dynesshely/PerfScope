using PerfScope.App.Localization;
using System.Globalization;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Forms;

/// <summary>「历史」页：按天查看应用级用量汇总（CPU 时间、峰值 CPU / 内存）。</summary>
internal sealed class HistoryForm : Form
{
    private readonly AppHost _host;
    private readonly Theme _theme;
    private readonly float _scale;
    private readonly ComboBox _dayBox;
    private readonly ThemedListView _list;
    private readonly Label _summaryLabel;
    private IReadOnlyList<DateOnly> _days = Array.Empty<DateOnly>();
    private bool _disposed;

    /// <summary>创建历史窗口。</summary>
    public HistoryForm(AppHost host)
    {
        _host = host;
        _scale = Theme.DetectDpiScale();
        _theme = Theme.Create(host.Settings.Theme, _scale);

        Text = Loc.T("PerfScope 历史 — 当天应用汇总");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        ClientSize = new Size(Scaled(720), Scaled(520));
        MinimumSize = new Size(Scaled(560), Scaled(400));

        _dayBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Scaled(160),
        };
        _dayBox.SelectedIndexChanged += (_, _) => ReloadDay();

        var refreshButton = new Button { Text = Loc.T("刷新"), Width = Scaled(80), Height = Scaled(28) };
        refreshButton.Click += (_, _) => ReloadDays();

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Height = Scaled(44),
            Padding = new Padding(Scaled(12), Scaled(8), Scaled(12), Scaled(4)),
            BackColor = _theme.Background,
            WrapContents = false,
        };
        topBar.Controls.Add(new Label
        {
            Text = Loc.T("日期"),
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, Scaled(6), Scaled(8), 0),
        });
        topBar.Controls.Add(_dayBox);
        refreshButton.Margin = new Padding(Scaled(8), 0, 0, 0);
        topBar.Controls.Add(refreshButton);

        _list = new ThemedListView(_theme)
        {
            Dock = DockStyle.Fill,
            Font = _theme.SmallFont,
        };
        _list.Columns.Add(Loc.T("应用"), Scaled(240));
        _list.Columns.Add(Loc.T("CPU 时间（秒）"), Scaled(120), HorizontalAlignment.Right);
        _list.Columns.Add(Loc.T("峰值 CPU"), Scaled(100), HorizontalAlignment.Right);
        _list.Columns.Add(Loc.T("峰值内存"), Scaled(120), HorizontalAlignment.Right);
        _list.Columns.Add(Loc.T("采样点"), Scaled(80), HorizontalAlignment.Right);

        _summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = Scaled(46),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _theme.TextMuted,
            Font = _theme.SmallFont,
            Padding = new Padding(Scaled(12), 0, Scaled(12), 0),
            Text = Loc.T("正在读取历史…"),
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = _theme.Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(topBar, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(_summaryLabel, 0, 2);
        Controls.Add(root);

        FormStyler.Apply(this, _theme);
        ReloadDays();
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);

    private void ReloadDays()
    {
        IReadOnlyList<DateOnly> days;
        try
        {
            days = _host.History.GetAvailableDays();
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, Loc.T("读取历史日期失败"), ex);
            days = Array.Empty<DateOnly>();
        }

        _days = days;
        _dayBox.BeginUpdate();
        _dayBox.Items.Clear();
        foreach (var day in _days)
        {
            _dayBox.Items.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        _dayBox.EndUpdate();

        if (_dayBox.Items.Count > 0)
        {
            _dayBox.SelectedIndex = _dayBox.Items.Count - 1;
        }
        else
        {
            _list.Items.Clear();
            _summaryLabel.Text = Loc.T("暂无已落盘的历史数据。历史会在启用「写入磁盘」后按天累积。");
        }
    }

    private void ReloadDay()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.EndUpdate();

        int index = _dayBox.SelectedIndex;
        if (index < 0 || index >= _days.Count)
        {
            return;
        }

        DateOnly date = _days[index];
        IReadOnlyList<DailyAppSummary> summaries;
        try
        {
            summaries = _host.History.QueryDay(date);
        }
        catch (Exception ex)
        {
            _host.SafeLog(LogLevel.Warn, Loc.T("查询历史汇总失败"), ex);
            _summaryLabel.Text = Loc.T("查询历史失败，详情见日志。");
            return;
        }

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var summary in summaries)
            {
                var displayName = string.IsNullOrWhiteSpace(summary.DisplayName) ? summary.AppKey : Loc.T(summary.DisplayName);
                var item = new ListViewItem(displayName);
                item.SubItems.Add((summary.CpuPercentSeconds / 100d).ToString("F1", CultureInfo.CurrentCulture));
                item.SubItems.Add(FormatUtil.Percent(summary.PeakCpuPercent));
                item.SubItems.Add(FormatUtil.Bytes(summary.PeakWorkingSetBytes));
                item.SubItems.Add(summary.SampleCount.ToString(CultureInfo.CurrentCulture));
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _summaryLabel.Text = summaries.Count == 0
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Loc.T(" 没有应用汇总数据。")
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Loc.T(" 共 ") + summaries.Count.ToString(CultureInfo.CurrentCulture) +
              Loc.T(" 个应用；CPU 时间 = 百分比·秒 / 100，越大表示当天占用越久。");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _theme.Dispose();
        }

        base.Dispose(disposing);
    }
}