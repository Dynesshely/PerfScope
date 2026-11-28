using System.Collections.ObjectModel;
using System.Windows.Media;
using PerfScope.App.Infrastructure;
using PerfScope.App.Mvvm;
using PerfScope.App.Services;
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

    // 每个应用的颜色是稳定的：缓存起来，避免每秒为每个块重新 new 一个 SolidColorBrush
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.Ordinal);
    private readonly IconService _icons;
    private TreemapMetric _metric = TreemapMetric.Memory;
    private double _panelWidth = 320;
    private double _panelHeight = 240;
    private AppSample[] _lastApps = Array.Empty<AppSample>();
    private TreemapTileViewModel? _hoveredTile;
    private bool _isDark = true;
    private bool _preferPrivateMemory;

    /// <summary>创建分布视图 ViewModel（图标由 <see cref="IconService"/> 提供，与列表共用同一份缓存）。</summary>
    public TreemapPanelViewModel(IconService icons)
    {
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
    }

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
        _brushes.Clear();
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

        // 增量同步：已存在的块只改坐标与数值，新块直接追加。
        // 矩形之间不重叠，所以 Canvas 的子元素顺序不影响观感，不需要每秒 Clear + Add
        // （那会让 ItemsControl 每次重建整棵可视化树）。
        foreach (var rect in rects)
        {
            if (!_tiles.TryGetValue(rect.Key, out var tile))
            {
                tile = new TreemapTileViewModel(rect.Key, byKey[rect.Key].DisplayName);
                _tiles[rect.Key] = tile;
                Tiles.Add(tile);
            }

            var app = byKey[rect.Key];
            string valueText = Metric == TreemapMetric.Memory
                ? FormatUtil.Bytes(_preferPrivateMemory ? app.PrivateBytes : app.WorkingSetBytes)
                : FormatUtil.Percent(app.CpuPercent);
            string displayName = Localization.Loc.T(app.DisplayName);
            var icon = IsNeutral(rect.Key) ? null : _icons.GetIcon(app.IconSourcePath, app.DisplayName);
            tile.Update(displayName, valueText, GetBrush(rect.Key), icon);
            tile.X = rect.X * _panelWidth;
            tile.Y = rect.Y * _panelHeight;
            tile.Width = Math.Max(0, rect.Width * _panelWidth);
            tile.Height = Math.Max(0, rect.Height * _panelHeight);
        }

        // 清掉已消失的应用对应的画笔缓存，避免长期运行后越积越多
        if (_brushes.Count > (_tiles.Count * 2) + 32)
        {
            var aliveKeys = new HashSet<string>(_tiles.Keys, StringComparer.Ordinal);
            var stale = _brushes.Keys.Where(k => !aliveKeys.Contains(k)).ToArray();
            foreach (var key in stale)
            {
                _brushes.Remove(key);
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
        if (HoveredTile is not null && !alive.Contains(HoveredTile.Key))
        {
            HoveredTile = null;
        }
    }

    /// <summary>未归属 CPU 行用中性灰（与旧版一致，不参与调色板取样）。</summary>
    private static bool IsNeutral(string key) => string.Equals(key, Core.Sampling.SnapshotComposer.UnattributedCpuKey, StringComparison.Ordinal);

    /// <summary>
    /// 取（或创建）方块画笔。配色沿用旧 WinForms 版：色相按 43° 步进取样、饱和度 0.30~0.34、
    /// 深色主题明度 0.30 起、浅色主题明度 0.70 起 —— 目的就是"暗调、不刺眼"，
    /// 同时保证块上的浅色/深色文字都够对比。
    /// </summary>
    private Brush GetBrush(string key)
    {
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Color color;
        if (IsNeutral(key))
        {
            color = _isDark ? Color.FromRgb(0x3E, 0x43, 0x4C) : Color.FromRgb(0xCE, 0xD2, 0xD8);
        }
        else
        {
            int hash = 17;
            foreach (char ch in key)
            {
                hash = unchecked(hash * 31 + ch);
            }

            int index = hash & 0x7FFFFFFF;   // 避免 int.MinValue 取绝对值溢出
            double hue = (index * 43 % 360) / 360.0;
            color = _isDark
                ? FromHsl(hue, 0.34, 0.30 + (index % 4) * 0.035)
                : FromHsl(hue, 0.30, 0.70 + (index % 4) * 0.028);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>HSL → RGB（hue 取 0~1，与旧版 Palette 的实现一致）。</summary>
    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double r = lightness;
        double g = lightness;
        double b = lightness;
        if (saturation > 0)
        {
            double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            r = HueToRgb(p, q, hue + (1.0 / 3));
            g = HueToRgb(p, q, hue);
            b = HueToRgb(p, q, hue - (1.0 / 3));
        }

        return Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
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
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + ((q - p) * ((2.0 / 3) - t) * 6);
        }

        return p;
    }
}
