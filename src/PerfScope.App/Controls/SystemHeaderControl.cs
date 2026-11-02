using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;

namespace PerfScope.App.Controls;

/// <summary>
/// 顶部整机指标区：两张并排的圆角卡片，每张是「标题 + 大数字 + 明细 + 迷你趋势线」。
/// 趋势线固定 0~100 刻度；鼠标悬停时显示坐标轴（Y 轴 0/50/100、X 轴时间）与十字线，
/// 并用自绘的小卡片给出对应的 X（时间）/ Y（百分比）数值。
/// </summary>
internal sealed class SystemHeaderControl : Control
{
    private const int Capacity = 120;

    private readonly TrendRing _cpuRing;
    private readonly TrendRing _memoryRing;
    private readonly List<DateTime> _cpuStamps = new(Capacity);
    private readonly List<DateTime> _memoryStamps = new(Capacity);
    private readonly PointF[] _buffer = new PointF[Capacity + 4];
    private readonly Rectangle[] _cardRects = new Rectangle[2];
    private readonly Rectangle[] _plotRects = new Rectangle[2];
    private Theme _theme;

    private double _cpuPercent;
    private double _memoryPercent;
    private long _totalBytes;
    private long _availableBytes;
    private int _processCount;
    private double _selfCpuPercent;
    private long _selfWorkingSetBytes;

    private int _hoverCard = -1;
    private int _hoverIndex = -1;
    private Point _hoverPoint;

    private Pen? _cpuPen;
    private Pen? _memoryPen;
    private SolidBrush? _surfaceBrush;
    private SolidBrush? _cardBrush;
    private Pen? _borderPen;
    private Pen? _baselinePen;
    private Pen? _gridPen;
    private Pen? _crossPen;

    /// <summary>创建顶部指标区。</summary>
    public SystemHeaderControl(Theme theme)
    {
        _theme = theme;
        _cpuRing = new TrendRing(Capacity);
        _memoryRing = new TrendRing(Capacity);
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = theme.Surface;
        RebuildResources();
    }

    /// <summary>推入一帧数据（只在时间戳变化时调用）。</summary>
    public void Push(double cpuPercent, double memoryPercent, long totalBytes, long availableBytes, int processCount, double selfCpuPercent, long selfWorkingSetBytes)
    {
        _cpuPercent = cpuPercent;
        _memoryPercent = memoryPercent;
        _totalBytes = totalBytes;
        _availableBytes = availableBytes;
        _processCount = processCount;
        _selfCpuPercent = selfCpuPercent;
        _selfWorkingSetBytes = selfWorkingSetBytes;
        _cpuRing.Push(cpuPercent);
        _memoryRing.Push(memoryPercent);
        var now = DateTime.Now;
        AppendStamp(_cpuStamps, now);
        AppendStamp(_memoryStamps, now);
        Invalidate();
    }

    /// <summary>切换主题（先换引用再重建资源，避免绘制中用到已释放对象）。</summary>
    public void SetTheme(Theme theme)
    {
        if (ReferenceEquals(theme, _theme))
        {
            return;
        }

        _theme = theme;
        BackColor = theme.Surface;
        RebuildResources();
        Invalidate();
    }

    private static void AppendStamp(List<DateTime> stamps, DateTime now)
    {
        stamps.Add(now);
        int extra = stamps.Count - Capacity;
        if (extra > 0)
        {
            stamps.RemoveRange(0, extra);
        }
    }

    private int Dp(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void RebuildResources()
    {
        DisposeResources();
        _cpuPen = new Pen(_theme.CpuLine, Math.Max(1.2f, Dp(2) * 0.9f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        _memoryPen = new Pen(_theme.MemoryLine, Math.Max(1.2f, Dp(2) * 0.9f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        _surfaceBrush = new SolidBrush(_theme.Surface);
        _cardBrush = new SolidBrush(_theme.SurfaceAlt);
        _borderPen = new Pen(_theme.Border);
        _baselinePen = new Pen(_theme.GridLine);
        _gridPen = new Pen(Theme.WithAlpha(_theme.TextMuted, 70), 1f);
        _crossPen = new Pen(Theme.WithAlpha(_theme.TextSecondary, 160), 1f) { DashStyle = DashStyle.Dash };
    }

    private void DisposeResources()
    {
        _cpuPen?.Dispose();
        _memoryPen?.Dispose();
        _surfaceBrush?.Dispose();
        _cardBrush?.Dispose();
        _borderPen?.Dispose();
        _baselinePen?.Dispose();
        _gridPen?.Dispose();
        _crossPen?.Dispose();
        _cpuPen = null;
        _memoryPen = null;
        _surfaceBrush = null;
        _cardBrush = null;
        _borderPen = null;
        _baselinePen = null;
        _gridPen = null;
        _crossPen = null;
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_surfaceBrush is not null)
        {
            graphics.FillRectangle(_surfaceBrush, ClientRectangle);
        }

        int padding = Dp(12);
        int gap = Dp(12);
        int availableWidth = Math.Max(0, Width - padding * 2);
        int cardHeight = Height - padding * 2;
        if (availableWidth <= 0 || cardHeight <= Dp(24))
        {
            return;
        }

        int cardWidth = Math.Max(Dp(160), (availableWidth - gap) / 2);
        var left = new Rectangle(padding, padding, cardWidth, cardHeight);
        int rightWidth = Math.Max(Dp(160), availableWidth - cardWidth - gap);
        var right = new Rectangle(left.Right + gap, padding, rightWidth, cardHeight);
        _cardRects[0] = left;
        _cardRects[1] = right;

        DrawCard(graphics, left, 0, "整机 CPU", FormatUtil.Percent(_cpuPercent), BuildCpuDetail(), _cpuRing, _cpuPen!, _theme.CpuLine);
        DrawCard(graphics, right, 1, "物理内存", FormatUtil.Percent(_memoryPercent), BuildMemoryDetail(), _memoryRing, _memoryPen!, _theme.MemoryLine);

        if (_hoverCard < 0 && UiDebug.HoverDemo && _cpuRing.Count >= 2)
        {
            _hoverCard = 0;
            _hoverIndex = _cpuRing.Count / 2;
            _hoverPoint = new Point(_cardRects[0].Left + _cardRects[0].Width / 2, _cardRects[0].Top + _cardRects[0].Height / 2);
        }

        if (_hoverCard >= 0)
        {
            DrawHoverOverlay(graphics, _hoverCard);
        }

        if (_borderPen is not null && Height > 1)
        {
            graphics.DrawLine(_borderPen, 0, Height - 1, Width, Height - 1);
        }
    }

    private string BuildCpuDetail()
    {
        string self = "自身 " + FormatUtil.Percent(_selfCpuPercent) + " / " + FormatUtil.Bytes(_selfWorkingSetBytes);
        return "进程 " + FormatUtil.Count(_processCount) + " 个 · " + self;
    }

    private string BuildMemoryDetail()
    {
        if (_totalBytes <= 0)
        {
            return "内存总量未知";
        }

        long used = Math.Max(0, _totalBytes - _availableBytes);
        return FormatUtil.Bytes(used) + " / " + FormatUtil.Bytes(_totalBytes);
    }

    /// <summary>画一张指标卡：左侧色条 + 标题 + 大数字 + 明细，右侧固定宽度的迷你趋势线。</summary>
    private void DrawCard(
        Graphics graphics,
        Rectangle bounds,
        int cardIndex,
        string caption,
        string value,
        string detail,
        TrendRing ring,
        Pen linePen,
        Color accent)
    {
        if (bounds.Width <= Dp(80) || bounds.Height <= Dp(24))
        {
            return;
        }

        using (var path = RoundedRect(bounds, Dp(10)))
        {
            if (_cardBrush is not null)
            {
                graphics.FillPath(_cardBrush, path);
            }

            if (_borderPen is not null)
            {
                graphics.DrawPath(_borderPen, path);
            }
        }

        int inset = Dp(14);

        // 左侧 3px 主题色条，区分 CPU / 内存两块
        using (var accentPen = new Pen(accent, Math.Max(2f, Dp(3))))
        {
            graphics.DrawLine(accentPen, bounds.Left + Dp(2), bounds.Top + Dp(14), bounds.Left + Dp(2), bounds.Bottom - Dp(14));
        }

        // 迷你趋势线固定在右侧，并有宽度上限：窗口再宽也不会被拉成一大片色块
        int sparkWidth = Math.Min(Dp(190), Math.Max(Dp(72), (int)(bounds.Width * 0.34f)));
        int sparkHeight = Math.Max(Dp(18), bounds.Height - Dp(34));
        var sparkRect = new Rectangle(
            bounds.Right - inset - sparkWidth,
            bounds.Top + (bounds.Height - sparkHeight) / 2,
            sparkWidth,
            sparkHeight);

        // 预留给坐标轴：左侧刻度、底部时间标签（平时不画，悬停时才显示，但空间一直保留，避免布局跳动）。
        // 宽度/高度按实测文字尺寸算，避免 150% DPI 下 "100" 被裁成 "00"。
        var axisLabelSize = TextRenderer.MeasureText(graphics, "100", _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int axisLeft = axisLabelSize.Width + Dp(12);
        int axisBottom = axisLabelSize.Height + Dp(6);
        var plot = new Rectangle(
            sparkRect.X + axisLeft,
            sparkRect.Y,
            Math.Max(Dp(24), sparkRect.Width - axisLeft),
            Math.Max(Dp(16), sparkRect.Height - axisBottom));
        _plotRects[cardIndex] = plot;

        int textLeft = bounds.Left + inset;
        int textWidth = Math.Max(Dp(70), sparkRect.Left - Dp(12) - textLeft);

        // 按字体实际高度排版：不同 DPI/字体下大数字的高度差异很大，写死矩形很容易被裁切
        const TextFormatFlags singleLine = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var measureSize = new Size(textWidth, int.MaxValue);
        Size captionSize = TextRenderer.MeasureText(graphics, caption, _theme.SmallFont, measureSize, singleLine);
        Size valueSize = TextRenderer.MeasureText(graphics, value, _theme.BigNumberFont, measureSize, singleLine);
        Size detailSize = TextRenderer.MeasureText(graphics, detail, _theme.SmallFont, measureSize, singleLine);

        // 明确的上下留白：标题与数字之间、数字与明细之间都留 Dp(10)。
        int topPad = Dp(12);
        int bottomPad = Dp(12);
        int gap = Dp(10);
        int captionHeight = Math.Max(Dp(16), captionSize.Height);
        int detailHeight = Math.Max(Dp(16), detailSize.Height);
        int valueHeight = Math.Max(Dp(28), valueSize.Height + Dp(8));

        // 卡片太矮时依次压缩间距与数字高度，绝不会让文字被裁切
        int total = topPad + captionHeight + gap + valueHeight + gap + detailHeight + bottomPad;
        if (total > bounds.Height)
        {
            int overflow = total - bounds.Height;
            int gapShrink = Math.Min(overflow, gap * 2);
            gap -= gapShrink / 2;
            overflow -= gapShrink;
            valueHeight = Math.Max(Dp(20), valueHeight - overflow);
        }

        int captionTop = bounds.Top + topPad;
        TextRenderer.DrawText(
            graphics,
            caption,
            _theme.SmallFont,
            new Rectangle(textLeft, captionTop, textWidth, captionHeight),
            _theme.TextSecondary,
            singleLine | TextFormatFlags.VerticalCenter);

        int valueTop = captionTop + captionHeight + gap;
        TextRenderer.DrawText(
            graphics,
            value,
            _theme.BigNumberFont,
            new Rectangle(textLeft - Dp(2), valueTop, textWidth + Dp(2), valueHeight),
            _theme.TextPrimary,
            singleLine | TextFormatFlags.VerticalCenter);

        TextRenderer.DrawText(
            graphics,
            detail,
            _theme.SmallFont,
            new Rectangle(textLeft, bounds.Bottom - bottomPad - detailHeight, Math.Max(Dp(70), bounds.Width - inset * 2), detailHeight),
            _theme.TextMuted,
            singleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (_baselinePen is not null)
        {
            graphics.DrawLine(_baselinePen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }

        if (ring.Count >= 2)
        {
            var points = ring.Chronological();
            SparklineRenderer.Draw(graphics, plot, points.AsSpan(0, ring.Count), _buffer, linePen, null, 100d);
        }
    }

    /// <summary>悬停时的坐标轴 + 十字线 + 数值卡片（自绘，不用系统 ToolTip）。</summary>
    private void DrawHoverOverlay(Graphics graphics, int cardIndex)
    {
        var ring = cardIndex == 0 ? _cpuRing : _memoryRing;
        var stamps = cardIndex == 0 ? _cpuStamps : _memoryStamps;
        int count = ring.Count;
        var plot = _plotRects[cardIndex];
        if (count < 2 || plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        double[] values = ring.Chronological();
        if (values.Length < count)
        {
            return;
        }

        // Y 轴：0 / 50 / 100 三条网格线与刻度（刻度宽度按实测文字宽度预留，右对齐）
        var axisLabelSize = TextRenderer.MeasureText(graphics, "100", _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int labelWidth = axisLabelSize.Width + Dp(6);
        for (int i = 0; i <= 2; i++)
        {
            int y = plot.Bottom - (int)Math.Round(plot.Height * i / 2.0);
            if (_gridPen is not null)
            {
                graphics.DrawLine(_gridPen, plot.Left, y, plot.Right, y);
            }

            TextRenderer.DrawText(
                graphics,
                (i * 50).ToString(System.Globalization.CultureInfo.CurrentCulture),
                _theme.SmallFont,
                new Rectangle(plot.Left - Dp(4) - labelWidth, y - axisLabelSize.Height / 2, labelWidth, axisLabelSize.Height),
                _theme.TextMuted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // X 轴：时间跨度
        TimeSpan span = stamps.Count >= 2 ? stamps[^1] - stamps[0] : TimeSpan.Zero;
        string spanText = span.TotalSeconds >= 90
            ? "-" + (span.TotalSeconds / 60).ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + " 分"
            : "-" + span.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + " 秒";
        int xLabelTop = plot.Bottom + Dp(3);
        int xLabelWidth = Math.Max(Dp(56), TextRenderer.MeasureText(graphics, spanText, _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Dp(10));
        TextRenderer.DrawText(
            graphics,
            spanText,
            _theme.SmallFont,
            new Rectangle(plot.Left - Dp(2), xLabelTop, xLabelWidth, axisLabelSize.Height),
            _theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        int nowWidth = TextRenderer.MeasureText(graphics, "现在", _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Dp(4);
        TextRenderer.DrawText(
            graphics,
            "现在",
            _theme.SmallFont,
            new Rectangle(plot.Right - nowWidth, xLabelTop, nowWidth, axisLabelSize.Height),
            _theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        // 十字线 + 数据点
        int index = Math.Clamp(_hoverIndex, 0, count - 1);
        float x = plot.Left + plot.Width * (float)index / (count - 1);
        double value = values[index];
        float y2 = plot.Bottom - plot.Height * (float)Math.Clamp(value / 100.0, 0, 1);

        if (_crossPen is not null)
        {
            graphics.DrawLine(_crossPen, x, plot.Top, x, plot.Bottom);
        }

        using (var marker = new SolidBrush(cardIndex == 0 ? _theme.CpuLine : _theme.MemoryLine))
        {
            int r = Math.Max(2, Dp(3));
            graphics.FillEllipse(marker, x - r, y2 - r, r * 2, r * 2);
        }

        // 数值卡片
        string title = cardIndex == 0 ? "整机 CPU" : "物理内存";
        string valueText = FormatUtil.Percent(value);
        DateTime stamp = stamps.Count == count ? stamps[index] : DateTime.Now;
        double secondsAgo = stamps.Count >= 2 ? (stamps[^1] - stamp).TotalSeconds : 0;
        string agoText = secondsAgo <= 0.5
            ? "现在"
            : secondsAgo < 90
                ? secondsAgo.ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + " 秒前"
                : (secondsAgo / 60).ToString("F0", System.Globalization.CultureInfo.CurrentCulture) + " 分钟前";
        string timeText = stamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture) + " · " + agoText;

        DrawTooltip(graphics, title, valueText, timeText, _hoverPoint);
    }

    private void DrawTooltip(Graphics graphics, string title, string value, string time, Point anchor)
    {
        var pad = Dp(10);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        var titleSize = TextRenderer.MeasureText(graphics, title, _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), flags);
        var valueSize = TextRenderer.MeasureText(graphics, value, _theme.UiBoldFont, new Size(int.MaxValue, int.MaxValue), flags);
        var timeSize = TextRenderer.MeasureText(graphics, time, _theme.SmallFont, new Size(int.MaxValue, int.MaxValue), flags);

        int width = Math.Max(Math.Max(titleSize.Width, valueSize.Width), timeSize.Width) + pad * 2 + Dp(6);
        int height = pad * 2 + titleSize.Height + Dp(2) + valueSize.Height + Dp(1) + timeSize.Height;

        int x = anchor.X + Dp(14);
        int y = anchor.Y + Dp(16);
        if (x + width > Width - Dp(4))
        {
            x = anchor.X - width - Dp(12);
        }

        if (y + height > Height - Dp(4))
        {
            y = anchor.Y - height - Dp(12);
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
        using (var fill = new SolidBrush(_theme.IsDark ? Color.FromArgb(44, 49, 59) : Color.White))
        using (var border = new Pen(_theme.Border))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        int textLeft = card.X + pad;
        int textWidth = card.Width - pad * 2;
        int textTop = card.Y + pad;
        TextRenderer.DrawText(graphics, title, _theme.SmallFont, new Rectangle(textLeft, textTop, textWidth, titleSize.Height), _theme.TextMuted, flags | TextFormatFlags.Left);
        textTop += titleSize.Height + Dp(2);
        TextRenderer.DrawText(graphics, value, _theme.UiBoldFont, new Rectangle(textLeft, textTop, textWidth, valueSize.Height), _theme.TextPrimary, flags | TextFormatFlags.Left);
        textTop += valueSize.Height + Dp(1);
        TextRenderer.DrawText(graphics, time, _theme.SmallFont, new Rectangle(textLeft, textTop, textWidth, timeSize.Height), _theme.TextSecondary, flags | TextFormatFlags.Left);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int card = -1;
        for (int i = 0; i < _cardRects.Length; i++)
        {
            if (_cardRects[i].Contains(e.Location))
            {
                card = i;
                break;
            }
        }

        int index = -1;
        if (card >= 0)
        {
            var plot = _plotRects[card];
            int count = card == 0 ? _cpuRing.Count : _memoryRing.Count;
            if (count >= 2 && plot.Width > 0 && e.X >= plot.Left - Dp(4) && e.X <= plot.Right + Dp(4))
            {
                double ratio = Math.Clamp((e.X - plot.Left) / (double)plot.Width, 0, 1);
                index = (int)Math.Round(ratio * (count - 1));
            }
        }

        if (card == _hoverCard && index == _hoverIndex && e.Location == _hoverPoint)
        {
            return;
        }

        _hoverCard = card;
        _hoverIndex = index;
        _hoverPoint = e.Location;
        Cursor = index >= 0 ? Cursors.Cross : Cursors.Default;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCard >= 0)
        {
            _hoverCard = -1;
            _hoverIndex = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private GraphicsPath RoundedRect(Rectangle bounds, int radius)
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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }

        base.Dispose(disposing);
    }
}