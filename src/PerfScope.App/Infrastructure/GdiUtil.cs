using System.Drawing.Drawing2D;

namespace PerfScope.App.Infrastructure;

/// <summary>GDI+ 绘制小工具，集中处理圆角矩形等重复绘制逻辑。</summary>
internal static class GdiUtil
{
    /// <summary>构造一个圆角矩形路径，调用方负责释放。</summary>
    public static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (r <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = r * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>填充圆角矩形（内部临时创建画刷与路径，退出时释放）。</summary>
    public static void FillRoundedRect(Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRect(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }
}
