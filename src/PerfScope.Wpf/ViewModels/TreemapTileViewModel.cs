using System.Windows.Media;
using PerfScope.App.Mvvm;

namespace PerfScope.App.ViewModels;

/// <summary>分布视图里的一块矩形。几何用像素坐标，由 <see cref="TreemapPanelViewModel"/> 在尺寸变化时重算。</summary>
public sealed class TreemapTileViewModel : ObservableObject
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private string _valueText = string.Empty;
    private Brush _fill = Brushes.Gray;

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
            }
        }
    }

    /// <summary>是否显示应用名（太小的块只留颜色，避免文字糊成一团）。</summary>
    public bool ShowLabel => Width >= 58 && Height >= 22;

    /// <summary>是否显示数值（更小的块只显示名字）。</summary>
    public bool ShowValue => Width >= 76 && Height >= 38;

    /// <summary>给 Border 用的边距（块之间留 1px 缝）。</summary>
    public System.Windows.Thickness Margin => new(X + 1, Y + 1, 0, 0);

    /// <summary>悬停提示文本。</summary>
    public string ToolTipText => DisplayName + Environment.NewLine + ValueText;

    /// <summary>更新度量与颜色。</summary>
    public void Update(string displayName, string valueText, Brush fill)
    {
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
