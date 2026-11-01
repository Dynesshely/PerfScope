using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Sampling;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Sampling;

/// <summary><see cref="MonitorEngine"/> 的后台循环、间隔切换、强制刷帧与异常隔离测试。</summary>
public sealed class MonitorEngineTests
{
    private static MonitorEngine CreateEngine(
        out SyntheticCapture capture,
        out SnapshotComposer composer,
        int intervalMs = 200,
        int hiddenIntervalMs = 5_000,
        bool adaptiveThrottle = false,
        ILog? log = null)
    {
        var settings = new AppSettings
        {
            SampleIntervalMs = intervalMs,
            HiddenSampleIntervalMs = hiddenIntervalMs,
            AdaptiveThrottleWhenHidden = adaptiveThrottle,
            SmoothingWindow = 1,
        };

        capture = new SyntheticCapture();
        composer = new SnapshotComposer(capture, new StubClassifier(), settings);
        return new MonitorEngine(composer, settings, log);
    }

    private static bool WaitForFrames(IMonitorEngine engine, long target, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (engine is MonitorEngine concrete && concrete.FramesProduced >= target)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return engine.Latest is not null && engine is MonitorEngine monitor && monitor.FramesProduced >= target;
    }

    [Fact]
    public void 后台线程持续产出帧()
    {
        using MonitorEngine engine = CreateEngine(out SyntheticCapture capture, out _, intervalMs: 200);

        engine.Start();
        engine.Start(); // 幂等

        Assert.True(engine.IsRunning);
        Assert.True(WaitForFrames(engine, 2, TimeSpan.FromSeconds(6)), "10 秒内未产出 2 帧");
        Assert.NotNull(engine.Latest);
        Assert.True(capture.CaptureCount >= 3);
        Assert.Equal(200, engine.EffectiveIntervalMs);

        engine.Stop();
        engine.Stop(); // 可重复调用
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void 隐藏窗口时按设置降频()
    {
        using MonitorEngine engine = CreateEngine(out _, out _, intervalMs: 1_000, hiddenIntervalMs: 7_000, adaptiveThrottle: true);

        Assert.Equal(1_000, engine.EffectiveIntervalMs);

        engine.SetUiVisible(false);
        Assert.Equal(7_000, engine.EffectiveIntervalMs);

        engine.SetUiVisible(true);
        Assert.Equal(1_000, engine.EffectiveIntervalMs);

        // 关闭自适应后，隐藏窗口也保持原间隔。
        var settings = new AppSettings
        {
            SampleIntervalMs = 1_000,
            HiddenSampleIntervalMs = 7_000,
            AdaptiveThrottleWhenHidden = false,
        };
        engine.SetUiVisible(false);
        engine.UpdateSettings(settings);
        Assert.Equal(1_000, engine.EffectiveIntervalMs);
    }

    [Fact]
    public void 设置热更新立即生效()
    {
        using MonitorEngine engine = CreateEngine(out _, out _, intervalMs: 2_000);

        engine.UpdateSettings(new AppSettings { SampleIntervalMs = 500, SmoothingWindow = 5, CpuMode = CpuNormalization.PerCorePercent });
        Assert.Equal(500, engine.EffectiveIntervalMs);

        engine.UpdateSettings(new AppSettings { SampleIntervalMs = 60_000 });
        Assert.Equal(60_000, engine.EffectiveIntervalMs);

        engine.UpdateSettings(new AppSettings { SampleIntervalMs = 10 });
        Assert.Equal(100, engine.EffectiveIntervalMs); // 下限保护
    }

    [Fact]
    public void 未启动时强制采样按需产生帧()
    {
        using MonitorEngine engine = CreateEngine(out SyntheticCapture capture, out _);

        Assert.False(engine.ForceSample()); // 首帧只是基线
        Assert.Null(engine.Latest);

        Assert.True(engine.ForceSample());
        Assert.NotNull(engine.Latest);
        Assert.Equal(2, capture.CaptureCount);
    }

    [Fact]
    public void 运行中强制采样能立即刷新且事件先于返回()
    {
        using MonitorEngine engine = CreateEngine(out _, out _, intervalMs: 300);

        int frameCount = 0;
        long latestAtEvent = -1;
        engine.FrameProduced += (sender, frame) =>
        {
            Interlocked.Increment(ref frameCount);
            MonitorEngine source = (MonitorEngine)sender!;
            latestAtEvent = source.Latest is null ? 0 : 1;
        };

        engine.Start();
        Assert.True(WaitForFrames(engine, 1, TimeSpan.FromSeconds(5)), "首帧基线未建立");

        long before = engine.FramesProduced;
        Assert.True(engine.ForceSample(), "运行中强制采样应当成功");
        Assert.True(WaitForFrames(engine, before + 1, TimeSpan.FromSeconds(5)), "强制采样未产出新帧");
        Assert.True(frameCount >= 2);
        Assert.Equal(1, latestAtEvent);

        engine.Stop();
    }

    [Fact]
    public void 事件订阅者抛异常不打断采样循环()
    {
        using MonitorEngine engine = CreateEngine(out _, out _, intervalMs: 150);

        int invoked = 0;
        engine.FrameProduced += (_, _) =>
        {
            Interlocked.Increment(ref invoked);
            throw new InvalidOperationException("故意抛出，验证隔离");
        };

        engine.Start();
        Assert.True(WaitForFrames(engine, 2, TimeSpan.FromSeconds(6)), "事件异常后采样循环应当继续");
        Assert.True(invoked >= 2);
        Assert.True(engine.IsRunning);

        engine.Stop();
    }

    [Fact]
    public void 释放后不再可用()
    {
        MonitorEngine engine = CreateEngine(out _, out _);
        engine.Dispose();
        engine.Dispose(); // 可重复

        Assert.False(engine.IsRunning);
        Assert.False(engine.ForceSample());
        engine.Stop(); // 释放后 Stop 不应抛出
    }

    [Fact]
    public void 单帧异常被吞掉并记录限流日志()
    {
        var log = new RecordingLog();
        var settings = new AppSettings { SampleIntervalMs = 150, SmoothingWindow = 1 };
        var composer = new ThrowingComposer();
        using var engine = new MonitorEngine(composer, settings, log);

        engine.Start();
        Thread.Sleep(800); // 首次立刻采样，之后按退避重试

        Assert.True(composer.Attempts >= 2);

        // 同类错误每分钟只记一条（启动/停止的 Info 不计入）。
        string[] errors = [.. log.Messages.Where(static message => message.Contains("采样帧合成失败", StringComparison.Ordinal))];
        Assert.Single(errors);
        Assert.True(engine.IsRunning);

        engine.Stop();
    }

    /// <summary>永远抛异常的合成器，用于验证引擎的异常兜底。</summary>
    private sealed class ThrowingComposer : ISnapshotComposer, ISnapshotProducer
    {
        /// <summary>被调用的次数。</summary>
        public int Attempts { get; private set; }

        public SnapshotFrame? Compose()
        {
            Attempts++;
            throw new InvalidOperationException("合成失败（测试）");
        }

        public SnapshotFrame? Compose(SystemRawSample current) => throw new InvalidOperationException("不应调用");

        public void OnSettingsChanged(AppSettings settings)
        {
        }

        public void Reset()
        {
        }
    }
}
