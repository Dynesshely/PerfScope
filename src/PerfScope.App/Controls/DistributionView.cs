using PerfScope.App.Localization;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;

namespace PerfScope.App.Controls;

/// <summary>
/// 右侧「分布」面板：顶部分段标签（内存分布 / CPU 分布），下面是对应的 Treemap。
/// 只创建一个控件实例、通过切换可见性复用，切换标签不重建树图，开销可以忽略。
/// </summary>
internal sealed class DistributionView : Panel
{
    private static readonly string[] TabTitles = { Loc.T("内存分布"), Loc.T("CPU 分布") };

    private readonly AppTreemapControl _memoryView;
    private readonly AppTreemapControl _cpuView;
    private readonly Rectangle[] _tabRects = new Rectangle[TabTitles.Length];
    private Theme _theme;
    private int _selected;
    private int _hoverTab = -1;

    /// <summary>创建分布面板。</summary>
    public DistributionView(Theme theme, IconProvider icons)
    {
        _theme = theme;
        _memoryView = new AppTreemapControl(theme, TreemapMetric.Memory, icons) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _cpuView = new AppTreemapControl(theme, TreemapMetric.Cpu, icons) { Dock = DockStyle.Fill, Margin = Padding.Empty, Visible = false };
        BackColor = theme.Surface;
        Padding = new Padding(0, Dp(34), 0, 0);
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        Controls.Add(_memoryView);
        Controls.Add(_cpuView);
        _memoryView.BringToFront();
    }

    /// <summary>当前选中的标签：0 = 内存分布，1 = CPU 分布。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int clamped = Math.Clamp(value, 0, TabTitles.Length - 1);
            if (clamped == _selected)
            {
                return;
            }

            _selected = clamped;
            _memoryView.Visible = _selected == 0;
            _cpuView.Visible = _selected == 1;
            Invalidate();
        }
    }

    /// <summary>切换主题。</summary>
    public void SetTheme(Theme theme)
    {
        if (ReferenceEquals(theme, _theme))
        {
            return;
        }

        _theme = theme;
        BackColor = theme.Surface;
        _memoryView.SetTheme(theme);
        _cpuView.SetTheme(theme);
        Invalidate();
    }

    /// <summary>推入一帧应用数据。</summary>
    public void SetApps(IReadOnlyList<AppSample> apps)
    {
        _memoryView.SetApps(apps);
        _cpuView.SetApps(apps);
    }

    /// <summary>物理内存总量（内存分布用）。</summary>
    public void SetPhysicalMemory(long totalBytes) => _memoryView.SetPhysicalMemory(totalBytes);

    /// <summary>系统在用物理内存（内存分布用）。</summary>
    public void SetUsedMemory(long usedBytes) => _memoryView.SetUsedMemory(usedBytes);

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using (var background = new SolidBrush(_theme.Surface))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        using (var divider = new Pen(_theme.Border))
        {
            graphics.DrawLine(divider, 0, Padding.Top - 1, Width, Padding.Top - 1);
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int left = Dp(12);
        int top = Dp(7);
        int height = Dp(23);
        for (int i = 0; i < TabTitles.Length; i++)
        {
            int width = TextRenderer.MeasureText(graphics, TabTitles[i], _theme.UiFont, new Size(int.MaxValue, height), TextFormatFlags.NoPadding).Width + Dp(22);
            var rect = new Rectangle(left, top, width, height);
            _tabRects[i] = rect;

            bool active = i == _selected;
            bool hover = i == _hoverTab && !active;
            var fill = active
                ? _theme.AccentSoft
                : hover
                    ? _theme.RowHover
                    : _theme.Surface;

            using (var path = Rounded(rect, Dp(6)))
            using (var brush = new SolidBrush(fill))
            {
                graphics.FillPath(brush, path);
                if (active)
                {
                    using var border = new Pen(Theme.WithAlpha(_theme.Accent, 150));
                    graphics.DrawPath(border, path);
                }
            }

            TextRenderer.DrawText(
                graphics,
                TabTitles[i],
                active ? _theme.UiBoldFont : _theme.UiFont,
                rect,
                active ? _theme.TextPrimary : _theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            left = rect.Right + Dp(8);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = HitTest(e.Location);
        if (hover != _hoverTab)
        {
            _hoverTab = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverTab >= 0)
        {
            _hoverTab = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            SelectedIndex = hit;
        }
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < _tabRects.Length; i++)
        {
            if (_tabRects[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
