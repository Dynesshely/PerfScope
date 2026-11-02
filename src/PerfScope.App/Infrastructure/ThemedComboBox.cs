namespace PerfScope.App.Infrastructure;

/// <summary>
/// 深色主题下拉框：条目自绘（深色底 + 选中高亮），并用 uxtheme 把系统绘制的按钮/边框
/// 切到深色方案；最后补一条主题色边框，避免深色页面里出现一块白色控件。
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private readonly Theme _theme;

    public ThemedComboBox(Theme theme)
    {
        _theme = theme;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = theme.SurfaceAlt;
        ForeColor = theme.TextPrimary;
        ItemHeight = Dp(22);
        Font = theme.UiFont;
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkModeInterop.SetTheme(Handle, _theme.IsDark, "DarkMode_CFD");
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var background = selected ? _theme.RowSelected : _theme.SurfaceAlt;
        using (var brush = new SolidBrush(background))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        string text = Convert.ToString(Items[e.Index], System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        var textBounds = new Rectangle(e.Bounds.X + Dp(8), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Dp(10)), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            _theme.UiFont,
            textBounds,
            _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != 0x000F) // WM_PAINT
        {
            return;
        }

        using var graphics = Graphics.FromHwnd(Handle);
        RenderBorder(graphics);
        RenderArrow(graphics);
    }

    /// <summary>在系统绘制之后补一条主题边框（内部调用，避免与 WndProc 重入）。</summary>
    private void RenderBorder(Graphics graphics)
    {
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var pen = new Pen(_theme.Border);
        graphics.DrawRectangle(pen, bounds);
    }

    /// <summary>系统绘制的下拉按钮是亮色方块，这里整块覆盖成主题色并自绘箭头。</summary>
    private void RenderArrow(Graphics graphics)
    {
        int arrowWidth = Math.Max(Dp(18), Height);
        var arrowRect = new Rectangle(Width - arrowWidth - 1, 1, arrowWidth, Math.Max(0, Height - 2));
        if (arrowRect.Width <= 0 || arrowRect.Height <= 0)
        {
            return;
        }

        using (var fill = new SolidBrush(_theme.HeaderBackground))
        {
            graphics.FillRectangle(fill, arrowRect);
        }

        using (var separator = new Pen(_theme.Border))
        {
            graphics.DrawLine(separator, arrowRect.Left, arrowRect.Top, arrowRect.Left, arrowRect.Bottom);
        }

        int size = Dp(4);
        int centerX = arrowRect.X + arrowRect.Width / 2;
        int centerY = arrowRect.Y + arrowRect.Height / 2;
        using var brush = new SolidBrush(_theme.TextSecondary);
        graphics.FillPolygon(brush, new[]
        {
            new Point(centerX - size, centerY - size / 2),
            new Point(centerX + size, centerY - size / 2),
            new Point(centerX, centerY + size),
        });
    }
}