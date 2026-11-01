using PerfScope.App.Infrastructure;

namespace PerfScope.App.Controls;

/// <summary>
/// 轻量扁平渲染器：让 ToolStrip / StatusStrip / 右键菜单跟随深色或浅色主题，
/// 不用第三方控件库，也不依赖系统配色。
/// </summary>
internal sealed class FlatToolStripRenderer : ToolStripRenderer
{
    private readonly Theme _theme;

    /// <summary>用指定主题创建渲染器。</summary>
    public FlatToolStripRenderer(Theme theme)
    {
        _theme = theme;
    }

    /// <inheritdoc />
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(e.ToolStrip is StatusStrip ? _theme.Surface : _theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    /// <inheritdoc />
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(_theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            return;
        }

        using var borderPen = new Pen(_theme.Border);
        e.Graphics.DrawLine(borderPen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    /// <inheritdoc />
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
        if (e.Item.Pressed)
        {
            GdiUtil.FillRoundedRect(e.Graphics, bounds, 4, _theme.RowSelected);
        }
        else if (e.Item.Selected || e.Item is ToolStripButton { Checked: true })
        {
            GdiUtil.FillRoundedRect(e.Graphics, bounds, 4, _theme.RowHover);
        }
    }

    /// <inheritdoc />
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
        {
            return;
        }

        var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        GdiUtil.FillRoundedRect(e.Graphics, bounds, 4, _theme.RowSelected);
    }

    /// <inheritdoc />
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var custom = e.Item.Tag is Color tagColor ? tagColor : (Color?)null;
        e.TextColor = custom ?? (e.Item.Enabled ? _theme.TextPrimary : _theme.TextMuted);
        base.OnRenderItemText(e);
    }

    /// <inheritdoc />
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_theme.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }

    /// <inheritdoc />
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    /// <inheritdoc />
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _theme.TextSecondary;
        base.OnRenderArrow(e);
    }
}
