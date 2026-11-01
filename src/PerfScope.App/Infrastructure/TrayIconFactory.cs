using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 用 GDI+ 现画托盘图标，不依赖外部 .ico 文件（避免发布时漏文件）。
/// 图案：深色圆角方块 + 渐变色脉搏折线，直观表达「监控」。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>创建托盘图标；<paramref name="size"/> 一般取 16 或 32。</summary>
    public static Icon Create(int size)
    {
        if (size < 16)
        {
            size = 16;
        }

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var bounds = new Rectangle(0, 0, size - 1, size - 1);
            using (var background = new LinearGradientBrush(
                       bounds,
                       Color.FromArgb(38, 46, 62),
                       Color.FromArgb(18, 21, 27),
                       LinearGradientMode.ForwardDiagonal))
            using (var path = GdiUtil.CreateRoundedRect(bounds, Math.Max(2, size / 5)))
            {
                graphics.FillPath(background, path);
            }

            using (var border = new Pen(Color.FromArgb(120, 76, 154, 255), Math.Max(1f, size / 16f)))
            using (var path = GdiUtil.CreateRoundedRect(bounds, Math.Max(2, size / 5)))
            {
                graphics.DrawPath(border, path);
            }

            // 脉搏折线：中线的三个峰谷
            float pad = size * 0.18f;
            float width = size - pad * 2;
            float mid = size * 0.55f;
            PointF[] points =
            {
                new(pad, mid),
                new(pad + width * 0.26f, mid),
                new(pad + width * 0.38f, mid - size * 0.24f),
                new(pad + width * 0.52f, mid + size * 0.2f),
                new(pad + width * 0.66f, mid),
                new(pad + width, mid),
            };

            using var line = new Pen(Color.FromArgb(76, 154, 255), Math.Max(1.4f, size / 11f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            graphics.DrawLines(line, points);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
