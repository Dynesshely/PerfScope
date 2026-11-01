using System.Drawing.Drawing2D;
using PerfScope.App.Infrastructure;

namespace PerfScope.App.Controls;

/// <summary>
/// 顶部整机指标区：两张并排的圆角卡片，每张是「标题 + 大数字 + 明细 + 迷你趋势线」。
/// 趋势线固定 0~100 刻度并画基线，曲线高度有绝对含义，也不会退化成一大块填充色。
/// 趋势点由调用方按帧推入，本控件只负责绘制，不做任何额外计算。
/// </summary>
internal sealed class SystemHeaderControl : Control
{
    private readonly TrendRing _cpuRing;
    private readonly TrendRing _memoryRing;
    private readonly PointF[] _buffer = new PointF[132];
    private Theme _theme;

    private double _cpuPercent;
    private double _memoryPercent;
    private long _totalBytes;
    private long _availableBytes;
    private int _processCount;
    private double _selfCpuPercent;
    private long _selfWorkingSetBytes;

    private Pen? _cpuPen;
    private Pen? _memoryPen;
    private SolidBrush? _surfaceBrush;
    private SolidBrush? _cardBrush;
    private Pen? _borderPen;
    private Pen? _baselinePen;

    /// <summary>创建顶部指标区。</summary>
    public SystemHeaderControl(Theme theme)
    {
        _theme = theme;
        _cpuRing = new TrendRing(120);
        _memoryRing = new TrendRing(120);
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
    }

    private void DisposeResources()
    {
        _cpuPen?.Dispose();
        _memoryPen?.Dispose();
        _surfaceBrush?.Dispose();
        _cardBrush?.Dispose();
        _borderPen?.Dispose();
        _baselinePen?.Dispose();
        _cpuPen = null;
        _memoryPen = null;
        _surfaceBrush = null;
        _cardBrush = null;
        _borderPen = null;
        _baselinePen = null;
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

        DrawCard(graphics, left, "整机 CPU", FormatUtil.Percent(_cpuPercent), BuildCpuDetail(), _cpuRing, _cpuPen!, _theme.CpuLine);
        DrawCard(graphics, right, "物理内存", FormatUtil.Percent(_memoryPercent), BuildMemoryDetail(), _memoryRing, _memoryPen!, _theme.MemoryLine);

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

        int textLeft = bounds.Left + inset;
        int textWidth = Math.Max(Dp(70), sparkRect.Left - Dp(12) - textLeft);

        // 按字体实际高度排版：不同 DPI/字体下大数字的高度差异很大，写死矩形很容易被裁切
        const TextFormatFlags singleLine = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var measureSize = new Size(textWidth, int.MaxValue);
        Size captionSize = TextRenderer.MeasureText(graphics, caption, _theme.SmallFont, measureSize, singleLine);
        Size valueSize = TextRenderer.MeasureText(graphics, value, _theme.BigNumberFont, measureSize, singleLine);
        Size detailSize = TextRenderer.MeasureText(graphics, detail, _theme.SmallFont, measureSize, singleLine);

        // 明确的上下留白：标题与数字之间、数字与明细之间都留 Dp(10)，
        // 数字矩形本身再加 Dp(8) 余量，保证字形上下都不贴边。
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
            graphics.DrawLine(_baselinePen, sparkRect.Left, sparkRect.Bottom, sparkRect.Right, sparkRect.Bottom);
        }

        if (ring.Count >= 2)
        {
            var points = ring.Chronological();
            SparklineRenderer.Draw(graphics, sparkRect, points.AsSpan(0, ring.Count), _buffer, linePen, null, 100d);
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