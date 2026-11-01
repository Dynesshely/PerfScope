using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;

namespace PerfScope.App.Controls;

/// <summary>
/// 内存树图（Treemap / 矩形树图，SpaceSniffer 用的就是这一类可视化）：
/// 每个应用一个矩形，**面积正比于它当前占用的内存**，方块越大表示占用越多。
/// 布局采用 squarified treemap（平方化）算法，尽量让方块接近正方形，便于辨认和比较。
/// </summary>
internal sealed class AppTreemapControl : Control
{
    private sealed record Block(
        RectangleF Bounds,
        string Key,
        string Name,
        long Bytes,
        double CpuPercent,
        int ProcessCount,
        Color Fill);

    private readonly List<Block> _blocks = new();
    private readonly ToolTip _tooltip = new()
    {
        InitialDelay = 250,
        ReshowDelay = 120,
        AutoPopDelay = 8000,
    };

    private Theme _theme;
    private AppSample[] _apps = Array.Empty<AppSample>();
    private long _physicalBytes;
    private long _usedBytes;
    private long _attributedBytes;
    private long _totalBytes;
    private int _hovered = -1;

    /// <summary>创建树图控件。</summary>
    public AppTreemapControl(Theme theme)
    {
        _theme = theme;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = theme.Surface;
    }

    /// <summary>切换主题。</summary>
    public void SetTheme(Theme theme)
    {
        if (ReferenceEquals(theme, _theme))
        {
            return;
        }

        _theme = theme;
        BackColor = theme.Surface;
        Rebuild();
        Invalidate();
    }

    /// <summary>推入一帧的应用数据（按内存生成方块）。</summary>
    public void SetApps(IReadOnlyList<AppSample> apps)
    {
        _apps = apps as AppSample[] ?? apps.ToArray();
        Rebuild();
        Invalidate();
    }

    /// <summary>物理内存总量，用于顶部文案。</summary>
    public void SetPhysicalMemory(long totalBytes)
    {
        _physicalBytes = totalBytes;
    }

    /// <summary>系统「在用」物理内存：用于算出无法归属到任何应用的那部分。</summary>
    public void SetUsedMemory(long usedBytes)
    {
        _usedBytes = Math.Max(0, usedBytes);
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
        Invalidate();
    }

    /// <summary>重新计算方块布局（squarified treemap）。</summary>
    private void Rebuild()
    {
        _blocks.Clear();
        _totalBytes = 0;
        if (_apps.Length == 0 || Width <= Dp(40) || Height <= Dp(60))
        {
            return;
        }

        var entries = new List<Entry>();
        foreach (var app in _apps)
        {
            if (app.WorkingSetBytes > 0)
            {
                entries.Add(new Entry(app.Key, app.DisplayName, app.WorkingSetBytes, app.CpuPercent, app.ProcessCount, EntryKind.Application));
            }
        }

        if (entries.Count == 0)
        {
            return;
        }

        long attributed = entries.Sum(e => e.Bytes);
        _attributedBytes = attributed;

        // 系统「在用」里总有一部分无法归属到任何进程（内核、驱动、文件缓存、压缩内存、共享页等）。
        long used = Math.Max(_usedBytes, attributed);
        long remainder = used - attributed;
        if (remainder > Math.Max(256L << 20, used / 50))
        {
            entries.Add(new Entry("__unattributed__", "未归属（系统 / 缓存 / 共享）", remainder, 0, 0, EntryKind.Unattributed));
        }

        // 空闲物理内存也按比例画出来，这样整张树图的面积就等于物理内存总量。
        long free = 0;
        if (_physicalBytes > 0)
        {
            free = Math.Max(0, _physicalBytes - used);
            if (free > Math.Max(64L << 20, _physicalBytes / 100))
            {
                entries.Add(new Entry("__free__", "空闲内存", free, 0, 0, EntryKind.Free));
            }
        }

        entries.Sort((left, right) => right.Bytes.CompareTo(left.Bytes));

        var area = new RectangleF(Dp(12), Dp(34), Math.Max(1, Width - Dp(24)), Math.Max(1, Height - Dp(46)));
        double total = entries.Sum(e => (double)e.Bytes);
        _totalBytes = (long)total;

        double scale = area.Width * area.Height / total;
        var nodes = entries
            .Select((entry, index) => new Node(entry.Key, entry.Name, entry.Bytes, entry.Cpu, entry.ProcessCount, entry.Bytes * scale, index, entry.Kind))
            .ToList();

        var remaining = area;
        int cursor = 0;
        while (cursor < nodes.Count && remaining.Width > 0.5 && remaining.Height > 0.5)
        {
            var row = new List<Node>();
            double worst = double.MaxValue;
            while (cursor < nodes.Count)
            {
                row.Add(nodes[cursor]);
                double current = WorstRatio(row, remaining);
                if (row.Count > 1 && current > worst)
                {
                    row.RemoveAt(row.Count - 1);
                    break;
                }

                worst = current;
                cursor++;
            }

            if (row.Count == 0)
            {
                break;
            }

            LayoutRow(row, ref remaining);
        }

        float gap = Dp(2) / 2f;
        foreach (var node in nodes)
        {
            if (node.Rect.Width <= 0 || node.Rect.Height <= 0)
            {
                continue;
            }

            var rect = node.Rect;
            rect.Inflate(-gap, -gap);
            if (rect.Width < 2 || rect.Height < 2)
            {
                continue;
            }

            var fill = node.Kind switch
            {
                EntryKind.Unattributed => _theme.IsDark ? Color.FromArgb(62, 67, 76) : Color.FromArgb(206, 210, 216),
                EntryKind.Free => _theme.IsDark ? Color.FromArgb(40, 45, 54) : Color.FromArgb(238, 241, 245),
                _ => Palette(node.Index),
            };
            _blocks.Add(new Block(rect, node.Key, node.Name, node.Bytes, node.Cpu, node.ProcessCount, fill));
        }

        if (_hovered >= _blocks.Count)
        {
            _hovered = -1;
        }
    }

    private enum EntryKind
    {
        Application,
        Unattributed,
        Free,
    }

    private sealed record Entry(string Key, string Name, long Bytes, double Cpu, int ProcessCount, EntryKind Kind);

    private sealed class Node
    {
        public Node(string key, string name, long bytes, double cpu, int processCount, double area, int index, EntryKind kind)
        {
            Key = key;
            Name = name;
            Bytes = bytes;
            Cpu = cpu;
            ProcessCount = processCount;
            Area = area;
            Index = index;
            Kind = kind;
        }

        public string Key { get; }
        public string Name { get; }
        public long Bytes { get; }
        public double Cpu { get; }
        public int ProcessCount { get; }
        public double Area { get; }
        public int Index { get; }
        public EntryKind Kind { get; }
        public RectangleF Rect { get; set; }
    }

    private static double WorstRatio(List<Node> row, RectangleF remaining)
    {
        double sum = 0;
        double min = double.MaxValue;
        double max = 0;
        foreach (var node in row)
        {
            sum += node.Area;
            min = Math.Min(min, node.Area);
            max = Math.Max(max, node.Area);
        }

        if (sum <= 0 || min <= 0)
        {
            return double.MaxValue;
        }

        double side = Math.Min(remaining.Width, remaining.Height);
        if (side <= 0)
        {
            return double.MaxValue;
        }

        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static void LayoutRow(List<Node> row, ref RectangleF remaining)
    {
        double rowArea = row.Sum(n => n.Area);
        if (rowArea <= 0)
        {
            return;
        }

        if (remaining.Width >= remaining.Height)
        {
            // 竖着排一列，贴在剩余区域的左侧
            float columnWidth = (float)(rowArea / remaining.Height);
            float y = remaining.Y;
            foreach (var node in row)
            {
                float height = (float)(node.Area / columnWidth);
                node.Rect = new RectangleF(remaining.X, y, columnWidth, height);
                y += height;
            }

            remaining = new RectangleF(remaining.X + columnWidth, remaining.Y, Math.Max(0, remaining.Width - columnWidth), remaining.Height);
        }
        else
        {
            // 横着排一行，贴在剩余区域的顶部
            float rowHeight = (float)(rowArea / remaining.Width);
            float x = remaining.X;
            foreach (var node in row)
            {
                float width = (float)(node.Area / rowHeight);
                node.Rect = new RectangleF(x, remaining.Y, width, rowHeight);
                x += width;
            }

            remaining = new RectangleF(remaining.X, remaining.Y + rowHeight, remaining.Width, Math.Max(0, remaining.Height - rowHeight));
        }
    }

    /// <summary>方块配色：按索引在色相环上均匀取样，深浅主题各一套。</summary>
    private Color Palette(int index)
    {
        double hue = (index * 43 % 360) / 360.0;
        if (_theme.IsDark)
        {
            // 深色下用低饱和的莫兰迪色系：能区分相邻方块，又不会像糖果盘一样刺眼
            double lightness = 0.30 + (index % 4) * 0.035;
            return FromHsl(hue, 0.34, lightness);
        }

        // 浅色下同样收敛饱和度，靠明度差区分层次
        double lightLightness = 0.70 + (index % 4) * 0.028;
        return FromHsl(hue, 0.30, lightLightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double r = lightness;
        double g = lightness;
        double b = lightness;
        if (saturation > 0)
        {
            double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            r = HueToRgb(p, q, hue + 1.0 / 3);
            g = HueToRgb(p, q, hue);
            b = HueToRgb(p, q, hue - 1.0 / 3);
        }

        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + (q - p) * (2.0 / 3 - t) * 6;
        }

        return p;
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using (var surface = new SolidBrush(_theme.Surface))
        {
            graphics.FillRectangle(surface, ClientRectangle);
        }

        DrawHeader(graphics);

        if (_blocks.Count == 0)
        {
            string message = _apps.Length == 0 ? "正在等待第一帧采样数据…" : "当前筛选条件下没有可显示的内存占用";
            TextRenderer.DrawText(
                graphics,
                message,
                _theme.UiFont,
                new Rectangle(Dp(12), Dp(48), Math.Max(0, Width - Dp(24)), Math.Max(0, Height - Dp(60))),
                _theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _blocks.Count; i++)
        {
            DrawBlock(graphics, _blocks[i], i == _hovered);
        }
    }

    private static string TrimUnit(long bytes) => FormatUtil.Bytes(bytes).Replace(" GB", string.Empty, StringComparison.Ordinal)
        .Replace(" MB", string.Empty, StringComparison.Ordinal)
        .Replace(" KB", string.Empty, StringComparison.Ordinal)
        .Replace(" B", string.Empty, StringComparison.Ordinal);

    private void DrawHeader(Graphics graphics)
    {
        int titleWidth = TextRenderer.MeasureText(graphics, "内存分布", _theme.UiBoldFont, new Size(int.MaxValue, Dp(20)), TextFormatFlags.NoPadding).Width + Dp(4);
        var titleRect = new Rectangle(Dp(12), Dp(8), Math.Max(Dp(40), titleWidth), Dp(20));
        TextRenderer.DrawText(
            graphics,
            "内存分布",
            _theme.UiBoldFont,
            titleRect,
            _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (_totalBytes <= 0)
        {
            return;
        }

        long used = Math.Max(_usedBytes, _attributedBytes);
        long free = _physicalBytes > used ? _physicalBytes - used : 0;
        string caption = _attributedBytes > 0 && used > _attributedBytes
            ? "已归属 " + TrimUnit(_attributedBytes) + " / 在用 " + TrimUnit(used)
              + (free > 0 ? " / 空闲 " + FormatUtil.Bytes(free) : string.Empty)
            : "共 " + FormatUtil.Bytes(_totalBytes) + " · 面积 = 内存";

        var captionRect = new Rectangle(
            titleRect.Right + Dp(10),
            Dp(8),
            Math.Max(0, Width - Dp(12) - (titleRect.Right + Dp(10))),
            Dp(20));
        TextRenderer.DrawText(
            graphics,
            caption,
            _theme.SmallFont,
            captionRect,
            _theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void DrawBlock(Graphics graphics, Block block, bool hovered)
    {
        var rect = block.Bounds;
        using (var fill = new SolidBrush(block.Fill))
        {
            graphics.FillRectangle(fill, rect);
        }

        if (block.Key == "__free__" && rect.Width > Dp(8) && rect.Height > Dp(8))
        {
            var state = graphics.Save();
            graphics.SetClip(rect);
            using var dot = new SolidBrush(Theme.WithAlpha(_theme.TextMuted, 34));
            int step = Dp(14);
            for (int y = (int)rect.Top + step / 2; y < rect.Bottom; y += step)
            {
                for (int x = (int)rect.Left + step / 2; x < rect.Right; x += step)
                {
                    graphics.FillEllipse(dot, x, y, Math.Max(1, Dp(1)), Math.Max(1, Dp(1)));
                }
            }

            graphics.Restore(state);
        }

        if (block.Key == "__unattributed__" && rect.Width > Dp(8) && rect.Height > Dp(8))
        {
            var state = graphics.Save();
            graphics.SetClip(rect);
            using var hatch = new Pen(Color.FromArgb(26, _theme.TextPrimary), Math.Max(1f, Dp(1)));
            int step = Dp(12);
            for (int x = (int)rect.Left - (int)rect.Height; x < rect.Right; x += step)
            {
                graphics.DrawLine(hatch, x, rect.Bottom, x + (int)rect.Height, rect.Top);
            }

            graphics.Restore(state);
        }

        if (hovered)
        {
            using var pen = new Pen(_theme.Accent, Math.Max(1.5f, Dp(2)));
            graphics.DrawRectangle(pen, rect.X, rect.Y, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
        }

        // 只有方块够大时才写文字，避免小方块里全是碎字
        if (rect.Width < Dp(52) || rect.Height < Dp(26))
        {
            return;
        }

        var textColor = _theme.IsDark ? Color.FromArgb(238, 242, 248) : Color.FromArgb(28, 32, 38);
        int padding = Dp(6);
        var nameRect = new Rectangle(
            (int)rect.X + padding,
            (int)rect.Y + padding,
            Math.Max(0, (int)rect.Width - padding * 2),
            Dp(16));
        TextRenderer.DrawText(
            graphics,
            block.Name,
            _theme.UiBoldFont,
            nameRect,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (rect.Height < Dp(44))
        {
            return;
        }

        var sizeRect = new Rectangle(
            (int)rect.X + padding,
            (int)rect.Y + padding + Dp(16),
            Math.Max(0, (int)rect.Width - padding * 2),
            Dp(15));
        TextRenderer.DrawText(
            graphics,
            FormatUtil.Bytes(block.Bytes),
            _theme.SmallFont,
            sizeRect,
            _theme.IsDark ? Color.FromArgb(205, 214, 226) : Color.FromArgb(58, 64, 74),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = -1;
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Bounds.Contains(e.X, e.Y))
            {
                hit = i;
                break;
            }
        }

        if (hit == _hovered)
        {
            return;
        }

        _hovered = hit;
        if (hit >= 0)
        {
            var block = _blocks[hit];
            string text;
            if (block.Key == "__unattributed__")
            {
                text = "未归属内存 " + FormatUtil.Bytes(block.Bytes) + Environment.NewLine +
                       "这部分物理内存无法归属到某个进程：内核与驱动、文件缓存、" + Environment.NewLine +
                       "内存压缩、GPU 与共享页等。";
            }
            else if (block.Key == "__free__")
            {
                double share = _physicalBytes > 0 ? block.Bytes * 100.0 / _physicalBytes : 0;
                text = "空闲内存 " + FormatUtil.Bytes(block.Bytes) + Environment.NewLine +
                       "占物理内存 " + share.ToString("F1", System.Globalization.CultureInfo.CurrentCulture) + "%" + Environment.NewLine +
                       "（系统可用内存，随时可以被应用申请）";
            }
            else
            {
                text = block.Name + Environment.NewLine +
                       "内存 " + FormatUtil.Bytes(block.Bytes) + Environment.NewLine +
                       "CPU " + FormatUtil.Percent(block.CpuPercent) + " · " + block.ProcessCount + " 个进程";
            }
            _tooltip.SetToolTip(this, text);
        }
        else
        {
            _tooltip.SetToolTip(this, null);
        }

        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered >= 0)
        {
            _hovered = -1;
            _tooltip.SetToolTip(this, null);
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tooltip.Dispose();
        }

        base.Dispose(disposing);
    }
}