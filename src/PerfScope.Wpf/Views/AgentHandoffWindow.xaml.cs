using System.Windows;
using PerfScope.App.Chrome;

namespace PerfScope.App.Views;

/// <summary>「接入 Agent」窗口：展示并复制 AppHost 生成的 Prompt。</summary>
public partial class AgentHandoffWindow : ChromeWindow
{
    private readonly Func<string> _promptFactory;

    /// <summary>创建窗口。</summary>
    /// <param name="promptFactory">每次重新生成时调用的工厂（由外壳注入 AppHost.BuildAgentPrompt）。</param>
    public AgentHandoffWindow(Func<string> promptFactory)
    {
        InitializeComponent();
        _promptFactory = promptFactory ?? throw new ArgumentNullException(nameof(promptFactory));
        Regenerate();
    }

    private void Regenerate()
    {
        try
        {
            PromptBox.Text = _promptFactory();
            StatusLine.Text = string.Empty;
        }
        catch (Exception ex)
        {
            PromptBox.Text = ex.Message;
            StatusLine.Text = "生成失败";
        }
    }

    private void OnRegenerateClick(object sender, RoutedEventArgs e) => Regenerate();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PromptBox.Text);
            StatusLine.Text = "已复制";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
