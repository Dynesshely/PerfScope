using PerfScope.App.Demo;
using PerfScope.App.Infrastructure;
using PerfScope.Core;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Insights;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App;

/// <summary>
/// 应用层的组合根：把设置、引擎、历史、日志装配成一套可热更新的服务，
/// 并对所有 Core 调用做异常兜底，保证界面不会因为某次采样失败而崩溃。
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly bool _demo;
    private readonly SessionAnalytics? _analytics;
    private readonly InsightStore? _insights;
    private bool _paused;
    private bool _disposed;
    private DateTime _lastReportUtc = DateTime.MinValue;

    private AppHost(
        AppSettings settings,
        ISettingsStore settingsStore,
        IMonitorEngine engine,
        IHistoryStore history,
        ILog log,
        bool demo,
        SessionAnalytics? analytics = null,
        InsightStore? insights = null)
    {
        Settings = settings;
        SettingsStore = settingsStore;
        Engine = engine;
        History = history;
        Log = log;
        _demo = demo;
        _analytics = analytics;
        _insights = insights;
        Engine.FrameProduced += OnFrameProduced;
    }

    /// <summary>当前生效的设置对象（引用会被原地更新，界面只读）。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>设置存储。</summary>
    public ISettingsStore SettingsStore { get; }

    /// <summary>采样引擎。</summary>
    public IMonitorEngine Engine { get; }

    /// <summary>历史存储。</summary>
    public IHistoryStore History { get; }

    /// <summary>日志。</summary>
    public ILog Log { get; }

    /// <summary>是否演示模式（不依赖真实采样实现）。</summary>
    public bool IsDemo => _demo;

    /// <summary>最新一帧；实现尚未就绪或读取失败时返回 <c>null</c>，界面据此显示占位文案。</summary>
    public SnapshotFrame? Latest
    {
        get
        {
            try
            {
                return Engine.Latest;
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Debug, "读取最新帧失败", ex);
                return null;
            }
        }
    }

    /// <summary>当前实际生效的采样间隔（毫秒）；读取失败时回退到设置值。</summary>
    public int EffectiveIntervalMs
    {
        get
        {
            try
            {
                return Engine.EffectiveIntervalMs;
            }
            catch
            {
                return Settings.SampleIntervalMs;
            }
        }
    }

    /// <summary>是否处于暂停状态。</summary>
    public bool IsPaused => _paused;

    /// <summary>最近一次操作失败的中文原因（供界面提示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>设置被成功应用后触发（UI 线程）。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>暂停状态变化后触发。</summary>
    public event EventHandler<bool>? PauseChanged;

    /// <summary>按运行模式创建服务。演示模式完全不触碰真实数据目录与注册表。</summary>
    public static AppHost Create(bool demo, bool mock = false, string? languageOverride = null)
    {
        if (demo || mock)
        {
            // 演示 / 截图模式没有配置文件，语言用命令行覆盖值（--lang en 之类），默认跟随系统
            Localization.Loc.Apply(languageOverride ?? Localization.Loc.System);

            var demoSettings = new AppSettings();
            IMonitorEngine demoEngine = mock ? new MockEngine(demoSettings) : new DemoEngine(demoSettings);
            var demoHistory = new DemoHistory(demoSettings);
            var demoStore = new DemoSettingsStore(demoSettings);
            return new AppHost(demoSettings, demoStore, demoEngine, demoHistory, NullLog.Instance, demo: true);
        }

        AppPaths.EnsureDirectories();

        // 先拿默认设置，再据此创建日志，最后用带日志的设置存储重读一次，保证启动失败原因可追溯。
        var bootstrapStore = CoreRuntime.CreateSettingsStore(NullLog.Instance);
        AppSettings settings = bootstrapStore.Load();

        // 语言必须在任何 UI 构造前确定（文案在构造时求值）
        Localization.Loc.Apply(languageOverride ?? settings.Language);
        ILog log = CoreRuntime.CreateLogger(settings);
        ISettingsStore settingsStore = CoreRuntime.CreateSettingsStore(log);
        IMonitorEngine engine = CoreRuntime.CreateEngine(settings, log);
        IHistoryStore history = CoreRuntime.CreateHistory(settings, log);
        var analytics = new SessionAnalytics(settings);
        var insights = new InsightStore(AppPaths.DataDirectory, log);
        return new AppHost(settings, settingsStore, engine, history, log, demo: false, analytics, insights);
    }

    /// <summary>启动采样。失败不抛异常，只记录日志。</summary>
    public bool Start()
    {
        try
        {
            Engine.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = "启动采样失败：" + ex.Message;
            SafeLog(LogLevel.Error, "启动采样失败", ex);
            return false;
        }
    }

    /// <summary>暂停 / 继续监控，并同步 <see cref="IsPaused"/> 与事件。</summary>
    public bool SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return true;
        }

        try
        {
            if (paused)
            {
                Engine.Stop();
            }
            else
            {
                Engine.Start();
            }

            _paused = paused;
            PauseChanged?.Invoke(this, _paused);
            return true;
        }
        catch (Exception ex)
        {
            LastError = (paused ? "暂停" : "继续") + "监控失败：" + ex.Message;
            SafeLog(LogLevel.Error, "切换暂停状态失败", ex);
            return false;
        }
    }

    /// <summary>告知引擎主窗口是否可见（用于隐藏时自适应降频）。</summary>
    public void SetUiVisible(bool visible)
    {
        try
        {
            Engine.SetUiVisible(visible);
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "设置界面可见性失败", ex);
        }
    }

    /// <summary>立即采样一次。</summary>
    public bool ForceSample()
    {
        try
        {
            return Engine.ForceSample();
        }
        catch (Exception ex)
        {
            LastError = "立即采样失败：" + ex.Message;
            SafeLog(LogLevel.Warn, "立即采样失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 应用新设置：落盘 → 引擎热更新 → 历史热更新 → 开机启动同步 → 广播给界面。
    /// 任一步失败都会记录中文原因并返回 <c>false</c>，由设置页提示用户。
    /// </summary>
    public bool ApplySettings(AppSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            if (!_demo)
            {
                SettingsStore.Save(next);
            }

            Settings = next;

            try
            {
                Engine.UpdateSettings(next);
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Warn, "引擎热更新设置失败", ex);
            }

            try
            {
                _analytics?.UpdateSettings(next);
                _insights?.Trim(next.DiskRetentionDays);
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Warn, "分析模块热更新设置失败", ex);
            }

            try
            {
                History.OnSettingsChanged(next);
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Warn, "历史模块热更新设置失败", ex);
            }

            if (!_demo && !StartupRegistration.TryApply(next.RunAtStartup, out string? error))
            {
                SafeLog(LogLevel.Warn, error ?? "同步开机启动失败");
            }

            SettingsChanged?.Invoke(this, next);
            return true;
        }
        catch (Exception ex)
        {
            LastError = "保存设置失败：" + ex.Message;
            SafeLog(LogLevel.Error, "保存设置失败", ex);
            return false;
        }
    }

    /// <summary>分析数据目录（report.md / report.json 所在位置）。演示 / 截图模式返回虚构路径。</summary>
    public string AnalysisDirectory => _demo
        ? @"C:\Users\you\AppData\Local\PerfScope\analysis\"
        : Path.Combine(AppPaths.DataDirectory, "analysis");

    /// <summary>构建今天的分析报告对象（不落盘）。</summary>
    public InsightReport? BuildReport()
    {
        if (_demo || _insights is null)
        {
            return null;
        }

        try
        {
            return ReportBuilder.Build(DateOnly.FromDateTime(DateTime.Now), History, _insights);
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "生成分析报告失败", ex);
            return null;
        }
    }

    /// <summary>立即生成一份今天的分析报告（Markdown + JSON），返回 markdown 路径。</summary>
    public string? WriteAnalysisReport()
    {
        var report = BuildReport();
        if (report is null)
        {
            return null;
        }

        try
        {
            var (markdownPath, jsonPath) = ReportBuilder.Write(report, AppPaths.DataDirectory);
            SafeLog(LogLevel.Info, "分析报告已更新：" + markdownPath + " / " + jsonPath);
            return markdownPath;
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "写入分析报告失败", ex);
            return null;
        }
    }

    /// <summary>生成最近 7 天 vs 上一个 7 天的周报（Markdown + JSON），返回 markdown 路径。</summary>
    public string? WriteWeeklyReport()
    {
        try
        {
            var report = WeeklyReportBuilder.Build(DateOnly.FromDateTime(DateTime.Now), History);
            var (markdownPath, jsonPath) = WeeklyReportBuilder.Write(report, AppPaths.DataDirectory);
            SafeLog(LogLevel.Info, "周报已更新：" + markdownPath + " / " + jsonPath);
            return markdownPath;
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "生成周报失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 生成「接入 Agent」用的 Prompt：刷新今天的报告后，把真实路径、数据口径与已算好的结论一并写进去。
    /// </summary>
    public string BuildAgentPrompt()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (_demo)
        {
            // 演示 / 截图模式：不读取真实数据，给一份虚构但结构完整的 Prompt，
            // 保证对外材料里不出现任何真实路径或应用名。
            var sample = new AgentPromptContext(
                today,
                @"C:\Users\you\AppData\Local\PerfScope\analysis\" + today.ToString("yyyy-MM-dd") + "-report.json",
                @"C:\Users\you\AppData\Local\PerfScope\analysis\" + today.ToString("yyyy-MM-dd") + "-report.md",
                @"C:\Users\you\AppData\Local\PerfScope\insights\" + today.ToString("yyyy-MM-dd") + ".jsonl",
                @"C:\Users\you\AppData\Local\PerfScope\analysis\",
                16,
                42,
                8,
                2.0,
                32.0,
                new[]
                {
                    "CPU 消耗最多：**Aurora Browser**，累计 692 秒（平均 6.4%）。",
                    "内存峰值最高：**Terra Game**，峰值 5200 MB。",
                    "共 9 段 CPU 高占用片段，最重的一段来自 **Studio Suite**（14:22 起，持续 6.5 分钟，均值 8.1%）。",
                });
            return AgentPromptBuilder.Build(sample);
        }
        var report = BuildReport();
        if (report is not null)
        {
            try
            {
                ReportBuilder.Write(report, AppPaths.DataDirectory);
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Warn, "写入分析报告失败", ex);
            }
        }

        string suffix = today.ToString("yyyy-MM-dd");
        var events = report?.Events ?? Array.Empty<InsightEvent>();
        var context = new AgentPromptContext(
            today,
            Path.Combine(AnalysisDirectory, suffix + "-report.json"),
            Path.Combine(AnalysisDirectory, suffix + "-report.md"),
            Path.Combine(AppPaths.DataDirectory, "insights", suffix + ".jsonl"),
            AnalysisDirectory,
            report?.Apps.Count ?? 0,
            events.Count,
            events.Count(e => e.Kind == InsightKinds.Digest),
            report?.System.SampleIntervalSeconds ?? 0,
            (report?.System.TotalPhysicalMb ?? 0) / 1024.0,
            report?.Findings ?? Array.Empty<string>());

        return AgentPromptBuilder.Build(context);
    }

    /// <summary>写日志（不让日志自身异常影响主流程）。</summary>
    public void SafeLog(LogLevel level, string message, Exception? exception = null)
    {
        try
        {
            Log.Write(level, message, exception);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void OnFrameProduced(object? sender, SnapshotFrame frame)
    {
        if (frame is null)
        {
            return;
        }

        try
        {
            History.Append(frame);
        }
        catch (Exception ex)
        {
            // 历史写失败不能影响采样线程
            SafeLog(LogLevel.Warn, "写入历史失败", ex);
        }

        try
        {
            _analytics?.Feed(frame);
            var events = _analytics?.DrainEvents();
            if (events is { Count: > 0 })
            {
                _insights?.Append(events);

                // 有 digest 时顺手刷新报告（最多每 5 分钟一次，避免频繁写盘）
                if (events.Any(e => e.Kind == InsightKinds.Digest) &&
                    (DateTime.UtcNow - _lastReportUtc).TotalMinutes >= 4)
                {
                    _lastReportUtc = DateTime.UtcNow;
                    WriteAnalysisReport();
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "分析数据写入失败", ex);
        }
    }

    /// <summary>停止采样并释放全部服务。</summary>
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
            _analytics?.Complete();
            var remaining = _analytics?.DrainEvents();
            if (remaining is { Count: > 0 })
            {
                _insights?.Append(remaining);
            }

            WriteAnalysisReport();
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "收尾分析数据失败", ex);
        }

        try
        {
            Engine.Stop();
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "停止采样失败", ex);
        }

        try
        {
            Engine.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "释放采样引擎失败", ex);
        }

        try
        {
            _insights?.Flush();
            _insights?.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "释放分析存储失败", ex);
        }

        try
        {
            History.Flush();
            History.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Warn, "释放历史存储失败", ex);
        }

        SafeLog(LogLevel.Info, "PerfScope 已退出");

        try
        {
            Log.Flush();
        }
        catch
        {
            // 忽略
        }
    }
}