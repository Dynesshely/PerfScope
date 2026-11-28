using System.Windows;
using System.Windows.Threading;

namespace PerfScope.App;

/// <summary>
/// WPF 应用对象。只做资源合并与全局异常兜底；启动流程在 <see cref="Wpf.WpfBootstrap"/>，
/// 与 WinForms 版并行存在，重写完成后 WinForms 入口会整体删除。
/// </summary>
public partial class App : Application
{
    /// <summary>构造应用对象（不自动 Run，由引导流程控制启动顺序）。</summary>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Infrastructure.CrashLog.Write(ex);
            }
        };
    }

    private bool _errorDialogShown;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Infrastructure.CrashLog.Write(e.Exception);

        // 同一个界面线程异常可能在每次渲染时重复触发：只提示一次，避免模态对话框堆叠把界面锁死
        if (!_errorDialogShown)
        {
            _errorDialogShown = true;
            Services.MessageBoxService.ShowError("界面线程发生未处理异常，程序已记录日志。\n\n" + e.Exception.Message);
        }

        e.Handled = true;
    }
}
