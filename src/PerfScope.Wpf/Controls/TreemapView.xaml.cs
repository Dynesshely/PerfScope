using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerfScope.App.ViewModels;

namespace PerfScope.App.Controls;

/// <summary>
/// 分布视图控件。布局（每块的位置与大小）全部由 ViewModel 算好，视图只做三件事：
/// 上报可用尺寸、把块渲染出来、把鼠标悬停位置换算成「哪一块」。
/// </summary>
public partial class TreemapView : UserControl
{
    private TreemapPanelViewModel? _panel;

    /// <summary>创建控件。</summary>
    public TreemapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Host.SizeChanged += OnHostSizeChanged;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _panel = e.NewValue as TreemapPanelViewModel;
        if (_panel is not null && Host.ActualWidth > 0)
        {
            _panel.SetSize(Host.ActualWidth, Host.ActualHeight);
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _panel?.SetSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panel is null)
        {
            return;
        }

        var point = e.GetPosition(Host);
        TreemapTileViewModel? hit = null;
        foreach (var tile in _panel.Tiles)
        {
            if (point.X >= tile.X && point.X <= tile.X + tile.Width
                && point.Y >= tile.Y && point.Y <= tile.Y + tile.Height)
            {
                hit = tile;
                break;
            }
        }

        _panel.HoveredTile = hit;
        if (hit is null)
        {
            HoverCard.Visibility = Visibility.Collapsed;
            return;
        }

        HoverCard.Visibility = Visibility.Visible;
        double left = point.X + 14;
        double top = point.Y + 14;
        if (left + HoverCard.ActualWidth > Host.ActualWidth)
        {
            left = Math.Max(0, point.X - HoverCard.ActualWidth - 10);
        }

        if (top + HoverCard.ActualHeight > Host.ActualHeight)
        {
            top = Math.Max(0, point.Y - HoverCard.ActualHeight - 10);
        }

        HoverCard.Margin = new Thickness(left, top, 0, 0);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_panel is not null)
        {
            _panel.HoveredTile = null;
        }

        HoverCard.Visibility = Visibility.Collapsed;
    }
}
