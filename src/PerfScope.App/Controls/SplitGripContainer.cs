using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;

namespace PerfScope.App.Controls;

/// <summary>
/// 带可见「小把手」的左右分隔容器：在分割条中央画一个圆角把手（三个点），
/// 拖拽行为沿用 <see cref="SplitContainer"/> 原生实现，不重新发明拖动逻辑。
/// </summary>
internal sealed class SplitGripContainer : SplitContainer
{
    private Theme _theme;

    /// <summary>创建分隔容器。</summary>
    public SplitGripContainer(Theme theme)
    {
        _theme = theme;
        Orientation = Orientation.Vertical;
        BackColor = theme.Border;
        SplitterWidth = Dp(11);
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        SplitterMoved += (_, _) => Invalidate();
        SplitterMoving += (_, _) => Invalidate();
    }

    /// <summary>切换主题。</summary>
    public void SetTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Border;
        Invalidate();
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawGrip(e.Graphics);
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private void DrawGrip(Graphics graphics)
    {
        if (Orientation != Orientation.Vertical || SplitterWidth <= 0 || Height <= 0)
        {
            return;
        }

        var strip = new Rectangle(SplitterDistance, 0, SplitterWidth, Height);
        if (strip.Width <= 0 || strip.Right > Width)
        {
            return;
        }

        using (var background = new SolidBrush(_theme.Border))
        {
            graphics.FillRectangle(background, strip);
        }

        int handleHeight = Math.Min(Height, Dp(56));
        var handle = new Rectangle(strip.X + Dp(1), (Height - handleHeight) / 2, Math.Max(1, strip.Width - Dp(2)), handleHeight);

        using (var path = Rounded(handle, Math.Max(2, Dp(4))))
        using (var fill = new SolidBrush(_theme.SurfaceAlt))
        using (var border = new Pen(_theme.GridLine))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        int dot = Math.Max(2, Dp(2));
        int spacing = dot * 3;
        int centerX = strip.X + strip.Width / 2;
        int centerY = Height / 2;
        using var dotBrush = new SolidBrush(_theme.TextMuted);
        for (int i = -1; i <= 1; i++)
        {
            graphics.FillEllipse(dotBrush, centerX - dot / 2f, centerY + i * spacing - dot / 2f, dot, dot);
        }
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
