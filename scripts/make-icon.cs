#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 生成 PerfScope 应用图标（多尺寸 ICO，PNG 压缩子图），纯 BCL 实现，不依赖外部资源。
var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = new List<(int Size, byte[] Png)>();
foreach (var size in sizes)
{
    using var bitmap = Render(size);
    using var ms = new MemoryStream();
    bitmap.Save(ms, ImageFormat.Png);
    frames.Add((size, ms.ToArray()));
}

var outputIndex = Array.FindIndex(args, a => a == "--out");
var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : "app.ico";
var directory = Path.GetDirectoryName(Path.GetFullPath(output));
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
using (var bw = new BinaryWriter(fs))
{
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)png.Length);
        bw.Write((uint)offset);
        offset += png.Length;
    }

    foreach (var (_, png) in frames)
    {
        bw.Write(png);
    }
}

if (Array.IndexOf(args, "--preview") >= 0)
{
    var previewPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "icon-preview.png");
    using var preview = Render(256);
    preview.Save(previewPath, ImageFormat.Png);
    Console.WriteLine($"已生成预览 {previewPath}");
}

Console.WriteLine($"已生成 {output}（{new FileInfo(output).Length / 1024.0:F1} KB，{frames.Count} 个尺寸）");

static Bitmap Render(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    var pad = size * 0.045f;
    var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
    using (var body = Rounded(rect, size * 0.24f))
    using (var fill = new LinearGradientBrush(rect, Color.FromArgb(255, 32, 36, 44), Color.FromArgb(255, 16, 18, 23), 65f))
    {
        g.FillPath(fill, body);
        using var border = new Pen(Color.FromArgb(70, 120, 230, 200), Math.Max(1f, size * 0.02f));
        g.DrawPath(border, body);
    }

    // 折线「脉动」：基线 + 一个尖峰，小尺寸下也清晰
    var points = new[]
    {
        new PointF(0.20f * size, 0.58f * size),
        new PointF(0.34f * size, 0.58f * size),
        new PointF(0.42f * size, 0.36f * size),
        new PointF(0.50f * size, 0.74f * size),
        new PointF(0.58f * size, 0.50f * size),
        new PointF(0.66f * size, 0.58f * size),
        new PointF(0.80f * size, 0.58f * size),
    };
    using (var pen = new Pen(Color.FromArgb(255, 82, 214, 176), Math.Max(1.4f, size * 0.082f)))
    {
        pen.LineJoin = LineJoin.Round;
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        g.DrawLines(pen, points);
    }

    return bitmap;
}

static GraphicsPath Rounded(RectangleF rect, float radius)
{
    var path = new GraphicsPath();
    var d = radius * 2;
    path.AddArc(rect.X, rect.Y, d, d, 180, 90);
    path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
    path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}