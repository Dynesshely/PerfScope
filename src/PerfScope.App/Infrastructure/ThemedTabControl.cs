using System.Drawing.Drawing2D;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 深色主题可用的 TabControl：原生 TabControl 的标签条与边框由系统绘制，
/// 深色模式下会露出白色底和亮边框，所以这里整块自绘（标签、选中下划线、内容区边框）。
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    private readonly Theme _theme;

    public ThemedTabControl(Theme theme)
    {
        _theme = theme;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);

        Appearance = TabAppearance.Normal;
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(Dp(92), Dp(30));
        Padding = new Point(Dp(12), Dp(6));
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var background = new SolidBrush(_theme.Background))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        // 内容区边框：比系统默认的亮色边框低调得多
        var display = DisplayRectangle;
        if (display.Width > 0 && display.Height > 0)
        {
            using var border = new Pen(_theme.Border);
            graphics.DrawRectangle(border, display.X - 1, display.Y - 1, display.Width + 1, display.Height + 1);
        }

        for (int i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            bool selected = SelectedIndex == i;
            if (selected)
            {
                using var active = new SolidBrush(_theme.SurfaceAlt);
                graphics.FillRectangle(active, bounds);
            }

            TextRenderer.DrawText(
                graphics,
                TabPages[i].Text,
                selected ? _theme.UiBoldFont : _theme.UiFont,
                bounds,
                selected ? _theme.TextPrimary : _theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (selected)
            {
                using var accent = new SolidBrush(_theme.Accent);
                graphics.FillRectangle(accent, bounds.X, bounds.Bottom - Dp(2), bounds.Width, Dp(2));
            }
        }

        // 标签条底部的一条分隔线，让标签与内容区分开
        if (TabCount > 0)
        {
            int y = GetTabRect(0).Bottom - 1;
            using var separator = new Pen(_theme.Border);
            graphics.DrawLine(separator, 0, y, Width, y);
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        // 全部在 OnPaint 里画，避免标签被绘制两次
    }
}
