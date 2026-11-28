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
                OnPropertyChanged(nameof(ShowIcon));
                OnPropertyChanged(nameof(IconVisibility));
            }
        }
    }

    /// <summary>是否显示应用名（太小的块只留颜色，避免文字糊成一团）。</summary>
    public bool ShowLabel => Width >= 58 && Height >= 22;

    /// <summary>是否显示数值（更小的块只显示名字）。</summary>
    public bool ShowValue => Width >= 76 && Height >= 38;

    /// <summary>是否显示应用图标（与旧版一致：块够大才画，避免压住文字）。</summary>
    public bool ShowIcon => Width >= 72 && Height >= 52;

    /// <summary>图标是否可见（块够大且确实拿到了图标）。</summary>
    public System.Windows.Visibility IconVisibility
        => ShowIcon && Icon is not null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>应用图标（取不到时为 null，此时只显示文字）。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(IconVisibility));
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
