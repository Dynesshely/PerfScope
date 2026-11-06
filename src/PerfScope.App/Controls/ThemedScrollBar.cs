using System.ComponentModel;
using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;

namespace PerfScope.App.Controls;

/// <summary>
/// 跟随主题的自绘竖向滚动条：细圆角滑块、悬停变亮、拖拽高亮、滚轮与轨道翻页都支持。
/// 原生 <see cref="VScrollBar"/> 由系统绘制、无法改配色，所以这里换成自绘实现；
/// 对外属性语义与 VScrollBar 保持一致（Minimum / Maximum / LargeChange / SmallChange / Value），方便直接替换。
/// </summary>
internal sealed class ThemedScrollBar : Control
{
    private Theme _theme;
    private int _minimum;
    private int _maximum;
    private int _largeChange = 1;
    private int _smallChange = 1;
    private int _value;
    private bool _dragging;
    private bool _hover;
    private int _dragOffset;

    /// <summary>创建滚动条。</summary>
    public ThemedScrollBar(Theme theme)
    {
        _theme = theme;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = theme.Surface;
        TabStop = false;
    }

    /// <summary>值变化事件（与 VScrollBar.ValueChanged 同义）。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>最小值。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value)
            {
                return;
            }

            _minimum = value;
            ClampIntoRange();
            Invalidate();
        }
    }

    /// <summary>最大值。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value)
            {
                return;
            }

            _maximum = value;
            ClampIntoRange();
            Invalidate();
        }
    }

    /// <summary>一页的跨度（决定滑块长度）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(1)]
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            ClampIntoRange();
            Invalidate();
        }
    }

    /// <summary>一格跨度（滚轮/翻页用）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(1)]
    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    /// <summary>当前值。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            int maximum = MaxValue;
            int clamped = Math.Clamp(value, _minimum, Math.Max(_minimum, maximum));
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>切换主题。</summary>
    public void SetTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Surface;
        Invalidate();
    }

    private int MaxValue => Math.Max(0, _maximum - _largeChange + 1);

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void ClampIntoRange()
    {
        int clamped = Math.Clamp(_value, _minimum, Math.Max(_minimum, MaxValue));
        if (clamped != _value)
        {
            _value = clamped;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle ThumbRect()
    {
        int trackHeight = Math.Max(1, Height);
        int range = Math.Max(1, _maximum - _minimum + 1);
        int thumbHeight = (int)Math.Round((double)trackHeight * _largeChange / range);
        thumbHeight = Math.Clamp(thumbHeight, Dp(28), trackHeight);

        int maximum = MaxValue;
        double ratio = maximum <= 0 ? 0 : Math.Clamp((double)(_value - _minimum) / maximum, 0, 1);
        int top = (int)Math.Round(ratio * (trackHeight - thumbHeight));
        int inset = Dp(3);
        return new Rectangle(inset, top, Math.Max(2, Width - inset * 2), thumbHeight);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using (var background = new SolidBrush(_theme.Surface))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        if (Height <= 0 || Width <= 0)
        {
            return;
        }

        var thumb = ThumbRect();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Color color = _dragging
            ? _theme.Accent
            : _hover
                ? Theme.WithAlpha(_theme.TextSecondary, 210)
                : Theme.WithAlpha(_theme.TextMuted, 150);

        using var path = Rounded(thumb, Math.Max(2, thumb.Width / 2));
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = e.Y - thumb.Top;
            Capture = true;
            Invalidate();
            return;
        }

        // 点到轨道：翻一页
        Value += e.Y < thumb.Top ? -_largeChange : _largeChange;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            bool hover = ThumbRect().Contains(e.Location);
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }

            return;
        }

        var thumb = ThumbRect();
        int travel = Math.Max(1, Height - thumb.Height);
        int top = Math.Clamp(e.Y - _dragOffset, 0, travel);
        int maximum = MaxValue;
        if (maximum <= 0)
        {
            return;
        }

        int target = _minimum + (int)Math.Round((double)top / travel * maximum);
        Value = target;
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = ThumbRect().Contains(PointToClient(Cursor.Position));
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover && !_dragging)
        {
            _hover = false;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int steps = e.Delta / 120;
        if (steps == 0)
        {
            steps = e.Delta > 0 ? 1 : -1;
        }

        Value -= steps * Math.Max(1, _smallChange * 3);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
