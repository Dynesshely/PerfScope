using System.Collections.ObjectModel;
using System.Windows;
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
    private long _physicalBytes;
    private long _availableBytes;
    private Brush? _freeTexture;
    private Brush? _unattributedTexture;

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
        _freeTexture = null;
        _unattributedTexture = null;
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
    /// <param name="apps">本帧的应用聚合。</param>
    /// <param name="physicalBytes">物理内存总量（用于铺出「空闲内存」块）。</param>
    /// <param name="availableBytes">当前可用物理内存。</param>
    public void Update(AppSample[] apps, long physicalBytes = 0, long availableBytes = 0)
    {
        _lastApps = apps ?? Array.Empty<AppSample>();
        _physicalBytes = physicalBytes;
        _availableBytes = availableBytes;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_panelWidth <= 0 || _panelHeight <= 0)
        {
            return;
        }

        // 输入项：应用块 + 旧版那两块中性块（未归属 / 空闲）。
        // 内存视图的面积口径 = 物理内存总量，CPU 视图 = 100%。
        var appInputs = new List<TreemapInput>(_lastApps.Length);
        var byKey = new Dictionary<string, AppSample>(StringComparer.Ordinal);
        var neutral = new Dictionary<string, NeutralTile>(StringComparer.Ordinal);

        double appTotal = 0;
        foreach (var app in _lastApps)
        {
            double weight = Metric == TreemapMetric.Memory
                ? (_preferPrivateMemory ? app.PrivateBytes : app.WorkingSetBytes)
                : app.CpuPercent;
            if (Metric == TreemapMetric.Cpu && weight <= 0.01)
            {
                continue;
            }

            if (weight > 0)
            {
                appInputs.Add(new TreemapInput(app.Key, weight));
                byKey[app.Key] = app;
                appTotal += weight;
            }
        }

        if (Metric == TreemapMetric.Memory)
        {
            AppendMemoryNeutrals(appInputs, neutral, appTotal);
        }
        else
        {
            AppendCpuNeutral(neutral, appTotal);
        }

        // 面积上限 24 块：再多的碎块在视觉上没有意义，只会拖慢渲染。
        // 中性块不参与截断，最后补回去，保证「空闲 / 未归属」一定看得见。
        if (appInputs.Count > 24)
        {
            appInputs.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
            appInputs.RemoveRange(24, appInputs.Count - 24);
        }

        var inputs = new List<TreemapInput>(appInputs.Count + neutral.Count);
        inputs.AddRange(appInputs);
        foreach (var pair in neutral)
        {
            inputs.Add(new TreemapInput(pair.Key, pair.Value.Weight));
        }

        var rects = TreemapLayout.Compute(inputs, minWeightRatio: 0.0005);

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

        // 增量同步：已存在的块只改坐标与数值，新块直接追加（不每秒重建整棵可视化树）
        foreach (var rect in rects)
        {
            if (!_tiles.TryGetValue(rect.Key, out var tile))
            {
                string name = neutral.TryGetValue(rect.Key, out var neutralInfo)
                    ? neutralInfo.Name
                    : Localization.Loc.T(byKey[rect.Key].DisplayName);
                tile = new TreemapTileViewModel(rect.Key, name);
                _tiles[rect.Key] = tile;
                Tiles.Add(tile);
            }

            if (neutral.TryGetValue(rect.Key, out var info))
            {
                Brush fill = info.Kind == TreemapTileKind.Free ? GetFreeBrush() : GetUnattributedBrush();
                Brush? texture = info.Kind == TreemapTileKind.Free ? GetFreeTexture() : GetUnattributedTexture();
                tile.Update(info.Name, info.ValueText, fill, null, info.Kind, texture);
            }
            else
            {
                var app = byKey[rect.Key];
                string valueText = Metric == TreemapMetric.Memory
                    ? FormatUtil.Bytes(_preferPrivateMemory ? app.PrivateBytes : app.WorkingSetBytes)
                    : FormatUtil.Percent(app.CpuPercent);
                string displayName = Localization.Loc.T(app.DisplayName);
                var icon = _icons.GetIcon(app.IconSourcePath, app.DisplayName);
                tile.Update(displayName, valueText, GetBrush(rect.Key), icon, TreemapTileKind.Application);
            }

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

    /// <summary>
    /// 内存视图的中性块：未归属（内核 / 缓存 / 共享）+ 空闲内存，规则与旧 WinForms 版一致。
    /// 工作集之和可能超过「在用内存」（共享页重复计数），这时把应用块等比缩小，保持彼此比例不变。
    /// </summary>
    private void AppendMemoryNeutrals(List<TreemapInput> appInputs, Dictionary<string, NeutralTile> neutral, double appTotal)
    {
        if (_physicalBytes <= 0)
        {
            return;
        }

        long used = Math.Max(0, _physicalBytes - _availableBytes);

        if (appTotal > used && used > 0)
        {
            double factor = used / appTotal;
            for (int i = 0; i < appInputs.Count; i++)
            {
                appInputs[i] = appInputs[i] with { Weight = appInputs[i].Weight * factor };
            }
        }
        else if (used > appTotal)
        {
            long remainder = used - (long)appTotal;
            if (remainder > Math.Max(256L << 20, used / 50))
            {
                neutral[NeutralUnattributedKey] = new NeutralTile(
                    Localization.Loc.T("未归属（内核 / 缓存 / 共享）"),
                    FormatUtil.Bytes(remainder),
                    TreemapTileKind.Unattributed,
                    remainder);
            }
        }

        long free = Math.Max(0, _physicalBytes - used);
        if (free > Math.Max(64L << 20, _physicalBytes / 100))
        {
            neutral[NeutralFreeKey] = new NeutralTile(
                Localization.Loc.T("空闲内存"),
                FormatUtil.Bytes(free),
                TreemapTileKind.Free,
                free);
        }
    }

    /// <summary>CPU 视图的中性块：空闲 CPU，让整张图的面积口径等于 100%。</summary>
    private static void AppendCpuNeutral(Dictionary<string, NeutralTile> neutral, double appTotal)
    {
        double idle = 100.0 - appTotal;
        if (idle > 0.5)
        {
            neutral[NeutralIdleCpuKey] = new NeutralTile(
                Localization.Loc.T("空闲 CPU"),
                FormatUtil.Percent(idle),
                TreemapTileKind.Free,
                idle);
        }
    }

    /// <summary>中性块的信息（名称 / 数值文本 / 种类 / 权重）。</summary>
    private readonly record struct NeutralTile(string Name, string ValueText, TreemapTileKind Kind, double Weight);

    private const string NeutralFreeKey = "__free__";
    private const string NeutralUnattributedKey = "__unattributed__";
    private const string NeutralIdleCpuKey = "__idle_cpu__";

    /// <summary>空闲块：深色 #282D36，浅色 #EEF1F5（旧版取值）。</summary>
    private Brush GetFreeBrush()
        => GetOrCreateNeutralBrush("free", _isDark ? Color.FromRgb(0x28, 0x2D, 0x36) : Color.FromRgb(0xEE, 0xF1, 0xF5));

    /// <summary>未归属块：深色 #3E434C，浅色 #CED2D8（旧版取值）。</summary>
    private Brush GetUnattributedBrush()
        => GetOrCreateNeutralBrush("unattributed", _isDark ? Color.FromRgb(0x3E, 0x43, 0x4C) : Color.FromRgb(0xCE, 0xD2, 0xD8));

    private Brush GetOrCreateNeutralBrush(string name, Color color)
    {
        string key = name + (_isDark ? ":dark" : ":light");
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>空闲块的点阵纹理（旧版：每 14px 一个半透明小点）。</summary>
    private Brush GetFreeTexture()
    {
        if (_freeTexture is not null)
        {
            return _freeTexture;
        }

        var dot = new GeometryDrawing(
            new SolidColorBrush(_isDark ? Color.FromArgb(34, 0x6B, 0x76, 0x86) : Color.FromArgb(34, 0x8A, 0x92, 0x9C)),
            null,
            new EllipseGeometry(new Point(1, 1), 1, 1));
        var brush = new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 14, 14),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 14, 14),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        _freeTexture = brush;
        return brush;
    }

    /// <summary>未归属块的斜纹纹理（旧版：每 12px 一道半透明斜线）。</summary>
    private Brush GetUnattributedTexture()
    {
        if (_unattributedTexture is not null)
        {
            return _unattributedTexture;
        }

        var pen = new Pen(new SolidColorBrush(_isDark ? Color.FromArgb(26, 0xEE, 0xF2, 0xF8) : Color.FromArgb(26, 0x1C, 0x20, 0x26)), 1);
        var line = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 12), new Point(12, 0)));
        var brush = new DrawingBrush(line)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 12, 12),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        _unattributedTexture = brush;
        return brush;
    }

    /// <summary>应用块配色：沿用旧 WinForms 版（色相 43° 步进、低饱和、暗调）。</summary>
    private Brush GetBrush(string key)
    {
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        int hash = 17;
        foreach (char ch in key)
        {
            hash = unchecked(hash * 31 + ch);
        }

        int index = hash & 0x7FFFFFFF;   // 避免 int.MinValue 取绝对值溢出
        double hue = (index * 43 % 360) / 360.0;
        var color = _isDark
            ? FromHsl(hue, 0.34, 0.30 + (index % 4) * 0.035)
            : FromHsl(hue, 0.30, 0.70 + (index % 4) * 0.028);

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
