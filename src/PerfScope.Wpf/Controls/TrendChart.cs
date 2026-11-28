using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PerfScope.App.ViewModels;

namespace PerfScope.App.Controls;

/// <summary>
/// 迷你趋势图（火花线）。数据来自 <see cref="TrendSeriesViewModel"/>，绘制在本控件的 OnRender 里：
/// 折线 + 渐变填充 + 末点高亮；鼠标悬停时叠加竖线与数值（对应旧版的「悬停看坐标」）。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    /// <summary>数据源。</summary>
    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(TrendSeriesViewModel), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    /// <summary>折线颜色。</summary>
    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(TrendChart),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>是否始终显示悬停坐标轴（截图 / 演示用）。</summary>
    public static readonly DependencyProperty ForceHoverProperty =
        DependencyProperty.Register(nameof(ForceHover), typeof(bool), typeof(TrendChart),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private TrendSeriesViewModel? _series;
    private int _hoverIndex = -1;

    /// <summary>创建趋势图。</summary>
    public TrendChart()
    {
        IsHitTestVisible = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>数据源。</summary>
    public TrendSeriesViewModel? Series
    {
        get => (TrendSeriesViewModel?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>折线颜色。</summary>
    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>强制显示悬停坐标轴（<c>--hover-demo</c>）。</summary>
    public bool ForceHover
    {
        get => (bool)GetValue(ForceHoverProperty);
        set => SetValue(ForceHoverProperty, value);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var series = Series;
        if (series is null || series.Points.Count < 2)
        {
            return;
        }

        var point = e.GetPosition(this);
        double ratio = Math.Clamp(point.X / Math.Max(1, ActualWidth), 0, 1);
        int index = (int)Math.Round(ratio * (series.Points.Count - 1));
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var series = Series;
        double width = ActualWidth;
        double height = ActualHeight;
        if (series is null || series.Points.Count < 2 || width < 8 || height < 8)
        {
            return;
        }

        var brush = LineBrush;
        var points = series.Points;
        double max = Math.Max(1, series.MaxValue);
        double stepX = width / (points.Count - 1);
        double padding = 2;

        Point Map(int index, double value)
        {
            double x = index * stepX;
            double y = height - padding - Math.Clamp(value / max, 0, 1) * (height - (padding * 2));
            return new Point(x, y);
        }

        // 折线
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(Map(0, points[0].Value), false, false);
            for (int i = 1; i < points.Count; i++)
            {
                context.LineTo(Map(i, points[i].Value), true, false);
            }
        }

        geometry.Freeze();

        // 渐变填充
        if (brush is SolidColorBrush solid)
        {
            var fill = new LinearGradientBrush(
                Color.FromArgb(64, solid.Color.R, solid.Color.G, solid.Color.B),
                Color.FromArgb(6, solid.Color.R, solid.Color.G, solid.Color.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();

            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(0, height), true, true);
                for (int i = 0; i < points.Count; i++)
                {
                    context.LineTo(Map(i, points[i].Value), true, false);
                }

                context.LineTo(new Point(width, height), true, false);
            }

            area.Freeze();
            drawingContext.DrawGeometry(fill, null, area);
        }

        // 注意：不要 Freeze 含 DynamicResource 画笔的 Pen —— 画笔本身可能未冻结，
        // 冻结父对象会抛 InvalidOperationException（"无法冻结此 Freezable"）。
        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(null, pen, geometry);

        // 末点高亮
        Point last = Map(points.Count - 1, points[^1].Value);
        drawingContext.DrawEllipse(brush, null, last, 2.4, 2.4);

        // 悬停坐标轴
        int hover = _hoverIndex;
        if (ForceHover && hover < 0)
        {
            hover = points.Count - 1;
        }

        if (hover < 0 || hover >= points.Count)
        {
            return;
        }

        Point hovered = Map(hover, points[hover].Value);
        var guidePen = new Pen(brush, 0.8) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
        drawingContext.DrawLine(guidePen, new Point(hovered.X, 0), new Point(hovered.X, height));
        drawingContext.DrawEllipse(brush, null, hovered, 3, 3);

        var text = new FormattedText(
            points[hover].Value.ToString("F1", CultureInfo.CurrentCulture) + "%",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10.5,
            brush,
            1.0);

        double textX = Math.Min(Math.Max(2, hovered.X + 5), width - text.Width - 2);
        double textY = Math.Max(0, hovered.Y - text.Height - 4);
        drawingContext.DrawText(text, new Point(textX, textY));
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrendChart chart)
        {
            return;
        }

        if (chart._series is not null)
        {
            chart._series.Points.CollectionChanged -= chart.OnPointsChanged;
            chart._series.PropertyChanged -= chart.OnSeriesPropertyChanged;
        }

        chart._series = e.NewValue as TrendSeriesViewModel;
        if (chart._series is not null)
        {
            chart._series.Points.CollectionChanged += chart.OnPointsChanged;
            chart._series.PropertyChanged += chart.OnSeriesPropertyChanged;
        }

        chart.InvalidateVisual();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
