using System.Windows;
using PerfScope.App.Chrome;

namespace PerfScope.App.Views;

/// <summary>历史窗口：按天查看落盘的应用聚合摘要。</summary>
public partial class HistoryWindow : ChromeWindow
{
    /// <summary>创建历史窗口。</summary>
    public HistoryWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
