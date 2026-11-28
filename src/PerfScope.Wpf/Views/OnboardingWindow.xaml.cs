using System.Windows;
using PerfScope.App.Chrome;
using PerfScope.Core.Settings;

namespace PerfScope.App.Views;

/// <summary>首次启动引导：三条要点 + 「以后不再显示」。</summary>
public partial class OnboardingWindow : ChromeWindow
{
    /// <summary>创建引导窗口。</summary>
    public OnboardingWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
