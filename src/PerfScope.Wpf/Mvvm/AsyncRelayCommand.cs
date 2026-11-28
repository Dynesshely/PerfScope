using System.Windows.Input;

namespace PerfScope.App.Mvvm;

/// <summary>
/// 异步命令：执行期间自动禁用，异常交给 <paramref name="onError"/>（默认吞掉并写调试输出），
/// 避免 async void 把异常抛到 Dispatcher 上导致进程崩溃。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    /// <summary>创建无参异步命令。</summary>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), onError)
    {
    }

    /// <summary>创建带参异步命令。</summary>
    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>是否正在执行。</summary>
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            if (_onError is null)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>手动通知可执行状态可能已变化。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
