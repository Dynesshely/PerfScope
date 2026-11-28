using System.Diagnostics;
using System.Windows;
using PerfScope.App.Chrome;
using PerfScope.App.Infrastructure;
using PerfScope.Core;

namespace PerfScope.App.Views;

/// <summary>关于窗口：版本、技术栈、许可、仓库与数据目录。</summary>
public partial class AboutWindow : ChromeWindow
{
    /// <summary>创建关于窗口。</summary>
    public AboutWindow()
    {
        InitializeComponent();
        VersionLine.Text = AppVersionInfo.Display + " · UI: WPF (MVVM)";
        DataDirLine.Text = "数据目录：" + AppPaths.DataDirectory;
    }

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e) => Open(AppPaths.DataDirectory);

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e) => Open("https://github.com/Dynesshely/PerfScope");

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不阻断窗口
        }
    }
}
