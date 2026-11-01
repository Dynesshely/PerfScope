using PerfScope.Core.Abstractions;
using PerfScope.Core.Diagnostics;
using PerfScope.Core.Grouping;
using PerfScope.Core.History;
using PerfScope.Core.Native;
using PerfScope.Core.Sampling;
using PerfScope.Core.Settings;

namespace PerfScope.Core;

/// <summary>
/// 组合根：把各具体实现装配成接口。UI 只依赖本类与 <see cref="Abstractions"/> 里的接口，
/// 不直接 new 具体实现，替换实现时不用改 UI。
/// </summary>
/// <remarks>
/// 装配关系：
/// <code>
/// MonitorEngine
///   └─ SnapshotComposer (同时实现 ISnapshotProducer，自行驱动 ICapture)
///        ├─ NativeCapture     一次 NtQuerySystemInformation 取全表
///        └─ AppClassifier     进程 -> 应用的归并（带缓存）
/// HistoryStore              内存趋势 + 磁盘分钟聚合
/// SettingsStore             原子读写 settings.json
/// FileLog                   轮转文件日志
/// </code>
/// </remarks>
public static class CoreRuntime
{
    /// <summary>创建设置存储（默认路径 %LOCALAPPDATA%\PerfScope\settings.json）。</summary>
    public static ISettingsStore CreateSettingsStore(ILog? log = null)
        => new SettingsStore(log);

    /// <summary>创建文件日志（按设置的级别与大小轮转）。</summary>
    public static ILog CreateLogger(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppPaths.EnsureDirectories();
        return new FileLog(AppPaths.LogFile, settings.LogLevel, settings.LogMaxKilobytes);
    }

    /// <summary>创建应用归并器。</summary>
    public static IAppClassifier CreateClassifier(AppSettings settings)
        => new AppClassifier(settings ?? throw new ArgumentNullException(nameof(settings)));

    /// <summary>创建快照合成器（内部会 new 一个 NativeCapture）。</summary>
    public static ISnapshotComposer CreateComposer(AppSettings settings)
        => new SnapshotComposer(new NativeCapture(), CreateClassifier(settings), settings);

    /// <summary>创建采样引擎。</summary>
    public static IMonitorEngine CreateEngine(AppSettings settings, ILog? log = null)
        => new MonitorEngine(CreateComposer(settings), settings, log);

    /// <summary>创建历史存储。</summary>
    public static IHistoryStore CreateHistory(AppSettings settings, ILog? log = null)
        => new HistoryStore(settings, log);

    /// <summary>
    /// 一次性创建整套运行时服务（引擎 + 历史 + 日志），返回可直接使用的聚合对象。
    /// UI 若已有自己的装配逻辑可以不用本方法，它是给命令行/自测场景准备的。
    /// </summary>
    public static MonitorRuntime CreateRuntime(AppSettings settings, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logger = log ?? CreateLogger(settings);
        var history = CreateHistory(settings, logger);
        var engine = CreateEngine(settings, logger);
        return new MonitorRuntime(engine, history, logger);
    }
}

/// <summary>引擎 + 历史 + 日志的聚合体，方便一次性启动与释放。</summary>
public sealed class MonitorRuntime : IDisposable
{
    private bool _disposed;

    /// <summary>创建聚合体。</summary>
    public MonitorRuntime(IMonitorEngine engine, IHistoryStore history, ILog log)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        History = history ?? throw new ArgumentNullException(nameof(history));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Engine.FrameProduced += OnFrameProduced;
    }

    /// <summary>采样引擎。</summary>
    public IMonitorEngine Engine { get; }

    /// <summary>历史存储。</summary>
    public IHistoryStore History { get; }

    /// <summary>日志。</summary>
    public ILog Log { get; }

    /// <summary>最新一帧。</summary>
    public Models.SnapshotFrame? Latest => Engine.Latest;

    /// <summary>启动采样。</summary>
    public void Start() => Engine.Start();

    /// <summary>停止采样。</summary>
    public void Stop() => Engine.Stop();

    /// <summary>释放全部资源（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Engine.FrameProduced -= OnFrameProduced;
        try
        {
            Engine.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放采样引擎时出错", ex);
        }

        try
        {
            History.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn("刷新历史时出错", ex);
        }

        try
        {
            History.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放历史存储时出错", ex);
        }

        if (Log is IDisposable disposableLog)
        {
            try
            {
                disposableLog.Dispose();
            }
            catch
            {
                // 日志释放失败不影响退出
            }
        }
    }

    private void OnFrameProduced(object? sender, Models.SnapshotFrame frame)
    {
        try
        {
            History.Append(frame);
        }
        catch (Exception ex)
        {
            Log.Warn("写入历史失败", ex);
        }
    }
}
