using System.Drawing.Drawing2D;

namespace PerfScope.App.Controls;

/// <summary>
/// Sparkline 绘制器。所有内存都在调用方复用（点数组、画笔、画刷），
/// 保证每秒重绘时几乎零分配。
/// </summary>
internal static class SparklineRenderer
{
    /// <summary>
    /// 在 <paramref name="bounds"/> 内绘制一条趋势线。
    /// <paramref name="buffer"/> 长度需不小于 <c>values.Length + 2</c>，用于拼线/填充多边形。
    /// </summary>
    /// <param name="fixedMaximum">
    /// 固定的纵轴上限：传 0 表示按当前窗口内的最大值自适应（用于单应用趋势）；
    /// 传 100 表示固定 0~100 百分比刻度（用于整机 CPU / 内存，曲线高度才有绝对含义，
    /// 也不会因为"最大值就等于当前值"而把曲线顶到天花板上、填充成一大块色块）。
    /// </param>
    public static void Draw(
        Graphics graphics,
        Rectangle bounds,
        ReadOnlySpan<double> values,
        PointF[] buffer,
        Pen pen,
        Brush? fillBrush,
        double fixedMaximum = 0)
    {
        if (values.Length < 2 || bounds.Width < 6 || bounds.Height < 6 || buffer.Length < values.Length + 2)
        {
            return;
        }

        double maximum = fixedMaximum;
        if (maximum <= 0.0001)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > maximum)
                {
                    maximum = values[i];
                }
            }
        }

        if (maximum <= 0.0001)
        {
            maximum = 1;
        }

        float stepX = bounds.Width / (float)(values.Length - 1);
        float top = bounds.Top + 1;
        float bottom = bounds.Bottom - 1;
        float height = Math.Max(1f, bottom - top);

        for (int i = 0; i < values.Length; i++)
        {
            double normalized = Math.Clamp(values[i] / maximum, 0, 1);
            float x = bounds.Left + i * stepX;
            float y = bottom - (float)(normalized * height);
            buffer[i] = new PointF(x, y);
        }

        if (fillBrush is not null)
        {
            buffer[values.Length] = new PointF(bounds.Right, bottom);
            buffer[values.Length + 1] = new PointF(bounds.Left, bottom);
            graphics.FillPolygon(fillBrush, buffer.AsSpan(0, values.Length + 2));
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLines(pen, buffer.AsSpan(0, values.Length));
    }
}