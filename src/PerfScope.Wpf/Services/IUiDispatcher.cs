namespace PerfScope.App.Services;

/// <summary>
/// UI 线程调度抽象。ViewModel 不直接引用 WPF 的 Dispatcher，这样单元测试里可以注入一个同步实现。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前是否已在 UI 线程上。</summary>
    bool IsUiThread { get; }

    /// <summary>把动作投递到 UI 线程（已在 UI 线程时立即执行）。</summary>
    void Post(Action action);

    /// <summary>把动作异步投递到 UI 线程。</summary>
    Task InvokeAsync(Action action);
}

/// <summary>基于 WPF Dispatcher 的实现。</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public WpfUiDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (IsUiThread)
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        if (IsUiThread)
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}
