using System.Windows;

namespace PerfScope.App.Services;

/// <summary>统一的消息框入口：保证图标、所有者窗口与本地化文案一致。</summary>
internal static class MessageBoxService
{
    /// <summary>显示错误。</summary>
    public static void ShowError(string message, string? title = null)
        => Show(message, title ?? "PerfScope", MessageBoxImage.Error);

    /// <summary>显示信息。</summary>
    public static void ShowInfo(string message, string? title = null)
        => Show(message, title ?? "PerfScope", MessageBoxImage.Information);

    /// <summary>询问是非，默认「否」。</summary>
    public static bool Confirm(string message, string? title = null)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title ?? "PerfScope", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, title ?? "PerfScope", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    private static void Show(string message, string title, MessageBoxImage image)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        }
    }
}
