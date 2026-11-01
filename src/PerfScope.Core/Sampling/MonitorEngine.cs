using System.Diagnostics;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Native;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Sampling;

/// <summary>
/// 常驻采样引擎：独立后台线程（优先级 BelowNormal）+ 可中断等待，
/// 负责按间隔驱动 <see cref="ISnapshotComposer"/>、吞掉单帧异常、连续失败指数退避、窗口隐藏时自适应降频。
/// </summary>
/// <remarks>
/// <para><b>线程模型</b>：<see cref="Start"/> 起一条后台线程，循环体用
/// <see cref="ManualResetEventSlim.Wait(int)"/> 睡眠；设置变更、可见性变化、强制刷帧都会
/// <see cref="ManualResetEventSlim.Set"/> 打断等待，因此无需重启线程。不用 <c>System.Threading.Timer</c>。</para>
/// <para><b>触发顺序</b>：先更新 <see cref="Latest"/>，再在后台线程触发 <see cref="FrameProduced"/>，
/// 保证事件处理者读到的 <see cref="Latest"/> 一定不旧于事件参数。</para>
/// <para><b>异常策略</b>：单帧异常只记日志（同类错误每分钟最多 1 条）并继续下一轮；
/// 连续失败时等待时间按 2 的幂退避，上限 60 秒。</para>
/// <para><b>采样来源</b>：引擎构造参数里没有 <c>ICapture</c>，因此要求传入的合成器实现
/// <see cref="ISnapshotProducer"/>（<see cref="SnapshotComposer"/> 就是默认实现）。</para>
/// </remarks>
public sealed class MonitorEngine : IMonitorEngine
{
    private const int MinIntervalMilliseconds = 100;
    private const int MaxIntervalMilliseconds = 60_000;
    private const int MaxBackoffMilliseconds = 60_000;
    private const int StopJoinMilliseconds = 3_000;
    private const int ForceSampleTimeoutMilliseconds = 2_000;
    private const int ErrorLogThrottleMilliseconds = 60_000;

    private readonly ISnapshotComposer _composer;
    private readonly ISnapshotProducer? _producer;
    private readonly ILog _log;
    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private readonly ManualResetEventSlim _forcedSampleCompleted = new(false);

    private volatile AppSettings _settings;
    private volatile bool _uiVisible = true;
    private volatile bool _stopping;
    private volatile bool _running;
    private volatile bool _disposed;

    private Thread? _thread;
    private volatile SnapshotFrame? _latest;
    private long _latestFrameStamp;
    private int _samplingGuard;
    private int _forcedSamplePending;
    private int _forcedSampleInFlight;
    private int _forcedSampleResult;
    private int _consecutiveFailures;
    private string? _lastErrorSignature;
    private long _lastErrorLogTimestamp;

    /// <summary>构造引擎。</summary>
    /// <param name="composer">帧合成器（需实现 <see cref="ISnapshotProducer"/> 才能驱动采样）。</param>
    /// <param name="settings">初始设置（内部保存副本）。</param>
    /// <param name="log">日志实现；<c>null</c> 时使用 <see cref="NullLog"/>。</param>
    public MonitorEngine(ISnapshotComposer composer, AppSettings settings, ILog? log = null)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();
        _log = log ?? NullLog.Instance;
        _producer = composer as ISnapshotProducer;

        // 原生采集器按契约无参构造（不带 ILog），在这里把它的诊断输出接到日志上。
        if (composer is SnapshotComposer concrete && concrete.Capture is NativeCapture nativeCapture)
        {
            nativeCapture.Diagnostics = message => _log.Warn(message);
        }
    }

    /// <inheritdoc />
    public SnapshotFrame? Latest => _latest;

    /// <inheritdoc />
    public event EventHandler<SnapshotFrame>? FrameProduced;

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <summary>已产生的帧数（诊断用）。</summary>
    public long FramesProduced => Interlocked.Read(ref _latestFrameStamp);

    /// <inheritdoc />
    public int EffectiveIntervalMs
    {
        get
        {
            AppSettings settings = _settings;
            int interval = settings.SampleIntervalMs;
            if (!_uiVisible && settings.AdaptiveThrottleWhenHidden)
            {
                interval = settings.HiddenSampleIntervalMs;
            }

            return Math.Clamp(interval, MinIntervalMilliseconds, MaxIntervalMilliseconds);
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MonitorEngine));
            }

            if (_running)
            {
                return;
            }

            _stopping = false;
            _consecutiveFailures = 0;
            _wakeSignal.Reset();
            _forcedSampleCompleted.Reset();

            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "PerfScope 采样线程",
                Priority = ThreadPriority.BelowNormal,
            };

            _thread = thread;
            _running = true;

            try
            {
                thread.Start();
            }
            catch (Exception exception)
            {
                _running = false;
                _thread = null;
                _log.Error("采样线程启动失败。", exception);
                throw;
            }
        }

        _log.Info($"采样引擎已启动，采样间隔 {EffectiveIntervalMs} ms。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            if (!_running && _thread is null)
            {
                return;
            }

            _stopping = true;
            thread = _thread;
        }

        Signal();
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            thread.Join(StopJoinMilliseconds);
        }

        lock (_lifecycleGate)
        {
            _thread = null;
            _running = false;
        }

        _log.Info("采样引擎已停止。");
    }

    /// <inheritdoc />
    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings previous = _settings;
        AppSettings clone = settings.Clone();
        _settings = clone;
        _composer.OnSettingsChanged(clone);

        // 采样间隔成倍变化时，旧基线的时间跨度与新间隔不匹配，丢弃基线重新起步。
        if (IsIntervalJump(previous.SampleIntervalMs, clone.SampleIntervalMs))
        {
            _composer.Reset();
        }

        Signal();
    }

    /// <inheritdoc />
    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible)
        {
            return;
        }

        _uiVisible = visible;
        Signal();
    }

    /// <inheritdoc />
    public bool ForceSample()
    {
        if (_disposed)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _forcedSampleInFlight, 1, 0) != 0)
        {
            // 已有一次强制采样在飞行中，直接拒绝，避免 UI 连点造成重入。
            return false;
        }

        try
        {
            if (!_running)
            {
                // 引擎未启动：在调用线程同步采一帧（首帧仍可能返回 null）。
                return SampleOnce() is not null;
            }

            _forcedSampleCompleted.Reset();
            Interlocked.Exchange(ref _forcedSampleResult, 0);
            Interlocked.Exchange(ref _forcedSamplePending, 1);
            Signal();

            if (!_forcedSampleCompleted.Wait(ForceSampleTimeoutMilliseconds))
            {
                return false;
            }

            return Volatile.Read(ref _forcedSampleResult) == 1;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _forcedSampleInFlight, 0);
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
            Stop();
        }
        catch (Exception exception)
        {
            _log.Error("采样引擎释放时停止失败。", exception);
        }

        _wakeSignal.Dispose();
        _forcedSampleCompleted.Dispose();
        if (_composer is IDisposable disposableComposer)
        {
            disposableComposer.Dispose();
        }
        else if (_composer is SnapshotComposer concrete && concrete.Capture is IDisposable disposableCapture)
        {
            disposableCapture.Dispose();
        }
    }

    // ================= 采样循环 =================

    private void Loop()
    {
        // 启动后立刻采一帧：首帧只建立基线（合成器返回 null）。
        SampleOnce();

        while (!_stopping)
        {
            int delay = ComputeDelayMilliseconds();
            bool signaled;
            try
            {
                signaled = _wakeSignal.Wait(delay);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (_stopping)
            {
                break;
            }

            _wakeSignal.Reset();

            if (signaled)
            {
                // 被设置变更 / 可见性切换 / 强制刷帧打断：按新参数重新计算等待时间。
                if (Interlocked.Exchange(ref _forcedSamplePending, 0) != 0)
                {
                    SnapshotFrame? frame = SampleOnce();
                    Interlocked.Exchange(ref _forcedSampleResult, frame is null ? 0 : 1);
                    try
                    {
                        _forcedSampleCompleted.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }

                continue;
            }

            SampleOnce();
        }
    }

    private int ComputeDelayMilliseconds()
    {
        int interval = EffectiveIntervalMs;
        int failures = _consecutiveFailures;
        if (failures <= 0)
        {
            return interval;
        }

        // 指数退避：interval * 2^failures，上限 60 秒。
        int shift = Math.Min(failures, 6);
        long backoff = (long)interval << shift;
        return (int)Math.Clamp(backoff, interval, MaxBackoffMilliseconds);
    }

    private SnapshotFrame? SampleOnce()
    {
        if (Interlocked.CompareExchange(ref _samplingGuard, 1, 0) != 0)
        {
            // 上一帧还没结束（理论上不会发生），跳过本次。
            return null;
        }

        try
        {
            if (_producer is null)
            {
                ReportFailure("合成器未实现 ISnapshotProducer，无法驱动采样（应当传入 Sampling.SnapshotComposer）。", null);
                return null;
            }

            SnapshotFrame? frame;
            try
            {
                frame = _producer.Compose();
            }
            catch (Exception exception)
            {
                ReportFailure("采样帧合成失败。", exception);
                return null;
            }

            if (frame is null)
            {
                // 首帧（建立基线）或时间源未前进：不是错误，不计入失败。
                return null;
            }

            _consecutiveFailures = 0;
            _latest = frame;
            Interlocked.Increment(ref _latestFrameStamp);

            try
            {
                FrameProduced?.Invoke(this, frame);
            }
            catch (Exception exception)
            {
                // 订阅者（UI）的异常不能打断采样循环。
                _log.Error("FrameProduced 事件处理异常已被隔离。", exception);
            }

            return frame;
        }
        finally
        {
            Interlocked.Exchange(ref _samplingGuard, 0);
        }
    }

    private void ReportFailure(string message, Exception? exception)
    {
        _consecutiveFailures++;

        string signature = exception is null ? message : exception.GetType().FullName + "|" + exception.Message;
        long now = Environment.TickCount64;
        bool sameError = string.Equals(signature, _lastErrorSignature, StringComparison.Ordinal);
        if (sameError && now - _lastErrorLogTimestamp < ErrorLogThrottleMilliseconds)
        {
            // 同类错误限流：每分钟最多一条。
            return;
        }

        _lastErrorSignature = signature;
        _lastErrorLogTimestamp = now;
        _log.Error($"{message}（连续失败 {_consecutiveFailures} 次，下次等待 {ComputeDelayMilliseconds()} ms）", exception);
    }

    private void Signal()
    {
        try
        {
            _wakeSignal.Set();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：忽略。
        }
    }

    private static bool IsIntervalJump(int previous, int current)
    {
        if (previous <= 0 || current <= 0)
        {
            return true;
        }

        return current >= previous * 2L || previous >= current * 2L;
    }
}
