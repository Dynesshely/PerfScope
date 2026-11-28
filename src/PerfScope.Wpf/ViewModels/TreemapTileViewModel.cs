using System.Windows.Media;
using PerfScope.App.Mvvm;

namespace PerfScope.App.ViewModels;

/// <summary>方块种类：应用块参与调色板取色，空闲 / 未归属块用中性色 + 纹理区分。</summary>
public enum TreemapTileKind
{
    /// <summary>普通应用块。</summary>
    Application = 0,

    /// <summary>无法归属到任何进程的内核 / 缓存 / 共享内存。</summary>
    Unattributed = 1,

    /// <summary>空闲内存（或空闲 CPU）。</summary>
    Free = 2,
}

/// <summary>分布视图里的一块矩形。几何用像素坐标，由 <see cref="TreemapPanelViewModel"/> 在尺寸变化时重算。</summary>
public sealed class TreemapTileViewModel : ObservableObject
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private string _valueText = string.Empty;
    private Brush _fill = Brushes.Gray;
    private ImageSource? _icon;
    private TreemapTileKind _kind = TreemapTileKind.Application;
    private Brush? _texture;

    /// <summary>创建矩形（只在首次出现该应用时调用）。</summary>
    public TreemapTileViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    /// <summary>应用归并键。</summary>
    public string Key { get; }

    /// <summary>应用展示名。</summary>
    public string DisplayName { get; private set; }

    /// <summary>度量文本（内存容量或 CPU 百分比）。</summary>
    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    /// <summary>填充色（按应用键稳定着色，切换度量时不跳色）。</summary>
    public Brush Fill
    {
        get => _fill;
        private set => SetProperty(ref _fill, value);
    }

    /// <summary>左边界（像素）。</summary>
    public double X
    {
        get => _x;
        set
        {
            if (SetProperty(ref _x, value))
            {
                OnPropertyChanged(nameof(Margin));
            }
        }
    }

    /// <summary>上边界（像素）。</summary>
    public double Y
    {
        get => _y;
        set
        {
            if (SetProperty(ref _y, value))
            {
                OnPropertyChanged(nameof(Margin));
            }
        }
    }

    /// <summary>宽度（像素）。</summary>
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    /// <summary>高度（像素）。</summary>
    public double Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value))
            {
                OnPropertyChanged(nameof(ShowLabel));
                OnPropertyChanged(nameof(ShowValue));
                OnPropertyChanged(nameof(IconSize));
                OnPropertyChanged(nameof(IconHorizontalAlignment));
                OnPropertyChanged(nameof(IconVerticalAlignment));
                OnPropertyChanged(nameof(IconMargin));
            }
        }
    }

    /// <summary>是否显示应用名（太小的块只留图标，避免文字糊成一团）。</summary>
    public bool ShowLabel => Width >= 58 && Height >= 22;

    /// <summary>是否显示数值（更小的块只显示名字）。</summary>
    public bool ShowValue => Width >= 76 && Height >= 38;

    /// <summary>有标题就贴右下角（避免压字）；没有标题才把图标居中。</summary>
    private bool IconAtCorner => ShowLabel;

    /// <summary>空间足够时用完整尺寸的角标图标。</summary>
    private bool IconBesideText => ShowLabel && Width >= 72 && Height >= 52;

    /// <summary>图标是否显示：只要有图标就显示，与块大小无关。</summary>
    public bool ShowIcon => Icon is not null;

    /// <summary>
    /// 图标边长：有标题时贴右下角（宽裕 20px、紧凑 13px）；没有标题时居中，按块缩小到放得下。
    /// </summary>
    public double IconSize
    {
        get
        {
            if (IconAtCorner)
            {
                return IconBesideText ? 20 : 13;
            }

            double fit = Math.Min(Width, Height) - 6;
            return Math.Clamp(fit, 4, 24);
        }
    }

    /// <summary>图标水平对齐（有标题时靠右，否则居中）。</summary>
    public System.Windows.HorizontalAlignment IconHorizontalAlignment
        => IconAtCorner ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center;

    /// <summary>图标垂直对齐（有标题时靠下，否则居中）。</summary>
    public System.Windows.VerticalAlignment IconVerticalAlignment
        => IconAtCorner ? System.Windows.VerticalAlignment.Bottom : System.Windows.VerticalAlignment.Center;

    /// <summary>图标外边距（贴右下角时留边，居中时为 0）。</summary>
    public System.Windows.Thickness IconMargin
        => IconAtCorner
            ? new System.Windows.Thickness(0, 0, IconBesideText ? 6 : 3, IconBesideText ? 5 : 3)
            : new System.Windows.Thickness(0);

    /// <summary>图标是否可见（只要有图标就显示，小到像素级也不隐藏）。</summary>
    public System.Windows.Visibility IconVisibility
        => Icon is not null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>应用图标（取不到时为 null，此时只显示文字）。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(ShowIcon));
                OnPropertyChanged(nameof(IconVisibility));
                OnPropertyChanged(nameof(IconSize));
            }
        }
    }

    /// <summary>是否拿到了图标。</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>给 Border 用的边距（块之间留 1px 缝）。</summary>
    public System.Windows.Thickness Margin => new(X + 1, Y + 1, 0, 0);

    /// <summary>方块种类。</summary>
    public TreemapTileKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    /// <summary>中性块的点阵 / 斜纹纹理（应用块为 null）。</summary>
    public Brush? Texture
    {
        get => _texture;
        private set => SetProperty(ref _texture, value);
    }

    /// <summary>悬停提示文本。</summary>
    public string ToolTipText => DisplayName + Environment.NewLine + ValueText;

    /// <summary>更新度量、颜色、种类与图标（图标只在变化时才替换，避免每帧重建绑定）。</summary>
    public void Update(string displayName, string valueText, Brush fill, ImageSource? icon,
        TreemapTileKind kind = TreemapTileKind.Application, Brush? texture = null)
    {
        if (!ReferenceEquals(Icon, icon))
        {
            Icon = icon;
        }

        Kind = kind;
        if (!ReferenceEquals(Texture, texture))
        {
            Texture = texture;
        }

        if (!string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            DisplayName = displayName;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ToolTipText));
        }

        ValueText = valueText;
        Fill = fill;
    }
}
