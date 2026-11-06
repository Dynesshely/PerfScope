using PerfScope.App.Localization;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Controls;

/// <summary>
/// 应用列表（核心控件）：自绘 + 虚拟滚动，不创建任何子控件行，
/// 只绘制可见行；图标、画笔、字体全部缓存，保证 1 秒刷新不闪烁、不卡顿。
/// 支持列排序、点击一行展开成员进程、按 TopN 与可见性过滤。
/// </summary>
internal sealed class AppListControl : Control
{
    /// <summary>可排序的列。</summary>
    public enum SortColumn
    {
        /// <summary>应用名。</summary>
        Name = 0,

        /// <summary>CPU 百分比。</summary>
        Cpu = 1,

        /// <summary>内存占用。</summary>
        Memory = 2,

        /// <summary>进程数。</summary>
        Process = 3,

        /// <summary>磁盘读写速率。</summary>
        Io = 4,
    }

    private const int ColumnCount = 6;


    private static readonly string[] HeaderTitles = { Loc.T("应用"), "CPU", Loc.T("内存"), Loc.T("进程数"), Loc.T("磁盘读写"), Loc.T("趋势") };
    private static readonly int[] HeaderSortTargets = { (int)SortColumn.Name, (int)SortColumn.Cpu, (int)SortColumn.Memory, (int)SortColumn.Process, (int)SortColumn.Io, -1 };

    private readonly IconProvider _icons;
    private readonly ThemedScrollBar _scrollBar;
    private readonly Rectangle[] _columns = new Rectangle[ColumnCount];
    private readonly List<Row> _rows = new(64);
    private readonly List<AppSample> _filtered = new(64);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly PointF[] _sparkBuffer = new PointF[66];

    private Theme _theme;
    private SolidBrush _backgroundBrush;
    private SolidBrush _altRowBrush;
    private SolidBrush _hoverBrush;
    private SolidBrush _selectedBrush;
    private SolidBrush _headerBrush;
    private SolidBrush _cpuBarBrush;
    private SolidBrush _memoryBarBrush;
    private SolidBrush _chevronBrush;
    private Font? _chevronFont;
    private Pen _gridPen;
    private Pen _headerBorderPen;
    private Pen _sparkPen;

    private AppSample[] _apps = Array.Empty<AppSample>();
    private Func<AppSample, bool>? _filter;
    private Func<string, TrendRing?>? _trendSource;

    private SortColumn _sort = SortColumn.Cpu;
    private bool _sortDescending = true;
    private int _rowHeight;
    private int _headerHeight;
    private int _scrollOffset;
    private int _selectedIndex = -1;
    private int _hoverIndex = -1;

    // 子项悬停时的自绘提示（完整可执行文件路径）
    private int _tipRow = -1;
    private string? _tipTitle;
    private string? _tipDetail;
    private Point _tipPoint;
    private long _totalPhysicalBytes;

    /// <summary>创建应用列表控件。</summary>
    public AppListControl(Theme theme, IconProvider icons)
    {
        _theme = theme;
        _icons = icons;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);

        BackColor = theme.Background;

        _scrollBar = new ThemedScrollBar(theme)
        {
            Visible = false,
            TabStop = false,
            SmallChange = 1,
            Width = Dp(11),
        };
        _scrollBar.ValueChanged += OnScrollBarValueChanged;
        Controls.Add(_scrollBar);

        _backgroundBrush = new SolidBrush(theme.Background);
        _altRowBrush = new SolidBrush(theme.SurfaceAlt);
        _hoverBrush = new SolidBrush(theme.RowHover);
        _selectedBrush = new SolidBrush(theme.RowSelected);
        _headerBrush = new SolidBrush(theme.HeaderBackground);
        _cpuBarBrush = new SolidBrush(Theme.WithAlpha(theme.Accent, 200));
        _memoryBarBrush = new SolidBrush(Theme.WithAlpha(theme.MemoryLine, 200));
        _chevronBrush = new SolidBrush(theme.TextSecondary);
        _gridPen = new Pen(theme.GridLine);
        _headerBorderPen = new Pen(theme.Border);
        _sparkPen = new Pen(theme.Accent, Math.Max(1.1f, Dp(2) * 0.6f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        _rowHeight = Dp(26);
        _headerHeight = Dp(30);
        RecalcColumns();
    }

    /// <summary>排序列变化（用于把用户偏好写入 ui.json）。</summary>
    public event EventHandler? SortChanged;

    /// <summary>最多显示的应用程序行数。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TopN { get; set; } = 30;

    /// <summary>内存列口径。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MemoryDisplay MemoryMode { get; set; } = MemoryDisplay.WorkingSet;

    /// <summary>当前排序列的持久化名称。</summary>
    public string SortColumnName => _sort switch
    {
        SortColumn.Name => "name",
        SortColumn.Memory => "memory",
        SortColumn.Process => "process",
        SortColumn.Io => "io",
        _ => "cpu",
    };

    /// <summary>当前是否降序。</summary>
    public bool SortDescending => _sortDescending;

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <summary>恢复上次的排序偏好。</summary>
    public void RestoreSort(string? column, bool descending)
    {
        _sort = column?.ToLowerInvariant() switch
        {
            "name" => SortColumn.Name,
            "memory" => SortColumn.Memory,
            "process" => SortColumn.Process,
            "io" => SortColumn.Io,
            _ => SortColumn.Cpu,
        };
        _sortDescending = descending;
        RebuildRows();
    }

    /// <summary>设置过滤谓词（例如隐藏 Windows 系统组）。</summary>
    public void SetFilter(Func<AppSample, bool>? filter)
    {
        _filter = filter;
        RebuildRows();
    }

    /// <summary>设置每个应用的迷你趋势数据源（返回环形缓冲，绘制时复用，不复制）。</summary>
    public void SetTrendSource(Func<string, TrendRing?>? source)
    {
        _trendSource = source;
    }

    /// <summary>更新整机物理内存总量（用于内存列的比例条）。</summary>
    public void SetPhysicalMemory(long totalBytes)
    {
        _totalPhysicalBytes = totalBytes;
    }

    /// <summary>设置变化后强制重排（例如内存口径、过滤条件改变）。</summary>
    public void RefreshRows() => RebuildRows();

    /// <summary>更新一帧的应用数据（应在 UI 线程调用）。</summary>
    public void SetApps(AppSample[] apps)
    {
        _apps = apps ?? Array.Empty<AppSample>();
        RebuildRows();
    }

    /// <summary>切换主题：重建缓存资源后立即重绘。</summary>
    public void SetTheme(Theme theme)
    {
        if (ReferenceEquals(theme, _theme))
        {
            return;
        }

        _theme = theme;
        BackColor = theme.Background;
        _scrollBar.SetTheme(theme);
        DisposeResources();
        _backgroundBrush = new SolidBrush(theme.Background);
        _altRowBrush = new SolidBrush(theme.SurfaceAlt);
        _hoverBrush = new SolidBrush(theme.RowHover);
        _selectedBrush = new SolidBrush(theme.RowSelected);
        _headerBrush = new SolidBrush(theme.HeaderBackground);
        _cpuBarBrush = new SolidBrush(Theme.WithAlpha(theme.Accent, 200));
        _memoryBarBrush = new SolidBrush(Theme.WithAlpha(theme.MemoryLine, 200));
        _chevronBrush = new SolidBrush(theme.TextSecondary);
        _gridPen = new Pen(theme.GridLine);
        _headerBorderPen = new Pen(theme.Border);
        _sparkPen = new Pen(theme.Accent, Math.Max(1.1f, Dp(2) * 0.6f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        Invalidate();
    }

    private void DisposeResources()
    {
        _backgroundBrush.Dispose();
        _altRowBrush.Dispose();
        _hoverBrush.Dispose();
        _selectedBrush.Dispose();
        _headerBrush.Dispose();
        _cpuBarBrush.Dispose();
        _memoryBarBrush.Dispose();
        _chevronBrush.Dispose();
        _gridPen.Dispose();
        _headerBorderPen.Dispose();
        _sparkPen.Dispose();
        _chevronFont?.Dispose();
        _chevronFont = null;
    }

    private void RebuildRows()
    {
        _filtered.Clear();
        foreach (var app in _apps)
        {
            if (app is null || (_filter is not null && !_filter(app)))
            {
                continue;
            }

            _filtered.Add(app);
        }

        _filtered.Sort(BuildComparison());

        if (UiDebug.ExpandFirstGroup && _expanded.Count == 0 && _filtered.Count > 0)
        {
            var first = _filtered.FirstOrDefault(app => app.Processes is { Length: > 0 });
            if (first is not null)
            {
                _expanded.Add(first.Key);
            }
        }

        _rows.Clear();
        int limit = Math.Clamp(TopN, 1, 200);
        for (int i = 0; i < _filtered.Count && i < limit; i++)
        {
            var app = _filtered[i];
            _rows.Add(new Row { IsProcess = false, App = app });
            if (app.Processes is null || app.Processes.Length == 0 || !_expanded.Contains(app.Key))
            {
                continue;
            }

            foreach (var process in app.Processes)
            {
                _rows.Add(new Row { IsProcess = true, App = app, Process = process });
            }
        }

        if (_selectedIndex >= _rows.Count)
        {
            _selectedIndex = _rows.Count - 1;
        }

        UpdateScrollBar();
        Invalidate();
    }

    private Comparison<AppSample> BuildComparison()
    {
        int direction = _sortDescending ? -1 : 1;
        bool byPrivate = MemoryMode == MemoryDisplay.PrivateWorkingSet;
        switch (_sort)
        {
            case SortColumn.Name:
                return (a, b) => direction * string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            case SortColumn.Memory:
                return (a, b) => TieBreak(direction * MemoryOf(a, byPrivate).CompareTo(MemoryOf(b, byPrivate)), a, b);
            case SortColumn.Process:
                return (a, b) => TieBreak(direction * a.ProcessCount.CompareTo(b.ProcessCount), a, b);
            case SortColumn.Io:
                return (a, b) => TieBreak(
                    direction * (a.ReadBytesPerSecond + a.WriteBytesPerSecond).CompareTo(b.ReadBytesPerSecond + b.WriteBytesPerSecond),
                    a,
                    b);
            default:
                return (a, b) => TieBreak(direction * a.CpuPercent.CompareTo(b.CpuPercent), a, b);
        }
    }

    private static int TieBreak(int primary, AppSample a, AppSample b)
        => primary != 0 ? primary : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);

    private static long MemoryOf(AppSample app, bool byPrivate) => byPrivate ? app.PrivateBytes : app.WorkingSetBytes;

    private void UpdateScrollBar()
    {
        int contentHeight = _rows.Count * _rowHeight;
        int viewHeight = Math.Max(1, Height - _headerHeight);
        bool needScroll = contentHeight > viewHeight;
        if (_scrollBar.Visible != needScroll)
        {
            _scrollBar.Visible = needScroll;
            RecalcColumns();
        }

        int scrollWidth = Math.Max(Dp(8), _scrollBar.Width);
        _scrollBar.Bounds = new Rectangle(Math.Max(0, Width - scrollWidth - Dp(2)), _headerHeight, scrollWidth, Math.Max(0, Height - _headerHeight));

        if (needScroll)
        {
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = Math.Max(0, contentHeight - 1);
            _scrollBar.LargeChange = Math.Max(1, viewHeight);
            _scrollBar.SmallChange = Math.Max(1, _rowHeight);
            int maxValue = Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
            int clamped = Math.Clamp(_scrollOffset, 0, maxValue);
            if (_scrollBar.Value != clamped)
            {
                _scrollBar.Value = clamped;
            }

            _scrollOffset = _scrollBar.Value;
        }
        else
        {
            _scrollOffset = 0;
        }
    }

    private void OnScrollBarValueChanged(object? sender, EventArgs e)
    {
        _scrollOffset = _scrollBar.Value;
        Invalidate();
    }

    private void RecalcColumns()
    {
        int width = Math.Max(Dp(260), Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
        int pad = Dp(12);
        int cpuWidth = Dp(78);
        int memoryWidth = Dp(100);
        int processWidth = Dp(70);
        int ioWidth = Dp(126);
        int trendWidth = Dp(112);
        int fixedWidth = cpuWidth + memoryWidth + processWidth + ioWidth + trendWidth;
        int nameWidth = Math.Max(Dp(170), width - fixedWidth - pad * 2);

        int x = pad;
        _columns[0] = new Rectangle(x, 0, nameWidth, 0);
        x += nameWidth;
        _columns[1] = new Rectangle(x, 0, cpuWidth, 0);
        x += cpuWidth;
        _columns[2] = new Rectangle(x, 0, memoryWidth, 0);
        x += memoryWidth;
        _columns[3] = new Rectangle(x, 0, processWidth, 0);
        x += processWidth;
        _columns[4] = new Rectangle(x, 0, ioWidth, 0);
        x += ioWidth;
        _columns[5] = new Rectangle(x, 0, Math.Max(0, Math.Min(trendWidth, width - x - pad)), 0);
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _rowHeight = Dp(27);
        _headerHeight = Dp(32);
        RecalcColumns();
        UpdateScrollBar();
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.FillRectangle(_backgroundBrush, ClientRectangle);

        int width = Math.Max(1, Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
        DrawHeader(graphics, width);

        int contentTop = _headerHeight;
        if (_rows.Count == 0)
        {
            string message = _apps.Length == 0 ? Loc.T("正在等待第一帧采样数据…") : Loc.T("当前筛选条件下没有应用");
            var emptyRect = new Rectangle(0, contentTop, width, Math.Max(0, Height - contentTop));
            TextRenderer.DrawText(
                graphics,
                message,
                _theme.UiFont,
                emptyRect,
                _theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return;
        }

        int clipHeight = Height - contentTop;
        if (clipHeight <= 0)
        {
            return;
        }

        graphics.SetClip(new Rectangle(0, contentTop, width, clipHeight), CombineMode.Replace);
        // 行背景/网格线不需要抗锯齿，只有趋势线需要，避免整体走慢路径
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        for (int index = 0; index < _rows.Count; index++)
        {
            int top = contentTop + index * _rowHeight - _scrollOffset;
            if (top + _rowHeight <= contentTop)
            {
                continue;
            }

            if (top >= Height)
            {
                break;
            }

            var rowRect = new Rectangle(0, top, width, _rowHeight);
            DrawRow(graphics, rowRect, index);
        }

        graphics.ResetClip();

        if (UiDebug.ForceListTooltip && _tipRow < 0)
        {
            // 仅截图 / 自动化验证：把提示固定挂在第一个子项上
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.IsProcess && !string.IsNullOrWhiteSpace(row.Process.ExecutablePath))
                {
                    _tipRow = i;
                    _tipTitle = row.Process.Name + "  ·  PID " + row.Process.Pid.ToString(System.Globalization.CultureInfo.CurrentCulture);
                    _tipDetail = row.Process.ExecutablePath;
                    int top = contentTop + i * _rowHeight - _scrollOffset;
                    _tipPoint = new Point(Dp(360), top + _rowHeight / 2);
                    break;
                }
            }
        }

        DrawHoverTooltip(graphics);
    }

    private void DrawHeader(Graphics graphics, int width)
    {
        var headerRect = new Rectangle(0, 0, width, _headerHeight);
        graphics.FillRectangle(_headerBrush, headerRect);
        graphics.DrawLine(_headerBorderPen, 0, _headerHeight - 1, width, _headerHeight - 1);

        int titleTop = Dp(4);
        int titleHeight = Math.Max(0, _headerHeight - Dp(8));
        for (int column = 0; column < ColumnCount; column++)
        {
            var cell = _columns[column];
            bool active = HeaderSortTargets[column] >= 0 && HeaderSortTargets[column] == (int)_sort;
            // 排序箭头画在列右端，这里给标题文字留出空间，避免压字
            int textInset = active ? Dp(16) : 0;
            var textRect = new Rectangle(cell.X, titleTop, Math.Max(0, cell.Width - textInset), titleHeight);
            var color = active ? _theme.TextPrimary : _theme.TextSecondary;
            var flags = GetTextFlags(column) | TextFormatFlags.VerticalCenter;

            TextRenderer.DrawText(graphics, HeaderTitles[column], _theme.SmallFont, textRect, color, flags);

            if (active)
            {
                DrawSortArrow(graphics, cell, titleTop + titleHeight / 2);
            }
        }
    }

    private void DrawSortArrow(Graphics graphics, Rectangle cell, int centerY)
    {
        int size = Math.Max(3, Dp(4));
        int arrowRight = cell.Right - Dp(4);
        int arrowLeft = arrowRight - size * 2;
        var points = _sortDescending
            ? new[] { new Point(arrowLeft, centerY - size / 2), new Point(arrowRight, centerY - size / 2), new Point(arrowLeft + size, centerY + size / 2) }
            : new[] { new Point(arrowLeft, centerY + size / 2), new Point(arrowRight, centerY + size / 2), new Point(arrowLeft + size, centerY - size / 2) };
        graphics.FillPolygon(_chevronBrush, points);
    }

    private static TextFormatFlags GetTextFlags(int column) => column switch
    {
        0 => TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis,
        5 => TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine,
        _ => TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis,
    };

    private void DrawRow(Graphics graphics, Rectangle rowRect, int index)
    {
        var row = _rows[index];
        bool selected = index == _selectedIndex;
        bool hover = !selected && index == _hoverIndex;

        if (selected)
        {
            graphics.FillRectangle(_selectedBrush, rowRect);
        }
        else if (hover)
        {
            graphics.FillRectangle(_hoverBrush, rowRect);
        }
        else if (!row.IsProcess && (index & 1) == 1)
        {
            graphics.FillRectangle(_altRowBrush, rowRect);
        }

        graphics.DrawLine(_gridPen, rowRect.Left, rowRect.Bottom - 1, rowRect.Right, rowRect.Bottom - 1);

        if (row.IsProcess)
        {
            DrawProcessRow(graphics, rowRect, row);
        }
        else
        {
            DrawAppRow(graphics, rowRect, row);
        }
    }

    private void DrawAppRow(Graphics graphics, Rectangle rowRect, Row row)
    {
        var app = row.App;
        bool expanded = _expanded.Contains(app.Key);
        int iconSize = Dp(16);
        int indent = Dp(12);

        int chevronX = indent + Dp(4);
        int centerY = rowRect.Y + rowRect.Height / 2;
        DrawChevron(graphics, chevronX + Dp(7), centerY, expanded);

        int iconX = chevronX + Dp(21);
        var icon = _icons.GetIcon(app.IconSourcePath ?? app.RootPath, app.DisplayName, iconSize);
        if (icon is not null)
        {
            graphics.DrawImage(icon, new Rectangle(iconX, centerY - iconSize / 2, iconSize, iconSize));
        }

        var nameCell = _columns[0];
        int nameLeft = iconX + iconSize + Dp(8);
        var nameRect = new Rectangle(nameLeft, rowRect.Y + Dp(2), Math.Max(0, nameCell.Right - nameLeft), rowRect.Height - Dp(4));
        TextRenderer.DrawText(
            graphics,
            app.DisplayName,
            _theme.UiFont,
            nameRect,
            _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        DrawCpuCell(graphics, rowRect, app.CpuPercent);
        DrawMemoryCell(graphics, rowRect, MemoryOf(app, MemoryMode == MemoryDisplay.PrivateWorkingSet));

        var processCell = _columns[3];
        TextRenderer.DrawText(
            graphics,
            FormatUtil.Count(app.ProcessCount),
            _theme.NumberFont,
            new Rectangle(processCell.X, rowRect.Y, Math.Max(0, processCell.Width - Dp(4)), rowRect.Height),
            _theme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        var ioCell = _columns[4];
        TextRenderer.DrawText(
            graphics,
            FormatUtil.BytesPerSecond(app.ReadBytesPerSecond + app.WriteBytesPerSecond),
            _theme.NumberFont,
            new Rectangle(ioCell.X, rowRect.Y, Math.Max(0, ioCell.Width - Dp(4)), rowRect.Height),
            _theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        DrawTrendCell(graphics, rowRect, app.Key);
    }

    private void DrawProcessRow(Graphics graphics, Rectangle rowRect, Row row)
    {
        var app = row.App;
        var process = row.Process;
        int iconSize = Dp(14);
        int centerY = rowRect.Y + rowRect.Height / 2;

        int indent = Dp(38);
        var icon = _icons.GetIcon(process.ExecutablePath ?? app.IconSourcePath, process.Name, iconSize);
        if (icon is not null)
        {
            graphics.DrawImage(icon, new Rectangle(indent, centerY - iconSize / 2, iconSize, iconSize));
        }

        var nameCell = _columns[0];
        int nameLeft = indent + iconSize + Dp(8);
        string label = process.Name + "  ·  PID " + process.Pid.ToString(System.Globalization.CultureInfo.CurrentCulture);
        TextRenderer.DrawText(
            graphics,
            label,
            _theme.SmallFont,
            new Rectangle(nameLeft, rowRect.Y, Math.Max(0, nameCell.Right - nameLeft), rowRect.Height),
            _theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        DrawCpuCell(graphics, rowRect, process.CpuPercent);
        DrawMemoryCell(graphics, rowRect, MemoryMode == MemoryDisplay.PrivateWorkingSet ? process.PrivateBytes : process.WorkingSetBytes);

        var processCell = _columns[3];
        var trendCell = _columns[5];
        int pathLeft = processCell.X;
        int pathWidth = Math.Max(0, trendCell.Right - pathLeft);
        TextRenderer.DrawText(
            graphics,
            process.ExecutablePath ?? Loc.T("(路径不可访问)"),
            _theme.SmallFont,
            new Rectangle(pathLeft, rowRect.Y, pathWidth, rowRect.Height),
            _theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
    }

    private void DrawCpuCell(Graphics graphics, Rectangle rowRect, double cpuPercent)
    {
        var cell = _columns[1];
        var textRect = new Rectangle(cell.X, rowRect.Y, Math.Max(0, cell.Width - Dp(4)), rowRect.Height);
        TextRenderer.DrawText(
            graphics,
            FormatUtil.Percent(cpuPercent),
            _theme.NumberFont,
            textRect,
            cpuPercent >= 80 ? _theme.Danger : cpuPercent >= 40 ? _theme.TextPrimary : _theme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        double ratio = Math.Clamp(cpuPercent / 100d, 0, 1);
        int barWidth = (int)(cell.Width * ratio);
        if (barWidth > 0)
        {
            graphics.FillRectangle(_cpuBarBrush, cell.X, rowRect.Bottom - Dp(3), barWidth, Math.Max(1, Dp(2)));
        }
    }

    private void DrawMemoryCell(Graphics graphics, Rectangle rowRect, long bytes)
    {
        var cell = _columns[2];
        TextRenderer.DrawText(
            graphics,
            FormatUtil.Bytes(bytes),
            _theme.NumberFont,
            new Rectangle(cell.X, rowRect.Y, Math.Max(0, cell.Width - Dp(4)), rowRect.Height),
            _theme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (_totalPhysicalBytes > 0 && bytes > 0)
        {
            double ratio = Math.Clamp(bytes / (double)_totalPhysicalBytes, 0, 1);
            int barWidth = (int)(cell.Width * ratio);
            if (barWidth > 0)
            {
                graphics.FillRectangle(_memoryBarBrush, cell.X, rowRect.Bottom - Dp(3), barWidth, Math.Max(1, Dp(2)));
            }
        }
    }

    private void DrawTrendCell(Graphics graphics, Rectangle rowRect, string key)
    {
        if (_trendSource is null)
        {
            return;
        }

        TrendRing? ring = _trendSource(key);
        if (ring is null || ring.Count < 2)
        {
            return;
        }

        var cell = _columns[5];
        var bounds = new Rectangle(cell.X + Dp(6), rowRect.Y + Dp(7), Math.Max(0, cell.Width - Dp(12)), Math.Max(0, rowRect.Height - Dp(14)));
        if (bounds.Width < 8 || bounds.Height < 6)
        {
            return;
        }

        double[] points = ring.Chronological();
        SparklineRenderer.Draw(graphics, bounds, points.AsSpan(0, ring.Count), _sparkBuffer, _sparkPen, null);
    }

    /// <summary>
    /// 展开 / 折叠箭头：用系统图标字体（Segoe Fluent Icons / MDL2 Assets）绘制字形，
    /// 比手绘折线在任意 DPI 下更锐利，也不再需要单独的画笔资源。
    /// </summary>
    /// <summary>进程行悬停提示：深色圆角卡片，显示进程名、PID 与完整可执行文件路径。</summary>
    private void DrawHoverTooltip(Graphics graphics)
    {
        if (_tipRow < 0 || string.IsNullOrEmpty(_tipTitle) || string.IsNullOrEmpty(_tipDetail))
        {
            return;
        }

        int maxWidth = Math.Min(Dp(620), Math.Max(Dp(240), Width - Dp(48)));
        var titleSize = TextRenderer.MeasureText(graphics, _tipTitle, _theme.SmallFont, new Size(maxWidth, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var lines = WrapText(graphics, _tipDetail, _theme.SmallFont, maxWidth - Dp(20));

        int pad = Dp(10);
        int lineHeight = TextRenderer.MeasureText(graphics, Loc.T("国Ag"), _theme.SmallFont).Height;
        int contentWidth = titleSize.Width;
        foreach (var line in lines)
        {
            contentWidth = Math.Max(contentWidth, TextRenderer.MeasureText(graphics, line, _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width);
        }

        int width = Math.Min(maxWidth, contentWidth + pad * 2);
        int height = pad * 2 + titleSize.Height + Dp(4) + lines.Count * lineHeight;

        int x = _tipPoint.X + Dp(18);
        int y = _tipPoint.Y + Dp(20);
        if (x + width > Width - Dp(6))
        {
            x = Math.Max(Dp(6), _tipPoint.X - width - Dp(12));
        }

        if (y + height > Height - Dp(6))
        {
            y = Math.Max(Dp(6), _tipPoint.Y - height - Dp(12));
        }

        var card = new Rectangle(x, y, width, height);
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var shadowPath = RoundedRect(new Rectangle(card.X + 1, card.Y + Dp(2), card.Width, card.Height), Dp(6)))
        using (var shadow = new SolidBrush(Color.FromArgb(_theme.IsDark ? 90 : 45, 0, 0, 0)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        using (var path = RoundedRect(card, Dp(6)))
        using (var fill = new SolidBrush(_theme.IsDark ? Color.FromArgb(44, 49, 59) : Color.White))
        using (var border = new Pen(_theme.Border))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        graphics.SmoothingMode = previousSmoothing;

        int textLeft = card.X + pad;
        int textTop = card.Y + pad;
        TextRenderer.DrawText(
            graphics,
            _tipTitle,
            _theme.SmallFont,
            new Rectangle(textLeft, textTop, card.Width - pad * 2, titleSize.Height),
            _theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        textTop += titleSize.Height + Dp(4);

        foreach (var line in lines)
        {
            TextRenderer.DrawText(
                graphics,
                line,
                _theme.SmallFont,
                new Rectangle(textLeft, textTop, card.Width - pad * 2, lineHeight),
                _theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            textTop += lineHeight;
        }
    }

    /// <summary>按像素宽度把长路径切成多行（优先在路径分隔符处换行）。</summary>
    private List<string> WrapText(Graphics graphics, string text, Font font, int maxWidth)
    {
        var lines = new List<string>(4);
        if (maxWidth <= Dp(20) || string.IsNullOrEmpty(text))
        {
            lines.Add(text ?? string.Empty);
            return lines;
        }

        string remaining = text;
        while (remaining.Length > 0 && lines.Count < 6)
        {
            int fit = remaining.Length;
            while (fit > 1 &&
                   TextRenderer.MeasureText(graphics, remaining[..fit], font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width > maxWidth)
            {
                fit--;
            }

            if (fit < remaining.Length)
            {
                int split = remaining.LastIndexOf('\\', Math.Max(0, fit - 1));
                if (split > fit / 3)
                {
                    fit = split + 1;
                }
            }

            lines.Add(remaining[..fit]);
            remaining = remaining[fit..];
        }

        if (remaining.Length > 0)
        {
            lines.Add("…" + remaining);
        }

        return lines;
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, radius * 2);
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawChevron(Graphics graphics, int centerX, int centerY, bool expanded)
    {
        _chevronFont ??= IconFont.Create(8f * (DeviceDpi / 96f));
        int box = Dp(14);
        var rect = new Rectangle(centerX - box / 2, centerY - box / 2, box, box);
        TextRenderer.DrawText(
            graphics,
            expanded ? IconFont.ChevronDown : IconFont.ChevronRight,
            _chevronFont,
            rect,
            _theme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (e.Y < _headerHeight)
        {
            HandleHeaderClick(e.X);
            return;
        }

        int index = RowIndexAt(e.Y);
        if (index < 0)
        {
            _selectedIndex = -1;
            Invalidate();
            return;
        }

        _selectedIndex = index;
        var row = _rows[index];
        if (!row.IsProcess)
        {
            ToggleExpand(row.App.Key);
        }

        Invalidate();
    }

    private void HandleHeaderClick(int x)
    {
        for (int column = 0; column < ColumnCount; column++)
        {
            int target = HeaderSortTargets[column];
            if (target < 0)
            {
                continue;
            }

            var cell = _columns[column];
            if (x < cell.X || x >= cell.Right)
            {
                continue;
            }

            var requested = (SortColumn)target;
            if (_sort == requested)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sort = requested;
                _sortDescending = requested != SortColumn.Name;
            }

            _scrollOffset = 0;
            RebuildRows();
            SortChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    private void ToggleExpand(string key)
    {
        if (!_expanded.Remove(key))
        {
            _expanded.Add(key);
        }

        RebuildRows();
    }

    private int RowIndexAt(int y)
    {
        if (y < _headerHeight)
        {
            return -1;
        }

        int index = (y - _headerHeight + _scrollOffset) / Math.Max(1, _rowHeight);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = RowIndexAt(e.Y);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }

        // 子项（进程行）悬停：显示完整路径的自绘提示
        int tipRow = -1;
        string? title = null;
        string? detail = null;
        if (index >= 0 && index < _rows.Count)
        {
            var row = _rows[index];
            if (row.IsProcess && !string.IsNullOrWhiteSpace(row.Process.ExecutablePath))
            {
                tipRow = index;
                title = row.Process.Name + "  ·  PID " + row.Process.Pid.ToString(System.Globalization.CultureInfo.CurrentCulture);
                detail = row.Process.ExecutablePath;
            }
        }

        if (tipRow != _tipRow || _tipPoint != e.Location)
        {
            _tipRow = tipRow;
            _tipTitle = title;
            _tipDetail = detail;
            _tipPoint = e.Location;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1 || _tipRow != -1)
        {
            _hoverIndex = -1;
            _tipRow = -1;
            _tipTitle = null;
            _tipDetail = null;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scrollBar.Visible)
        {
            return;
        }

        int maxValue = Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
        int next = Math.Clamp(_scrollBar.Value - e.Delta, _scrollBar.Minimum, maxValue);
        if (next != _scrollBar.Value)
        {
            _scrollBar.Value = next;
        }
    }

    /// <inheritdoc />
    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.Enter => true,
        _ => base.IsInputKey(keyData),
    };

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0)
        {
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Keys.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Keys.PageUp:
                MoveSelection(-Math.Max(1, (Height - _headerHeight) / Math.Max(1, _rowHeight)));
                e.Handled = true;
                break;
            case Keys.PageDown:
                MoveSelection(Math.Max(1, (Height - _headerHeight) / Math.Max(1, _rowHeight)));
                e.Handled = true;
                break;
            case Keys.Left:
            case Keys.Right:
            case Keys.Space:
            case Keys.Enter:
                if (_selectedIndex >= 0 && _selectedIndex < _rows.Count)
                {
                    var row = _rows[_selectedIndex];
                    if (!row.IsProcess)
                    {
                        ToggleExpand(row.App.Key);
                    }
                }

                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int index = Math.Clamp(_selectedIndex + delta, 0, _rows.Count - 1);
        if (index == _selectedIndex)
        {
            return;
        }

        _selectedIndex = index;
        EnsureVisible(index);
        Invalidate();
    }

    private void EnsureVisible(int index)
    {
        if (!_scrollBar.Visible)
        {
            return;
        }

        int viewHeight = Math.Max(1, Height - _headerHeight);
        int top = index * _rowHeight;
        int desired = _scrollOffset;
        if (top < _scrollOffset)
        {
            desired = top;
        }
        else if (top + _rowHeight > _scrollOffset + viewHeight)
        {
            desired = top + _rowHeight - viewHeight;
        }

        int maxValue = Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
        desired = Math.Clamp(desired, 0, maxValue);
        if (desired != _scrollBar.Value)
        {
            _scrollBar.Value = desired;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
            _scrollBar.ValueChanged -= OnScrollBarValueChanged;
            _scrollBar.Dispose();
        }

        base.Dispose(disposing);
    }

    private struct Row
    {
        public bool IsProcess;
        public AppSample App;
        public AppProcessInfo Process;
    }
}
