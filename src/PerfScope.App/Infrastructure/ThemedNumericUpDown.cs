using System.Drawing.Drawing2D;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 深色主题的数字输入框：原生 NumericUpDown 的上下按钮固定是系统亮色，
/// 这里隐藏原生按钮，改由控件自身在右侧画两个主题化按钮，并接管点击/滚轮。
/// 中间仍然是原生编辑框，所以键盘输入、选中、剪贴板行为都和原来一致。
/// </summary>
internal sealed class ThemedNumericUpDown : NumericUpDown
{
    private readonly Theme _theme;
    private int _buttonWidth;

    public ThemedNumericUpDown(Theme theme)
    {
        _theme = theme;
        BorderStyle = BorderStyle.None;
        BackColor = theme.SurfaceAlt;
        ForeColor = theme.TextPrimary;
        ThousandsSeparator = false;
        TextAlign = HorizontalAlignment.Left;
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyLayout();
    }

    /// <summary>隐藏原生加减按钮，并把编辑框缩到左侧，右侧留给自绘按钮。</summary>
    private void ApplyLayout()
    {
        if (!IsHandleCreated || Controls.Count < 2)
        {
            return;
        }

        _buttonWidth = Dp(22);
        Controls[0].Visible = false;              // UpDownButtons
        var edit = Controls[1];
        edit.Left = Dp(6);
        edit.Top = Math.Max(0, (Height - edit.Height) / 2);
        edit.Width = Math.Max(Dp(20), Width - _buttonWidth - Dp(8));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width, Height);
        using (var background = new SolidBrush(_theme.SurfaceAlt))
        {
            graphics.FillRectangle(background, bounds);
        }

        using (var border = new Pen(_theme.Border))
        {
            graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }

        // 右侧按钮条
        int buttonWidth = _buttonWidth > 0 ? _buttonWidth : Dp(22);
        var strip = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Math.Max(0, Height - 2));
        using (var stripBrush = new SolidBrush(_theme.HeaderBackground))
        {
            graphics.FillRectangle(stripBrush, strip);
        }

        using (var separator = new Pen(_theme.Border))
        {
            graphics.DrawLine(separator, strip.Left, strip.Top, strip.Left, strip.Bottom);
        }

        int half = strip.Height / 2;
        DrawArrow(graphics, new Rectangle(strip.Left, strip.Top, strip.Width, half), up: true);
        DrawArrow(graphics, new Rectangle(strip.Left, strip.Top + half, strip.Width, strip.Height - half), up: false);
    }

    private void DrawArrow(Graphics graphics, Rectangle bounds, bool up)
    {
        int size = Dp(4);
        int centerX = bounds.X + bounds.Width / 2;
        int centerY = bounds.Y + bounds.Height / 2;
        using var brush = new SolidBrush(_theme.TextSecondary);
        Point[] points = up
            ? new[] { new Point(centerX - size, centerY + size / 2), new Point(centerX + size, centerY + size / 2), new Point(centerX, centerY - size) }
            : new[] { new Point(centerX - size, centerY - size / 2), new Point(centerX + size, centerY - size / 2), new Point(centerX, centerY + size) };
        graphics.FillPolygon(brush, points);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int buttonWidth = _buttonWidth > 0 ? _buttonWidth : Dp(22);
        if (e.X < Width - buttonWidth)
        {
            return;
        }

        if (e.Y < Height / 2)
        {
            UpButton();
        }
        else
        {
            DownButton();
        }

        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Invalidate();
    }
}
