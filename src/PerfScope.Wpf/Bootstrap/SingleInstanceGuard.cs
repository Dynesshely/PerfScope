namespace PerfScope.App.Bootstrap;

/// <summary>
/// 单实例互斥 + 唤醒通道。两个入口共用同一套名字，保证 WinForms 版与 WPF 版不会同时跑起来。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PerfScope.SingleInstance";
    private const string MutexNameDemo = @"Local\PerfScope.SingleInstance.Demo";
    private const string ActivateEventName = @"Local\PerfScope.Activate";
    private const string ActivateEventNameDemo = @"Local\PerfScope.Activate.Demo";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private bool _disposed;

    private SingleInstanceGuard(bool demo, Mutex mutex, EventWaitHandle activateEvent)
    {
        Demo = demo;
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    /// <summary>是否演示 / 截图模式（演示模式允许与正式实例并行）。</summary>
    public bool Demo { get; }

    /// <summary>请求显示主窗口时触发（由后台线程等待唤醒事件后发出）。</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>尝试成为唯一实例。已有实例时返回 <c>null</c> 并唤醒它。</summary>
    public static SingleInstanceGuard? TryAcquire(bool demo)
    {
        string mutexName = demo ? MutexNameDemo : MutexName;
        var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            ActivateExisting(demo);
            return null;
        }

        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, demo ? ActivateEventNameDemo : ActivateEventName);
        var guard = new SingleInstanceGuard(demo, mutex, activateEvent);
        guard.StartListening();
        return guard;
    }

    private void StartListening()
    {
        var thread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (!_activateEvent.WaitOne(500))
                    {
                        continue;
                    }

                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    // 唤醒失败不影响主流程
                }
            }
        })
        {
            IsBackground = true,
            Name = "PerfScope.ActivationListener",
        };
        thread.Start();
    }

    private static void ActivateExisting(bool demo)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(demo ? ActivateEventNameDemo : ActivateEventName);
            handle.Set();
        }
        catch
        {
            // 已有实例可能刚退出，忽略
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _activateEvent.Set();
        }
        catch
        {
            // 忽略
        }

        _activateEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // 未持有互斥体时忽略
        }

        _mutex.Dispose();
    }
}
