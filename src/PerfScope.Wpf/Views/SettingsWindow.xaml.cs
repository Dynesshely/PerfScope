using System.Windows;
using PerfScope.App.Chrome;
using PerfScope.App.ViewModels;

namespace PerfScope.App.Views;

/// <summary>设置窗口：只有一个 ViewModel 与两个按钮，业务逻辑全在 <see cref="SettingsViewModel"/> 里。</summary>
public partial class SettingsWindow : ChromeWindow
{
    private readonly SettingsViewModel _viewModel;
    private bool _saved;

    /// <summary>创建设置窗口。</summary>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Saved += (_, _) =>
        {
            _saved = true;
            DialogResult = true;
            Close();
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => _viewModel.SaveCommand.Execute(null);

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = _saved;
        Close();
    }
}
