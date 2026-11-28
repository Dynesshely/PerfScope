using System.Collections.ObjectModel;
using System.Windows.Media;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.Core.Analysis;
using PerfScope.Core.Models;

namespace PerfScope.App.ViewModels;

/// <summary>分布视图的度量口径。</summary>
public enum TreemapMetric
{
    /// <summary>按内存（字节数）分配面积。</summary>
    Memory = 0,

    /// <summary>按 CPU 百分比分配面积。</summary>
    Cpu = 1,
}

/// <summary>
/// 分布视图（树图）ViewModel：把一帧里的应用数据映射成像素矩形。
/// 布局算法在 Core 的 <see cref="TreemapLayout"/> 里（纯几何、可单测），本类只负责喂数据与着色。
/// </summary>
public sealed class TreemapPanelViewModel : ObservableObject
{
    private readonly Dictionary<string, TreemapTileViewModel> _tiles = new(StringComparer.Ordinal);
    private TreemapMetric _metric = TreemapMetric.Memory;
    private double _panelWidth = 320;
    private double _panelHeight = 240;
    private AppSample[] _lastApps = Array.Empty<AppSample>();
    private TreemapTileViewModel? _hoveredTile;
    private bool _isDark = true;
    private bool _preferPrivateMemory;

    /// <summary>当前矩形（按面积降序）。</summary>
    public ObservableCollection<TreemapTileViewModel> Tiles { get; } = new();

    /// <summary>当前度量口径。</summary>
    public TreemapMetric Metric
    {
        get => _metric;
        set
        {
            if (SetProperty(ref _metric, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>当前悬停的矩形（null 表示没有）。</summary>
    public TreemapTileViewModel? HoveredTile
    {
        get => _hoveredTile;
        set
        {
            if (SetProperty(ref _hoveredTile, value))
            {
                OnPropertyChanged(nameof(HasHoveredTile));
            }
        }
    }

    /// <summary>是否有悬停矩形（悬停卡片可见性用）。</summary>
    public bool HasHoveredTile => HoveredTile is not null;

    /// <summary>是否为空（没有可展示的应用）。</summary>
    public bool IsEmpty => Tiles.Count == 0;

    /// <summary>设置内存口径：true 用私有字节，false 用工作集（必须与列表保持一致，否则两处数字对不上）。</summary>
    public void SetMemoryMode(bool preferPrivateMemory)
    {
        if (_preferPrivateMemory == preferPrivateMemory)
        {
            return;
        }

        _preferPrivateMemory = preferPrivateMemory;
        Rebuild();
    }

    /// <summary>更新主题（决定色块明度）。</summary>
    public void SetTheme(bool dark)
    {
        if (_isDark == dark)
        {
            return;
        }

        _isDark = dark;
        Rebuild();
    }

    /// <summary>更新面板尺寸（像素），会重新计算布局。</summary>
    public void SetSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(width - _panelWidth) < 0.5 && Math.Abs(height - _panelHeight) < 0.5)
        {
            return;
        }

        _panelWidth = width;
        _panelHeight = height;
        Rebuild();
    }

    /// <summary>用一帧数据刷新（不改变尺寸时也只更新数值与颜色）。</summary>
    public void Update(AppSample[] apps)
    {
        _lastApps = apps ?? Array.Empty<AppSample>();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_panelWidth <= 0 || _panelHeight <= 0)
        {
            return;
        }

        var inputs = new List<TreemapInput>(_lastApps.Length);
        var byKey = new Dictionary<string, AppSample>(StringComparer.Ordinal);
        foreach (var app in _lastApps)
        {
            double weight = Metric == TreemapMetric.Memory
                ? (_preferPrivateMemory ? app.PrivateBytes : app.WorkingSetBytes)
                : app.CpuPercent;
            if (weight > 0)
            {
                inputs.Add(new TreemapInput(app.Key, weight));
                byKey[app.Key] = app;
            }
        }

        // 面积上限 24 块：再多的碎块在视觉上没有意义，只会拖慢渲染
        if (inputs.Count > 24)
        {
            inputs.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
            inputs.RemoveRange(24, inputs.Count - 24);
        }

        var rects = TreemapLayout.Compute(inputs, minWeightRatio: 0.002);

        // 清理已经消失的应用
        var alive = new HashSet<string>(rects.Select(r => r.Key), StringComparer.Ordinal);
        for (int i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!alive.Contains(Tiles[i].Key))
            {
                _tiles.Remove(Tiles[i].Key);
                Tiles.RemoveAt(i);
            }
        }

        // 按布局顺序重建可见集合（顺序变化时视图会重排）
        var ordered = new List<TreemapTileViewModel>(rects.Count);
        foreach (var rect in rects)
        {
            if (!_tiles.TryGetValue(rect.Key, out var tile))
            {
                tile = new TreemapTileViewModel(rect.Key, byKey[rect.Key].DisplayName);
                _tiles[rect.Key] = tile;
            }

            var app = byKey[rect.Key];
            string valueText = Metric == TreemapMetric.Memory
                ? FormatUtil.Bytes(_preferPrivateMemory ? app.PrivateBytes : app.WorkingSetBytes)
                : FormatUtil.Percent(app.CpuPercent);
            tile.Update(app.DisplayName, valueText, CreateBrush(rect.Key));
            tile.X = rect.X * _panelWidth;
            tile.Y = rect.Y * _panelHeight;
            tile.Width = Math.Max(0, rect.Width * _panelWidth);
            tile.Height = Math.Max(0, rect.Height * _panelHeight);
            ordered.Add(tile);
        }

        SynchronizeTiles(ordered);
        OnPropertyChanged(nameof(IsEmpty));
        if (HoveredTile is not null && !alive.Contains(HoveredTile.Key))
        {
            HoveredTile = null;
        }
    }

    private void SynchronizeTiles(List<TreemapTileViewModel> ordered)
    {
        if (Tiles.Count == ordered.Count)
        {
            bool same = true;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(Tiles[i], ordered[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        Tiles.Clear();
        foreach (var tile in ordered)
        {
            Tiles.Add(tile);
        }
    }

    /// <summary>按应用键稳定派生颜色：同一个应用在内存 / CPU 视图下颜色一致。</summary>
    private Brush CreateBrush(string key)
    {
        int hash = 17;
        foreach (char ch in key)
        {
            hash = unchecked(hash * 31 + ch);
        }

        // 12 个等距色相，避免相邻块撞色；同一个应用在两种度量下颜色恒定
        int index = Math.Abs(hash) % 12;
        double hue = (index * 30.0 + (index >= 6 ? 15 : 0)) % 360;

        // 深色主题用中等明度 + 较高饱和度（白字可读且不发灰）；浅色主题压暗一档
        double lightness = _isDark ? 0.48 : 0.42;
        var color = FromHsl(hue, _isDark ? 0.58 : 0.55, lightness);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = lightness - (c / 2);

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
