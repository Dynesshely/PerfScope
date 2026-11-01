namespace PerfScope.App.Infrastructure;

/// <summary>
/// 深色主题的 ListView（Details 视图）：原生列头与空白区在深色模式下是亮色，
/// 这里改为自绘列头、行、复选框与边框，保证和主界面配色一致。
/// </summary>
internal sealed class ThemedListView : ListView
{
    private readonly Theme _theme;

    public ThemedListView(Theme theme)
    {
        _theme = theme;
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        MultiSelect = false;
        HideSelection = false;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BorderStyle = BorderStyle.None;
        OwnerDraw = true;
        DoubleBuffered = true;
        BackColor = theme.Surface;
        ForeColor = theme.TextPrimary;
    }

    private int[]? _baseColumnWidths;
    private bool _fitting;

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitColumnsToClient();
    }

    /// <summary>按当前控件宽度等比缩放列宽，避免出现横向滚动条。</summary>
    public void FitColumnsToClient()
    {
        if (_fitting || Columns.Count == 0)
        {
            return;
        }

        if (_baseColumnWidths is null || _baseColumnWidths.Length != Columns.Count)
        {
            _baseColumnWidths = Columns.Cast<ColumnHeader>().Select(column => Math.Max(24, column.Width)).ToArray();
        }

        int available = ClientRectangle.Width - Dp(2);
        int total = _baseColumnWidths.Sum();
        if (available <= Dp(80) || total <= 0)
        {
            return;
        }

        _fitting = true;
        try
        {
            double scale = available / (double)total;
            int used = 0;
            for (int i = 0; i < Columns.Count; i++)
            {
                int width = i == Columns.Count - 1
                    ? Math.Max(Dp(40), available - used)
                    : Math.Max(Dp(40), (int)Math.Round(_baseColumnWidths[i] * scale));
                Columns[i].Width = width;
                used += width;
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var background = new SolidBrush(_theme.HeaderBackground))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        var textBounds = new Rectangle(e.Bounds.X + Dp(8), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Dp(10)), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header!.Text,
            _theme.SmallFont,
            textBounds,
            _theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        using var border = new Pen(_theme.Border);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // 行的绘制统一放在 OnDrawSubItem 里完成
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item!.Selected;
        var background = selected
            ? _theme.RowSelected
            : (e.ItemIndex % 2 == 0 ? _theme.Surface : _theme.SurfaceAlt);

        using (var brush = new SolidBrush(background))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        var textBounds = new Rectangle(e.Bounds.X + Dp(8), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Dp(10)), e.Bounds.Height);
        if (e.ColumnIndex == 0 && CheckBoxes)
        {
            int box = Dp(14);
            var boxRect = new Rectangle(e.Bounds.X + Dp(6), e.Bounds.Y + (e.Bounds.Height - box) / 2, box, box);
            DrawCheckBox(e.Graphics, boxRect, e.Item.Checked);
            textBounds = new Rectangle(boxRect.Right + Dp(6), e.Bounds.Y, Math.Max(0, e.Bounds.Width - boxRect.Width - Dp(14)), e.Bounds.Height);
        }

        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem!.Text,
            _theme.SmallFont,
            textBounds,
            selected ? _theme.TextPrimary : _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawCheckBox(Graphics graphics, Rectangle bounds, bool isChecked)
    {
        using var background = new SolidBrush(_theme.SurfaceAlt);
        graphics.FillRectangle(background, bounds);

        var borderColor = isChecked ? _theme.Accent : _theme.Border;
        using (var border = new Pen(borderColor))
        {
            graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        if (!isChecked)
        {
            return;
        }

        using var pen = new Pen(_theme.Accent, Math.Max(1.6f, Dp(2)));
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int left = bounds.X + bounds.Width / 4;
        int middle = bounds.X + bounds.Width / 2;
        int right = bounds.Right - bounds.Width / 4;
        int top = bounds.Y + bounds.Height / 3;
        int bottom = bounds.Bottom - bounds.Height / 4;
        graphics.DrawLines(pen, new[]
        {
            new Point(left, (top + bottom) / 2),
            new Point(middle, bottom),
            new Point(right, top),
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Items.Count == 0)
        {
            using var background = new SolidBrush(_theme.Surface);
            e.Graphics.FillRectangle(background, ClientRectangle);
        }

        using var border = new Pen(_theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }
}