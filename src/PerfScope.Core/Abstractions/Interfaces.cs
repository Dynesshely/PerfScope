using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Abstractions;

/// <summary>
/// 采集一次系统原始计数器。实现类：<c>PerfScope.Core.Native.NativeCapture</c>（无参构造）。
/// 要求：单次调用只做一次 NtQuerySystemInformation 批量查询 + 少量系统调用，禁止对每个进程
/// OpenProcess/WMI/PerformanceCounter 逐进程轮询 —— 这是本工具自身开销低的关键。
/// </summary>
public interface ICapture
{
    /// <summary>采集一次全量快照。可在任意线程调用，实现需保证线程安全。</summary>
    SystemRawSample Capture();
}

/// <summary>
/// 把单个进程归并到「应用」。实现类：<c>PerfScope.Core.Grouping.AppClassifier</c>（构造参数 <see cref="AppSettings"/>）。
/// 归并优先级（实现必须遵循，并写进 <see cref="AppIdentity.Reason"/>）：
/// 用户规则 → 版本信息(ProductName+CompanyName) → 安装根目录 → exe 所在目录 → exe 文件名 → 兜底。
/// </summary>
public interface IAppClassifier
{
    /// <summary>
    /// 归类一个进程。<paramref name="parent"/> 为父进程样本（父进程本帧不存在时为 <c>null</c>），
    /// 用于「子进程继承父进程归属」。实现必须缓存 exe 路径 → 归类结果，避免每帧读盘。
    /// </summary>
    AppIdentity Classify(ProcessRawSample process, ProcessRawSample? parent);

    /// <summary>设置热更新后调用。</summary>
    void OnSettingsChanged(AppSettings settings);

    /// <summary>清空缓存（例如规则大改之后）。</summary>
    void ResetCache();
}

/// <summary>
/// 把一次原始采样合成为「应用级」<see cref="SnapshotFrame"/>。实现类：<c>PerfScope.Core.Sampling.SnapshotComposer</c>
/// （构造参数 <c>ICapture, IAppClassifier, AppSettings</c>）。
/// 实现负责：差分 CPU/IO（处理 PID 复用与新进程）、按应用聚合、平滑、按 CPU 降序排序。
/// </summary>
public interface ISnapshotComposer
{
    /// <summary>
    /// 合成一帧。内部保存上一帧用于差分；首帧（无上一帧）返回 <c>null</c>。
    /// </summary>
    SnapshotFrame? Compose(SystemRawSample current);

    /// <summary>设置热更新。</summary>
    void OnSettingsChanged(AppSettings settings);

    /// <summary>丢弃上一帧状态（采样间隔大幅变化或恢复暂停后调用）。</summary>
    void Reset();
}

/// <summary>
/// 常驻采样引擎。实现类：<c>PerfScope.Core.Sampling.MonitorEngine</c>
/// （构造参数 <c>ISnapshotComposer, AppSettings, ILog</c>）。
/// 负责后台线程按间隔采样、异常兜底（单次失败不得终止循环）、自身开销统计（CPU 差分 + 工作集），
/// 以及主窗口隐藏时的自适应降频。
/// </summary>
public interface IMonitorEngine : IDisposable
{
    /// <summary>最新一帧；尚未产生时为 <c>null</c>。可在任意线程读取。</summary>
    SnapshotFrame? Latest { get; }

    /// <summary>每产生一帧触发一次（在后台线程触发）。</summary>
    event EventHandler<SnapshotFrame>? FrameProduced;

    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>当前实际生效的采样间隔（毫秒），含自适应降频后的值。</summary>
    int EffectiveIntervalMs { get; }

    /// <summary>启动采样循环。</summary>
    void Start();

    /// <summary>停止采样循环。</summary>
    void Stop();

    /// <summary>设置热更新（采样间隔、平滑窗口、口径等）。</summary>
    void UpdateSettings(AppSettings settings);

    /// <summary>立即采样一次（UI 手动刷新用），返回是否成功产生新帧。</summary>
    bool ForceSample();

    /// <summary>
    /// 告知主窗口是否可见。可见性变化时引擎据此在 `SampleIntervalMs` 与 `HiddenSampleIntervalMs`
    /// 之间切换（受 `AdaptiveThrottleWhenHidden` 控制）。托盘常驻期间窗口不可见，应当调用本方法降频。
    /// </summary>
    void SetUiVisible(bool visible);
}

/// <summary>
/// 历史与趋势存储。实现类：<c>PerfScope.Core.History.HistoryStore</c>（构造参数 <c>AppSettings, ILog?, string? rootDirectory = null</c>）。
/// 设计约束：内存里只保留应用级环形缓冲（受 <see cref="AppSettings.MemoryRetentionMinutes"/> 限制），
/// 磁盘上按天写紧凑定长记录，受 <see cref="AppSettings.DiskRetentionDays"/> 限制。
/// 所有方法必须线程安全（引擎后台线程写，UI 线程读）。
/// </summary>
public interface IHistoryStore : IDisposable
{
    /// <summary>追加一帧（线程安全、非阻塞，内部批量落盘）。</summary>
    void Append(SnapshotFrame frame);

    /// <summary>把缓冲刷到磁盘。</summary>
    void Flush();

    /// <summary>取某个应用的内存趋势，最多 <paramref name="maxPoints"/> 个点（时间升序）。</summary>
    IReadOnlyList<TrendPoint> GetTrend(string appKey, int maxPoints);

    /// <summary>查询某天（本地日期）的应用聚合摘要，按 CPU 降序。</summary>
    IReadOnlyList<DailyAppSummary> QueryDay(DateOnly date);

    /// <summary>已落盘历史中存在数据的日期（升序）。</summary>
    IReadOnlyList<DateOnly> GetAvailableDays();

    /// <summary>按保留策略清理过期文件。</summary>
    void Trim();

    /// <summary>设置热更新（口径、保留时长、落盘开关等变化后调用）。</summary>
    void OnSettingsChanged(AppSettings settings);
}

/// <summary>
/// 设置读写。实现类：<c>PerfScope.Core.Settings.SettingsStore</c>（构造参数 <c>ILog?, string? settingsPath = null</c>）。
/// 读取时必须容错：文件损坏/字段越界时回退默认值或就近修正，绝不能让程序启动失败。
/// </summary>
public interface ISettingsStore
{
    /// <summary>设置文件完整路径。</summary>
    string SettingsPath { get; }

    /// <summary>读取设置（不存在则返回默认值并落盘）。</summary>
    AppSettings Load();

    /// <summary>保存设置（原子写入：先写临时文件再替换）。</summary>
    void Save(AppSettings settings);
}

/// <summary>极简日志接口，避免引入第三方依赖。</summary>
public interface ILog
{
    /// <summary>写一条日志。</summary>
    void Write(LogLevel level, string message, Exception? exception = null);

    /// <summary>刷新缓冲。</summary>
    void Flush();
}

/// <summary>日志便捷扩展。</summary>
public static class LogExtensions
{
    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);

    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warn, message, ex);

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);
}

/// <summary>什么都不做的日志实现，用于单测与 CLI 场景。</summary>
public sealed class NullLog : ILog
{
    /// <summary>共享实例。</summary>
    public static readonly NullLog Instance = new();

    private NullLog()
    {
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }

    /// <inheritdoc />
    public void Flush()
    {
    }
}
