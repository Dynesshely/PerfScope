using PerfScope.App.Localization;
using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Models;

namespace PerfScope.App.Controls;

/// <summary>树图度量的口径。</summary>
internal enum TreemapMetric
{
    /// <summary>面积 = 应用占用的内存。</summary>
    Memory = 0,

    /// <summary>面积 = 应用的 CPU 占用百分比。</summary>
    Cpu = 1,
}

/// <summary>
/// 分布树图（Treemap / 矩形树图，SpaceSniffer 用的就是这一类可视化）：
/// 每个应用一个矩形，**面积正比于该应用的度量值**（内存或 CPU），方块越大占用越多。
/// 布局采用 squarified treemap（平方化）算法；大方块里会画应用图标，方便一眼认出是谁。
/// </summary>
internal sealed class AppTreemapControl : Control
{
    private sealed record Block(
        RectangleF Bounds,
        string Key,
        string Name,
        string ValueText,
        double Weight,
        double CpuPercent,
        int ProcessCount,
        Color Fill,
        string? IconPath);

    private readonly List<Block> _blocks = new();
    private readonly IconProvider _icons;
    private readonly TreemapMetric _metric;
    private Theme _theme;
    private AppSample[] _apps = Array.Empty<AppSample>();
    private long _physicalBytes;
    private long _usedBytes;
    private long _attributedBytes;
    private double _totalWeight;
    private int _hovered = -1;
    private Point _hoverPoint;

    /// <summary>创建树图控件。</summary>
    public AppTreemapControl(Theme theme, TreemapMetric metric, IconProvider icons)
    {
        _theme = theme;
        _metric = metric;
        _icons = icons;
        AccessibleName = Loc.T("分布视图");
        AccessibleRole = AccessibleRole.Chart;
        AccessibleDescription = Loc.T("矩形树图：面积代表占用大小，悬停可查看应用详情。");
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = theme.Surface;
        ForeColor = theme.TextPrimary;
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
        ForeColor = theme.TextPrimary;
        Rebuild();
        Invalidate();
    }

    /// <summary>推入一帧的应用数据。</summary>
    public void SetApps(IReadOnlyList<AppSample> apps)
    {
        var source = apps as AppSample[] ?? apps.ToArray();
        _apps = Array.ConvertAll(source, app => app with { DisplayName = Loc.T(app.DisplayName) });
        Rebuild();
        Invalidate();
    }

    /// <summary>物理内存总量（内存口径用）。</summary>
    public void SetPhysicalMemory(long totalBytes)
    {
        _physicalBytes = totalBytes;
    }

    /// <summary>系统「在用」物理内存（内存口径用）。</summary>
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
        _totalWeight = 0;
        if (_apps.Length == 0 || Width <= Dp(40) || Height <= Dp(50))
        {
            return;
        }

        var entries = _metric == TreemapMetric.Memory ? BuildMemoryEntries() : BuildCpuEntries();
        if (entries.Count == 0)
        {
            return;
        }

        entries.Sort((left, right) => right.Weight.CompareTo(left.Weight));

        var area = new RectangleF(Dp(12), Dp(30), Math.Max(1, Width - Dp(24)), Math.Max(1, Height - Dp(42)));
        double total = entries.Sum(e => e.Weight);
        _totalWeight = total;
        if (total <= 0)
        {
            return;
        }

        double scale = area.Width * area.Height / total;
        var nodes = entries
            .Select((entry, index) => new Node(entry, entry.Weight * scale, index))
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

            var entry = node.Entry;
            var fill = entry.Kind switch
            {
                EntryKind.Unattributed => _theme.IsDark ? Color.FromArgb(62, 67, 76) : Color.FromArgb(206, 210, 216),
                EntryKind.Free => _theme.IsDark ? Color.FromArgb(40, 45, 54) : Color.FromArgb(238, 241, 245),
                _ => Palette(node.Index),
            };
            _blocks.Add(new Block(rect, entry.Key, entry.Name, entry.ValueText, entry.Weight, entry.Cpu, entry.ProcessCount, fill, entry.IconPath));
        }

        if (_hovered >= _blocks.Count)
        {
            _hovered = -1;
        }
    }

    /// <summary>内存口径：应用工作集 + 未归属 + 空闲。</summary>
    private List<Entry> BuildMemoryEntries()
    {
        var entries = new List<Entry>();
        foreach (var app in _apps)
        {
            if (app.WorkingSetBytes > 0)
            {
                entries.Add(new Entry(
                    app.Key,
                    app.DisplayName,
                    app.WorkingSetBytes,
                    FormatUtil.Bytes(app.WorkingSetBytes),
                    app.CpuPercent,
                    app.ProcessCount,
                    app.IconSourcePath ?? app.RootPath,
                    EntryKind.Application));
            }
        }

        if (entries.Count == 0)
        {
            return entries;
        }

        long attributed = entries.Sum(e => (long)e.Weight);
        _attributedBytes = attributed;

        long used = Math.Max(0, _usedBytes);

        // 各进程的「工作集」会重复计算共享页，几百个进程时总和可能超过物理内存。
        // 这时把应用块按 used/attributed 等比缩小：彼此的比例不变，面积口径回到「在用内存」。
        if (attributed > used && attributed > 0 && used > 0)
        {
            double factor = (double)used / attributed;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Kind == EntryKind.Application)
                {
                    entries[i] = entries[i] with { Weight = entries[i].Weight * factor };
                }
            }
        }
        else
        {
            // 系统「在用」里总有一部分无法归属到任何进程（内核、驱动、文件缓存、压缩内存、共享页等）。
            long remainder = used - attributed;
            if (remainder > Math.Max(256L << 20, used / 50))
            {
                entries.Add(new Entry("__unattributed__", Loc.T("未归属（内核 / 缓存 / 共享）"), remainder, FormatUtil.Bytes(remainder), 0, 0, null, EntryKind.Unattributed));
            }
        }

        // 空闲物理内存也按比例画出来，让整张图的面积等于物理内存总量。
        if (_physicalBytes > 0)
        {
            long free = Math.Max(0, _physicalBytes - used);
            if (free > Math.Max(64L << 20, _physicalBytes / 100))
            {
                entries.Add(new Entry("__free__", Loc.T("空闲内存"), free, FormatUtil.Bytes(free), 0, 0, null, EntryKind.Free));
            }
        }

        return entries;
    }

    /// <summary>CPU 口径：应用 CPU 百分比 + 空闲 CPU（整机口径下才补）。</summary>
    private List<Entry> BuildCpuEntries()
    {
        var entries = new List<Entry>();
        double total = 0;
        foreach (var app in _apps)
        {
            if (app.CpuPercent <= 0.01)
            {
                continue;
            }

            total += app.CpuPercent;
            entries.Add(new Entry(
                app.Key,
                app.DisplayName,
                app.CpuPercent,
                FormatUtil.Percent(app.CpuPercent),
                app.CpuPercent,
                app.ProcessCount,
                app.IconSourcePath ?? app.RootPath,
                EntryKind.Application));
        }

        // 补一块「空闲」：让整张图的面积口径等于 100%，
        // 空闲很大时应用块会变小，但Loc.T("还有多少余量")一眼可见（可用工具栏切换回内存分布看细节）。
        double idle = 100.0 - total;
        if (idle > 0.5)
        {
            entries.Add(new Entry("__idle_cpu__", Loc.T("空闲 CPU"), idle, FormatUtil.Percent(idle), 0, 0, null, EntryKind.Free));
        }

        return entries;
    }

    private enum EntryKind
    {
        Application,
        Unattributed,
        Free,
    }

    private sealed record Entry(
        string Key,
        string Name,
        double Weight,
        string ValueText,
        double Cpu,
        int ProcessCount,
        string? IconPath,
        EntryKind Kind);

    private sealed class Node
    {
        public Node(Entry entry, double area, int index)
        {
            Entry = entry;
            Area = area;
            Index = index;
        }

        public Entry Entry { get; }
        public double Area { get; }
        public int Index { get; }
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
            double lightness = 0.30 + (index % 4) * 0.035;
            return FromHsl(hue, 0.34, lightness);
        }

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
            string message = _apps.Length == 0
                ? Loc.T("正在等待第一帧采样数据…")
                : _metric == TreemapMetric.Memory ? Loc.T("当前筛选条件下没有可显示的内存占用") : Loc.T("当前没有可显示的 CPU 占用");
            TextRenderer.DrawText(
                graphics,
                message,
                _theme.UiFont,
                new Rectangle(Dp(12), Dp(40), Math.Max(0, Width - Dp(24)), Math.Max(0, Height - Dp(52))),
                _theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _blocks.Count; i++)
        {
            DrawBlock(graphics, _blocks[i], i == _hovered);
        }

        if (_hovered < 0 && UiDebug.HoverDemo)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Key is not ("__free__" or "__idle_cpu__" or "__unattributed__"))
                {
                    _hovered = i;
                    _hoverPoint = new Point((int)_blocks[i].Bounds.Left + (int)_blocks[i].Bounds.Width / 2, (int)_blocks[i].Bounds.Top + (int)_blocks[i].Bounds.Height / 2);
                    break;
                }
            }
        }

        if (_hovered >= 0 && _hovered < _blocks.Count)
        {
            DrawHoverCard(graphics, _blocks[_hovered]);
        }
    }

    private static string TrimUnit(long bytes) => FormatUtil.Bytes(bytes)
        .Replace(" GB", string.Empty, StringComparison.Ordinal)
        .Replace(" MB", string.Empty, StringComparison.Ordinal)
        .Replace(" KB", string.Empty, StringComparison.Ordinal)
        .Replace(" B", string.Empty, StringComparison.Ordinal);

    private void DrawHeader(Graphics graphics)
    {
        string caption;
        if (_metric == TreemapMetric.Memory)
        {
            long used = Math.Max(0, _usedBytes);
            long free = _physicalBytes > used ? _physicalBytes - used : 0;
            caption = _physicalBytes > 0
                ? Loc.T("在用 {0} / 空闲 {1} / 共 {2} · 面积 = 内存", TrimUnit(used), TrimUnit(free), FormatUtil.Bytes(_physicalBytes))
                : Loc.T("已归属 {0} · 面积 = 内存", FormatUtil.Bytes(_attributedBytes));
        }
        else
        {
            double appTotal = _blocks.Where(b => b.Key != "__idle_cpu__").Sum(b => b.Weight);
            double idlePart = _blocks.Where(b => b.Key == "__idle_cpu__").Sum(b => b.Weight);
            caption = idlePart > 0
                ? Loc.T("应用合计 {0} / 空闲 {1}", FormatUtil.Percent(appTotal), FormatUtil.Percent(idlePart))
                : Loc.T("应用合计 {0}", FormatUtil.Percent(appTotal));
        }

        if (string.IsNullOrEmpty(caption))
        {
            return;
        }

        TextRenderer.DrawText(
            graphics,
            caption,
            _theme.SmallFont,
            new Rectangle(Dp(12), Dp(6), Math.Max(0, Width - Dp(24)), Dp(18)),
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

        bool neutral = block.Key is "__free__" or "__idle_cpu__" or "__unattributed__";
        DrawTexture(graphics, block, rect);

        if (hovered)
        {
            using var pen = new Pen(_theme.Accent, Math.Max(1.5f, Dp(2)));
            graphics.DrawRectangle(pen, rect.X, rect.Y, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
        }

        int pad = Dp(7);
        int innerWidth = Math.Max(0, (int)rect.Width - pad * 2);
        int innerHeight = Math.Max(0, (int)rect.Height - pad * 2);
        if (innerWidth < Dp(6) || innerHeight < Dp(6))
        {
            return;
        }

        // 图标：按块大小分档（同一档位内复用缓存，不会因为每次绘制重新解码）
        int iconSize = rect.Width >= Dp(150) && rect.Height >= Dp(90)
            ? Dp(26)
            : rect.Width >= Dp(90)
                ? Dp(20)
                : Dp(16);
        Image? icon = neutral || block.IconPath is not { Length: > 0 }
            ? null
            : _icons.GetIcon(block.IconPath, block.Name, iconSize);

        // 文字全部按实测高度排版，上下各留 pad，彻底避免底部被裁
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var nameSize = TextRenderer.MeasureText(graphics, block.Name, _theme.UiBoldFont, new Size(innerWidth, int.MaxValue), flags);
        var valueSize = TextRenderer.MeasureText(graphics, block.ValueText, _theme.SmallFont, new Size(innerWidth, int.MaxValue), flags);
        int gap = Dp(2);
        int textHeight = nameSize.Height + gap + valueSize.Height;

        bool textWidthOk = innerWidth >= Dp(30);
        bool textHeightOk = innerHeight >= textHeight;
        bool textFits = textWidthOk && textHeightOk;
        bool iconFits = icon is not null && innerWidth >= iconSize && innerHeight >= iconSize;

        // 图标与文字同时出现时，图标放在文字下方、靠右下角；只有剩余高度真的够才画，避免压字
        bool iconBesideText = textFits && icon is not null
            && innerHeight >= textHeight + gap + iconSize
            && innerWidth >= iconSize;

        if (textFits && (icon is null || iconBesideText))
        {
            DrawBlockText(graphics, block, rect, pad, innerWidth, nameSize, valueSize, gap);
            if (iconBesideText)
            {
                DrawBlockIcon(graphics, icon!, rect, iconSize);
            }

            return;
        }

        // 文字放不下（或加了图标就会遮挡）时：整块只显示图标
        if (icon is not null)
        {
            // 块够大就按档位画，太小的话把图标缩到块内居中，保证一定能看见
            int fit = Math.Min(Math.Min(iconSize, innerWidth), innerHeight);
            if (fit < Dp(10))
            {
                fit = Math.Max(Dp(6), Math.Min(innerWidth, innerHeight));
            }

            var box = new Rectangle(
                (int)rect.X + (int)(rect.Width - fit) / 2,
                (int)rect.Y + (int)(rect.Height - fit) / 2,
                fit,
                fit);
            graphics.DrawImage(icon, box);
            return;
        }

        // 没有图标、文字又放不下：能放名称就只放名称，否则留空
        if (textWidthOk && innerHeight >= nameSize.Height)
        {
            TextRenderer.DrawText(
                graphics,
                block.Name,
                _theme.UiBoldFont,
                new Rectangle((int)rect.X + pad, (int)rect.Y + pad, innerWidth, nameSize.Height),
                _theme.IsDark ? Color.FromArgb(238, 242, 248) : Color.FromArgb(28, 32, 38),
                flags | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        }
    }

    /// <summary>名称 + 数值两行文字，按实测高度排版并带上下留白。</summary>
    private void DrawBlockText(
        Graphics graphics,
        Block block,
        RectangleF rect,
        int pad,
        int innerWidth,
        Size nameSize,
        Size valueSize,
        int gap)
    {
        var textColor = _theme.IsDark ? Color.FromArgb(238, 242, 248) : Color.FromArgb(28, 32, 38);
        var valueColor = _theme.IsDark ? Color.FromArgb(205, 214, 226) : Color.FromArgb(58, 64, 74);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;

        int top = (int)rect.Y + pad;
        TextRenderer.DrawText(
            graphics,
            block.Name,
            _theme.UiBoldFont,
            new Rectangle((int)rect.X + pad, top, innerWidth, nameSize.Height),
            textColor,
            flags);

        TextRenderer.DrawText(
            graphics,
            block.ValueText,
            _theme.SmallFont,
            new Rectangle((int)rect.X + pad, top + nameSize.Height + gap, innerWidth, valueSize.Height),
            valueColor,
            flags);
    }

    private void DrawBlockIcon(Graphics graphics, Image icon, RectangleF rect, int iconSize)
    {
        int pad = Dp(7);
        var box = new Rectangle(
            (int)rect.Right - pad - iconSize,
            (int)rect.Bottom - pad - iconSize,
            iconSize,
            iconSize);
        graphics.DrawImage(icon, box);
    }

    /// <summary>空闲 / 未归属块的特殊纹理，用来和普通应用块区分。</summary>
    private void DrawTexture(Graphics graphics, Block block, RectangleF rect)
    {
        if (rect.Width <= Dp(8) || rect.Height <= Dp(8))
        {
            return;
        }

        if (block.Key is "__free__" or "__idle_cpu__")
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
            return;
        }

        if (block.Key == "__unattributed__")
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

        _hoverPoint = e.Location;
        if (hit != _hovered)
        {
            _hovered = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }

        // 悬停卡片跟手且内容实时刷新，所以每次移动都重绘
        Invalidate();
    }

    /// <summary>自绘悬浮卡片：跟随鼠标、内容每帧实时刷新（数据变了卡片就跟着变）。</summary>
    private void DrawHoverCard(Graphics graphics, Block block)
    {
        var (title, lines) = Describe(block);

        int pad = Dp(10);
        int lineGap = Dp(3);
        const TextFormatFlags measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var titleSize = TextRenderer.MeasureText(graphics, title, _theme.UiBoldFont, new Size(int.MaxValue, int.MaxValue), measureFlags);
        var lineSizes = lines
            .Select(line => TextRenderer.MeasureText(graphics, line, _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), measureFlags))
            .ToArray();

        int contentWidth = Math.Max(titleSize.Width, lineSizes.Length == 0 ? 0 : lineSizes.Max(s => s.Width));
        // 多留几个像素：不同 DPI 下 TextRenderer 的测量与绘制会有一两个像素的差异，
        // 不留余量会导致明明放得下的文字被 EndEllipsis 截断。
        int width = Math.Min(contentWidth + pad * 2 + Dp(8), Math.Max(Dp(140), Width - Dp(10)));
        int height = pad * 2 + titleSize.Height;
        if (lineSizes.Length > 0)
        {
            height += Dp(4);
            foreach (var size in lineSizes)
            {
                height += size.Height + lineGap;
            }

            height -= lineGap;
        }

        int x = _hoverPoint.X + Dp(14);
        int y = _hoverPoint.Y + Dp(18);
        if (x + width > Width - Dp(4))
        {
            x = _hoverPoint.X - width - Dp(12);
        }

        if (y + height > Height - Dp(4))
        {
            y = _hoverPoint.Y - height - Dp(12);
        }

        x = Math.Max(Dp(4), x);
        y = Math.Max(Dp(4), y);
        var card = new Rectangle(x, y, width, height);

        using (var shadow = new SolidBrush(Color.FromArgb(_theme.IsDark ? 90 : 45, 0, 0, 0)))
        using (var shadowPath = RoundedRect(new Rectangle(card.X + Dp(1), card.Y + Dp(2), card.Width, card.Height), Dp(8)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        using (var path = RoundedRect(card, Dp(8)))
        using (var fill = new SolidBrush(_theme.IsDark ? Color.FromArgb(44, 49, 59) : Color.FromArgb(255, 255, 255)))
        using (var border = new Pen(_theme.Border))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        int textLeft = card.X + pad;
        int textWidth = card.Width - pad * 2;
        int textTop = card.Y + pad;
        TextRenderer.DrawText(
            graphics,
            title,
            _theme.UiBoldFont,
            new Rectangle(textLeft, textTop, textWidth, titleSize.Height),
            _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        textTop += titleSize.Height + Dp(4);
        for (int i = 0; i < lines.Count; i++)
        {
            TextRenderer.DrawText(
                graphics,
                lines[i],
                _theme.SmallFont,
                new Rectangle(textLeft, textTop, textWidth, lineSizes[i].Height),
                _theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            textTop += lineSizes[i].Height + lineGap;
        }
    }

    private (string Title, List<string> Lines) Describe(Block block)
    {
        var lines = new List<string>(3);

        if (block.Key == "__unattributed__")
        {
            lines.Add(block.ValueText + Loc.T(" · 占在用内存 ") + Share(block.Weight, _usedBytes > 0 ? _usedBytes : block.Weight));
            lines.Add(Loc.T("内核与驱动、文件缓存、压缩内存、共享页等"));
            lines.Add(Loc.T("这部分无法归属到任何进程"));
            return (Loc.T("未归属内存"), lines);
        }

        if (block.Key == "__free__")
        {
            lines.Add(block.ValueText + Loc.T(" · 占物理内存 ") + Share(block.Weight, _physicalBytes));
            lines.Add(Loc.T("系统可用内存，随时可以被应用申请"));
            return (Loc.T("空闲内存"), lines);
        }

        if (block.Key == "__idle_cpu__")
        {
            lines.Add(block.ValueText + Loc.T(" · 整机口径下未被任何进程占用"));
            return (Loc.T("空闲 CPU"), lines);
        }

        if (_metric == TreemapMetric.Memory)
        {
            long used = Math.Max(_usedBytes, _attributedBytes);
            lines.Add(Loc.T("内存 ") + block.ValueText + (used > 0 ? Loc.T(" · 占在用 ") + Share(block.Weight, used) : string.Empty));
            lines.Add("CPU " + FormatUtil.Percent(block.CpuPercent) + " · " + block.ProcessCount + Loc.T(" 个进程"));
            return (block.Name, lines);
        }

        lines.Add("CPU " + block.ValueText + " · " + block.ProcessCount + Loc.T(" 个进程"));
        return (block.Name, lines);
    }

    private static string Share(double part, double whole)
        => whole <= 0 ? "—" : (part * 100.0 / whole).ToString("F1", System.Globalization.CultureInfo.CurrentCulture) + "%";

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered >= 0)
        {
            _hovered = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
