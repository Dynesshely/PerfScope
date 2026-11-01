using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Sampling;

/// <summary>构造原始采样的小工具。</summary>
internal static class Samples
{
    /// <summary>1 秒对应的 100ns 计时单位。</summary>
    public const long Second = 10_000_000L;

    /// <summary>构造单个进程样本。</summary>
    public static ProcessRawSample Process(
        int pid,
        string imageName,
        long cpuTicks = 0,
        long createTicks = 1,
        long workingSet = 1_000_000,
        long privateBytes = 500_000,
        long readBytes = 0,
        long writeBytes = 0,
        int parentPid = 0,
        string? executablePath = null,
        int threadCount = 1,
        int handleCount = 10)
        => new(pid, parentPid, imageName, executablePath, cpuTicks, createTicks, workingSet, privateBytes, readBytes, writeBytes, threadCount, handleCount);

    /// <summary>构造一次系统级采样。</summary>
    public static SystemRawSample Frame(
        DateTime timestampUtc,
        ProcessRawSample[] processes,
        int logicalProcessors = 4,
        long idleTicks = 0,
        long kernelTicks = 0,
        long userTicks = 0,
        long totalPhysicalBytes = 16L * 1024 * 1024 * 1024,
        long availablePhysicalBytes = 8L * 1024 * 1024 * 1024)
        => new(timestampUtc, logicalProcessors, idleTicks, kernelTicks, userTicks, totalPhysicalBytes, availablePhysicalBytes, processes);

    /// <summary>按固定顺序生成时间戳（测试不关心真实时间）。</summary>
    public static DateTime Timestamp(int index) => new(2026, 1, 1, 0, 0, index, DateTimeKind.Utc);
}

/// <summary>可注入固定值的单调时钟，单位与频率由构造参数决定。</summary>
internal sealed class TestClock
{
    private long _value;

    public TestClock(long frequency = 1000)
    {
        Frequency = frequency;
    }

    /// <summary>时间源频率（每秒的 tick 数）。</summary>
    public long Frequency { get; }

    /// <summary>当前值。</summary>
    public long Now => _value;

    /// <summary>前进指定 tick。</summary>
    public void Advance(long ticks) => _value += ticks;

    /// <summary>直接设置当前值。</summary>
    public void Set(long ticks) => _value = ticks;
}

/// <summary>预置样本队列的采集器。</summary>
internal sealed class FakeCapture : ICapture
{
    private readonly object _gate = new();
    private readonly Queue<SystemRawSample> _queue = new();

    /// <summary>被调用 <see cref="Capture"/> 的次数。</summary>
    public int CaptureCount { get; private set; }

    /// <summary>压入一次采样。</summary>
    public void Enqueue(SystemRawSample sample)
    {
        lock (_gate)
        {
            _queue.Enqueue(sample);
        }
    }

    /// <inheritdoc />
    public SystemRawSample Capture()
    {
        lock (_gate)
        {
            CaptureCount++;
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException("FakeCapture 的样本队列已空。");
            }

            return _queue.Dequeue();
        }
    }
}

/// <summary>每次采集都自增计数器的合成采集器，用于引擎冒烟测试（不依赖真实系统）。</summary>
internal sealed class SyntheticCapture : ICapture
{
    private readonly object _gate = new();
    private int _frame;

    /// <summary>被调用 <see cref="Capture"/> 的次数。</summary>
    public int CaptureCount { get; private set; }

    /// <inheritdoc />
    public SystemRawSample Capture()
    {
        lock (_gate)
        {
            CaptureCount++;
            _frame++;
            long cpuTicks = _frame * 1_000_000L;
            ProcessRawSample[] processes =
            [
                Samples.Process(1001, "alpha.exe", cpuTicks, workingSet: 10_000_000, privateBytes: 8_000_000, executablePath: @"C:\Apps\Alpha\alpha.exe"),
                Samples.Process(1002, "beta.exe", cpuTicks / 2, workingSet: 20_000_000, privateBytes: 16_000_000, executablePath: @"C:\Apps\Beta\beta.exe"),
            ];

            return new SystemRawSample(
                DateTime.UtcNow,
                4,
                _frame * 500L,
                _frame * 800L,
                _frame * 200L,
                16L * 1024 * 1024 * 1024,
                8L * 1024 * 1024 * 1024,
                processes);
        }
    }
}

/// <summary>按委托映射的归并器。</summary>
internal sealed class StubClassifier : IAppClassifier
{
    private readonly Func<ProcessRawSample, AppIdentity> _map;

    public StubClassifier()
        : this(static process => new AppIdentity(
            "exe:" + process.ImageName,
            process.ImageName,
            null,
            AppKind.ExeName,
            "测试归并：按映像名"))
    {
    }

    public StubClassifier(Func<ProcessRawSample, AppIdentity> map)
    {
        _map = map;
    }

    /// <summary><see cref="Classify"/> 被调用的次数。</summary>
    public int ClassifyCount { get; private set; }

    /// <summary><see cref="OnSettingsChanged"/> 被调用的次数。</summary>
    public int SettingsChangeCount { get; private set; }

    /// <summary>最近一次收到的设置引用。</summary>
    public AppSettings? LastSettings { get; private set; }

    /// <inheritdoc />
    public AppIdentity Classify(ProcessRawSample process, ProcessRawSample? parent)
    {
        ClassifyCount++;
        return _map(process);
    }

    /// <inheritdoc />
    public void OnSettingsChanged(AppSettings settings)
    {
        SettingsChangeCount++;
        LastSettings = settings;
    }

    /// <inheritdoc />
    public void ResetCache()
    {
    }
}

/// <summary>记录日志的日志实现，用于断言限流行为。</summary>
internal sealed class RecordingLog : ILog
{
    private readonly object _gate = new();
    private readonly List<string> _messages = new();

    /// <summary>收到的日志条数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>收到的日志文本。</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _messages.Add(level + "|" + message);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
    }
}
