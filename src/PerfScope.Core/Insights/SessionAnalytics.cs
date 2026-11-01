using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Insights;

/// <summary>
/// 会话级分析引擎：不保存原始采样，而是把「连续的 2 秒数据」压成对人、对 AI 都有意义的结论——
/// CPU 高占用片段、内存持续增长、整机高负载/内存压力片段，以及固定窗口的聚合摘要。
///
/// 设计要点：
/// * 只在内存里维护固定容量的状态（应用数上限 512、内存窗口 60 点），长时间运行不会膨胀；
/// * 计算量 O(应用数)，每帧几十微秒级别；
/// * 事件一旦产生就交给上层落盘（JSONL 追加），崩溃最多丢最后一个未闭合片段。
/// </summary>
public sealed class SessionAnalytics
{
    /// <summary>最多同时跟踪多少个应用（超出后丢弃最久未见者）。</summary>
    public const int MaxTrackedApps = 512;

    private const int MemoryWindow = 60;
    private const int RecentEventLimit = 200;

    /// <summary>单个片段最长持续时间：超过就切段输出，保证长时间持续高负载/增长也能持续产出记录。</summary>
    private const double MaxEpisodeSeconds = 900;

    /// <summary>同一应用两次 CPU 片段之间的冷却时间（秒），避免在阈值附近抖动时刷屏。</summary>
    private const double EpisodeCooldownSeconds = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, AppState> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DigestAccumulator> _digestApps = new(StringComparer.Ordinal);
    private readonly List<InsightEvent> _pending = new();
    private readonly List<InsightEvent> _recent = new();

    private InsightThresholds _thresholds;
    private DateTime _sessionStartUtc;
    private DateTime _lastFrameUtc;
    private double _intervalSeconds = 2;
    private int _frames;

    // 系统级累计
    private double _sysCpuSum;
    private double _sysCpuPeak;
    private double _sysMemSum;
    private long _minAvailable = long.MaxValue;
    private Episode _systemCpuEpisode;
    private Episode _systemMemEpisode;

    // digest 窗口
    private DateTime _digestStartUtc;
    private double _digestSysCpuSum;
    private double _digestSysCpuPeak;
    private double _digestSysMemSum;
    private int _digestFrames;
    private long _digestMinAvailable = long.MaxValue;

    /// <summary>创建分析引擎，阈值取自设置。</summary>
    public SessionAnalytics(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _thresholds = InsightThresholds.FromSettings(settings);
    }

    /// <summary>设置热更新（阈值变化下一次采样生效）。</summary>
    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _thresholds = InsightThresholds.FromSettings(settings);
        }
    }

    /// <summary>喂入一帧采样。线程安全，由采样线程调用。</summary>
    public void Feed(SnapshotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (!_thresholds.Enabled)
            {
                return;
            }

            if (_sessionStartUtc == default)
            {
                _sessionStartUtc = frame.TimestampUtc;
                _digestStartUtc = frame.TimestampUtc;
                Push(new InsightEvent
                {
                    Kind = InsightKinds.SessionStart,
                    TimestampUtc = frame.TimestampUtc,
                    SampleCount = 0,
                    Metrics = new Dictionary<string, double>
                    {
                        ["sampleIntervalSeconds"] = Math.Round(frame.Interval.TotalSeconds, 3),
                        ["totalPhysicalMb"] = Math.Round(frame.TotalPhysicalBytes / 1048576.0, 1),
                        ["logicalProcessors"] = Environment.ProcessorCount,
                    },
                });
            }

            double interval = frame.Interval.TotalSeconds;
            _intervalSeconds = interval > 0.1 && interval < 120 ? interval : _intervalSeconds;
            _lastFrameUtc = frame.TimestampUtc;
            _frames++;

            _sysCpuSum += frame.SystemCpuPercent;
            _sysCpuPeak = Math.Max(_sysCpuPeak, frame.SystemCpuPercent);
            _sysMemSum += frame.SystemMemoryPercent;
            _minAvailable = Math.Min(_minAvailable, frame.AvailablePhysicalBytes);

            _digestSysCpuSum += frame.SystemCpuPercent;
            _digestSysCpuPeak = Math.Max(_digestSysCpuPeak, frame.SystemCpuPercent);
            _digestSysMemSum += frame.SystemMemoryPercent;
            _digestMinAvailable = Math.Min(_digestMinAvailable, frame.AvailablePhysicalBytes);
            _digestFrames++;

            UpdateSystemEpisodes(frame);
            UpdateApps(frame);
            UpdateDigest(frame);
        }
    }

    /// <summary>取走待落盘事件（调用方负责写入存储）。</summary>
    public IReadOnlyList<InsightEvent> DrainEvents()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return Array.Empty<InsightEvent>();
            }

            var copy = _pending.ToArray();
            _pending.Clear();
            return copy;
        }
    }

    /// <summary>当前分析快照（UI 用）。</summary>
    public InsightSnapshot Snapshot()
    {
        lock (_gate)
        {
            int count = Math.Max(1, _frames);
            return new InsightSnapshot(
                _sessionStartUtc,
                _frames,
                _sysCpuSum / count,
                _sysCpuPeak,
                _sysMemSum / count,
                _minAvailable == long.MaxValue ? 0 : _minAvailable,
                _recent.ToArray(),
                _pending.Count);
        }
    }

    /// <summary>会话结束时把还没闭合的片段收尾（避免丢掉最后一段）。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            foreach (var state in _apps.Values)
            {
                CloseCpuEpisode(state, _lastFrameUtc, force: true);
                CloseGrowthEpisode(state, _lastFrameUtc, force: true);
            }

            CloseSystemEpisode(ref _systemCpuEpisode, InsightKinds.SystemCpuEpisode, _lastFrameUtc, force: true);
            CloseSystemEpisode(ref _systemMemEpisode, InsightKinds.SystemMemoryPressure, _lastFrameUtc, force: true);
            EmitDigest(_lastFrameUtc);
        }
    }

    // ================= 系统级 =================

    private void UpdateSystemEpisodes(SnapshotFrame frame)
    {
        bool highCpu = frame.SystemCpuPercent >= _thresholds.SystemCpuPercent;
        if (highCpu)
        {
            AddToEpisode(ref _systemCpuEpisode, frame.TimestampUtc, frame.SystemCpuPercent, 0, 0);
        }
        else
        {
            CloseSystemEpisode(ref _systemCpuEpisode, InsightKinds.SystemCpuEpisode, frame.TimestampUtc, force: false);
        }

        double availablePercent = frame.TotalPhysicalBytes > 0
            ? frame.AvailablePhysicalBytes * 100.0 / frame.TotalPhysicalBytes
            : 100;
        bool lowMemory = availablePercent <= _thresholds.LowMemoryAvailablePercent;
        if (lowMemory)
        {
            AddToEpisode(ref _systemMemEpisode, frame.TimestampUtc, frame.SystemCpuPercent, availablePercent, 0);
        }
        else
        {
            CloseSystemEpisode(ref _systemMemEpisode, InsightKinds.SystemMemoryPressure, frame.TimestampUtc, force: false);
        }
    }

    private void CloseSystemEpisode(ref Episode episode, string kind, DateTime nowUtc, bool force)
    {
        if (!episode.Active)
        {
            return;
        }

        double seconds = (nowUtc - episode.StartUtc).TotalSeconds;
        if (!force && seconds < _thresholds.SystemEpisodeSeconds)
        {
            return;
        }

        if (seconds >= _thresholds.SystemEpisodeSeconds)
        {
            double avgCpu = episode.Samples > 0 ? episode.SumCpu / episode.Samples : 0;
            Push(new InsightEvent
            {
                Kind = kind,
                TimestampUtc = nowUtc,
                StartUtc = episode.StartUtc,
                EndUtc = nowUtc,
                DurationSeconds = Math.Round(seconds, 1),
                AvgCpuPercent = Math.Round(avgCpu, 2),
                PeakCpuPercent = Math.Round(episode.PeakCpu, 2),
                SampleCount = episode.Samples,
                Note = kind == InsightKinds.SystemCpuEpisode
                    ? "整机 CPU 持续高于阈值，建议看看 Top 应用是不是某个应用在满载"
                    : "可用内存持续低于阈值，注意是否存在内存泄漏或缓存膨胀",
            });
        }

        episode = default;
    }

    // ================= 应用级 =================

    private void UpdateApps(SnapshotFrame frame)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in frame.Apps)
        {
            if (app is null || string.IsNullOrEmpty(app.Key))
            {
                continue;
            }

            seen.Add(app.Key);
            if (!_apps.TryGetValue(app.Key, out var state))
            {
                if (_apps.Count >= MaxTrackedApps)
                {
                    PruneOldestApp();
                }

                state = new AppState(app.Key, app.DisplayName);
                _apps[app.Key] = state;
            }

            state.Name = app.DisplayName;
            state.LastSeenUtc = frame.TimestampUtc;

            // CPU 高占用片段（带 30 秒冷却，避免在阈值附近来回抖动时产生一堆碎片）
            if (app.CpuPercent >= _thresholds.AppCpuPercent &&
                (state.CpuEpisode.Active ||
                 (frame.TimestampUtc - state.LastCpuEpisodeEndUtc).TotalSeconds >= EpisodeCooldownSeconds))
            {
                AddToEpisode(ref state.CpuEpisode, frame.TimestampUtc, app.CpuPercent, app.WorkingSetBytes / 1048576.0, app.ProcessCount);
            }
            else
            {
                CloseCpuEpisode(state, frame.TimestampUtc, force: false);
            }

            // 内存趋势
            AppendMemorySample(state, frame.TimestampUtc, app.WorkingSetBytes / 1048576.0);
            UpdateGrowthEpisode(state, frame.TimestampUtc, app.ProcessCount);

            // digest 累计
            if (!_digestApps.TryGetValue(app.Key, out var acc))
            {
                acc = new DigestAccumulator(app.DisplayName);
                _digestApps[app.Key] = acc;
            }

            acc.Add(app.CpuPercent, app.WorkingSetBytes / 1048576.0);
        }

        // 消失的应用：闭合片段并加宽清空
        foreach (var pair in _apps)
        {
            if (seen.Contains(pair.Key))
            {
                continue;
            }

            var state = pair.Value;
            if ((frame.TimestampUtc - state.LastSeenUtc).TotalSeconds > 30)
            {
                CloseCpuEpisode(state, frame.TimestampUtc, force: true);
                CloseGrowthEpisode(state, frame.TimestampUtc, force: true);
                state.ExitPending = true;
            }
        }

        if (_apps.Count > MaxTrackedApps / 2)
        {
            PruneExitedApps();
        }
    }

    private void AddToEpisode(ref Episode episode, DateTime nowUtc, double cpu, double memoryMb, int processes)
    {
        if (!episode.Active)
        {
            episode = new Episode { Active = true, StartUtc = nowUtc };
        }
        else if ((nowUtc - episode.StartUtc).TotalSeconds >= MaxEpisodeSeconds)
        {
            // 持续高负载超过 15 分钟：先把这一段落下来，再接着统计下一段
            var snapshot = episode;
            episode = new Episode { Active = true, StartUtc = nowUtc };
            CloseEpisodeSegment(ref snapshot, nowUtc);
        }

        episode.Samples++;
        episode.SumCpu += cpu;
        episode.PeakCpu = Math.Max(episode.PeakCpu, cpu);
        episode.SumMemory += memoryMb;
        episode.PeakMemory = Math.Max(episode.PeakMemory, memoryMb);
        episode.PeakProcesses = Math.Max(episode.PeakProcesses, processes);
    }

    private void CloseCpuEpisode(AppState state, DateTime nowUtc, bool force)
    {
        ref var episode = ref state.CpuEpisode;
        if (!episode.Active)
        {
            return;
        }

        double seconds = (nowUtc - episode.StartUtc).TotalSeconds;
        if (!force && seconds < _thresholds.AppCpuEpisodeSeconds)
        {
            return;
        }

        if (seconds >= _thresholds.AppCpuEpisodeSeconds)
        {
            double avg = episode.Samples > 0 ? episode.SumCpu / episode.Samples : 0;
            Push(new InsightEvent
            {
                Kind = InsightKinds.AppCpuEpisode,
                TimestampUtc = nowUtc,
                AppKey = state.Key,
                AppName = state.Name,
                StartUtc = episode.StartUtc,
                EndUtc = nowUtc,
                DurationSeconds = Math.Round(seconds, 1),
                AvgCpuPercent = Math.Round(avg, 2),
                PeakCpuPercent = Math.Round(episode.PeakCpu, 2),
                AvgMemoryMb = Math.Round(episode.Samples > 0 ? episode.SumMemory / episode.Samples : 0, 1),
                PeakMemoryMb = Math.Round(episode.PeakMemory, 1),
                ProcessCount = episode.PeakProcesses,
                SampleCount = episode.Samples,
                Note = "该应用持续占用较高 CPU，可结合时间点判断是前台交互还是后台任务",
            });
        }

        episode = default;
        state.LastCpuEpisodeEndUtc = nowUtc;
    }

    private void CloseEpisodeSegment(ref Episode episode, DateTime nowUtc)
    {
        double seconds = (nowUtc - episode.StartUtc).TotalSeconds;
        if (seconds < 60 || episode.Samples <= 0)
        {
            return;
        }

        Push(new InsightEvent
        {
            Kind = InsightKinds.SystemCpuEpisode,
            TimestampUtc = nowUtc,
            StartUtc = episode.StartUtc,
            EndUtc = nowUtc,
            DurationSeconds = Math.Round(seconds, 1),
            AvgCpuPercent = Math.Round(episode.SumCpu / episode.Samples, 2),
            PeakCpuPercent = Math.Round(episode.PeakCpu, 2),
            SampleCount = episode.Samples,
            Note = "长时间持续高负载，已按 15 分钟分段记录",
        });
    }

    private static void AppendMemorySample(AppState state, DateTime nowUtc, double memoryMb)
    {
        int index = state.MemoryHead;
        state.MemoryMb[index] = memoryMb;
        state.MemorySeconds[index] = (nowUtc - state.FirstSeenUtc).TotalSeconds;
        state.MemoryHead = (index + 1) % MemoryWindow;
        state.MemoryCount = Math.Min(state.MemoryCount + 1, MemoryWindow);
    }

    private void UpdateGrowthEpisode(AppState state, DateTime nowUtc, int processes)
    {
        if (state.MemoryCount < 12)
        {
            return;
        }

        double slope = ComputeSlope(state) * 60.0; // MB/分钟
        bool growing = slope >= _thresholds.MemoryGrowthMbPerMinute;

        if (growing)
        {
            if (!state.GrowthActive)
            {
                state.GrowthActive = true;
                state.GrowthStartUtc = nowUtc;
                state.GrowthStartMb = state.MemoryMb[(state.MemoryHead + MemoryWindow - 1) % MemoryWindow];
                state.GrowthPeakMb = state.GrowthStartMb;
            }

            double currentMb = state.MemoryMb[(state.MemoryHead + MemoryWindow - 1) % MemoryWindow];
            state.GrowthPeakMb = Math.Max(state.GrowthPeakMb, currentMb);
            state.GrowthSlope = slope;
            state.GrowthPeakProcesses = Math.Max(state.GrowthPeakProcesses, processes);

            // 持续增长超过 15 分钟：切段输出，随后重新计时
            if ((nowUtc - state.GrowthStartUtc).TotalSeconds >= MaxEpisodeSeconds)
            {
                CloseGrowthEpisode(state, nowUtc, force: true);
                state.GrowthActive = true;
                state.GrowthStartUtc = nowUtc;
                state.GrowthStartMb = currentMb;
                state.GrowthPeakMb = currentMb;
                state.GrowthSlope = slope;
                state.GrowthPeakProcesses = processes;
            }
        }
        else if (state.GrowthActive)
        {
            CloseGrowthEpisode(state, nowUtc, force: false);
        }
    }

    private void CloseGrowthEpisode(AppState state, DateTime nowUtc, bool force)
    {
        if (!state.GrowthActive)
        {
            return;
        }

        double seconds = (nowUtc - state.GrowthStartUtc).TotalSeconds;
        double growth = state.GrowthPeakMb - state.GrowthStartMb;
        bool meaningful = seconds >= 120 && growth >= 150;

        if (meaningful)
        {
            Push(new InsightEvent
            {
                Kind = InsightKinds.AppMemoryGrowth,
                TimestampUtc = nowUtc,
                AppKey = state.Key,
                AppName = state.Name,
                StartUtc = state.GrowthStartUtc,
                EndUtc = nowUtc,
                DurationSeconds = Math.Round(seconds, 1),
                AvgMemoryMb = Math.Round((state.GrowthStartMb + state.GrowthPeakMb) / 2, 1),
                PeakMemoryMb = Math.Round(state.GrowthPeakMb, 1),
                GrowthMbPerMinute = Math.Round(state.GrowthSlope, 1),
                ProcessCount = state.GrowthPeakProcesses,
                SampleCount = state.MemoryCount,
                Note = "内存持续增长，可能是缓存累积或泄漏；可对比同时段是否有大量文件/网络操作",
            });
        }

        state.GrowthActive = false;
        state.GrowthSlope = 0;
        state.GrowthPeakProcesses = 0;
    }

    /// <summary>对最近的内存采样做最小二乘拟合，返回 MB/秒。</summary>
    private static double ComputeSlope(AppState state)
    {
        int n = state.MemoryCount;
        if (n < 2)
        {
            return 0;
        }

        double sumX = 0, sumY = 0, sumXx = 0, sumXy = 0;
        for (int i = 0; i < n; i++)
        {
            int index = (state.MemoryHead + MemoryWindow - n + i) % MemoryWindow;
            double x = state.MemorySeconds[index];
            double y = state.MemoryMb[index];
            sumX += x;
            sumY += y;
            sumXx += x * x;
            sumXy += x * y;
        }

        double denominator = n * sumXx - sumX * sumX;
        if (Math.Abs(denominator) < 1e-6)
        {
            return 0;
        }

        return (n * sumXy - sumX * sumY) / denominator;
    }

    // ================= Digest =================

    private void UpdateDigest(SnapshotFrame frame)
    {
        if ((frame.TimestampUtc - _digestStartUtc).TotalMinutes < _thresholds.DigestMinutes)
        {
            return;
        }

        EmitDigest(frame.TimestampUtc);
    }

    private void EmitDigest(DateTime nowUtc)
    {
        if (_digestFrames <= 0)
        {
            _digestStartUtc = nowUtc;
            return;
        }

        var top = _digestApps
            .Select(pair => new { pair.Key, Acc = pair.Value })
            .OrderByDescending(item => item.Acc.CpuSum)
            .Take(15)
            .Select(item => new InsightAppLine(
                item.Key,
                item.Acc.Name,
                Math.Round(item.Acc.CpuSum / Math.Max(1, item.Acc.Samples), 2),
                Math.Round(item.Acc.CpuPeak, 2),
                Math.Round(item.Acc.MemorySum / Math.Max(1, item.Acc.Samples), 1),
                Math.Round(item.Acc.MemoryPeak, 1),
                item.Acc.Samples))
            .ToArray();

        Push(new InsightEvent
        {
            Kind = InsightKinds.Digest,
            TimestampUtc = nowUtc,
            StartUtc = _digestStartUtc,
            EndUtc = nowUtc,
            DurationSeconds = Math.Round((nowUtc - _digestStartUtc).TotalSeconds, 1),
            SampleCount = _digestFrames,
            AvgCpuPercent = Math.Round(_digestSysCpuSum / _digestFrames, 2),
            PeakCpuPercent = Math.Round(_digestSysCpuPeak, 2),
            AvgMemoryMb = Math.Round(_digestSysMemSum / _digestFrames, 1),
            Apps = top,
            Metrics = new Dictionary<string, double>
            {
                ["systemCpuAvg"] = Math.Round(_digestSysCpuSum / _digestFrames, 2),
                ["systemCpuPeak"] = Math.Round(_digestSysCpuPeak, 2),
                ["systemMemoryAvgPercent"] = Math.Round(_digestSysMemSum / _digestFrames, 2),
                ["minAvailableMb"] = Math.Round((_digestMinAvailable == long.MaxValue ? 0 : _digestMinAvailable) / 1048576.0, 1),
            },
        });

        _digestApps.Clear();
        _digestSysCpuSum = 0;
        _digestSysCpuPeak = 0;
        _digestSysMemSum = 0;
        _digestMinAvailable = long.MaxValue;
        _digestFrames = 0;
        _digestStartUtc = nowUtc;
    }

    // ================= 内部工具 =================

    private void Push(InsightEvent insight)
    {
        _pending.Add(insight);
        _recent.Add(insight);
        if (_recent.Count > RecentEventLimit)
        {
            _recent.RemoveRange(0, _recent.Count - RecentEventLimit);
        }
    }

    private void PruneOldestApp()
    {
        string? oldestKey = null;
        DateTime oldest = DateTime.MaxValue;
        foreach (var pair in _apps)
        {
            if (pair.Value.LastSeenUtc < oldest)
            {
                oldest = pair.Value.LastSeenUtc;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is not null)
        {
            _apps.Remove(oldestKey);
        }
    }

    private void PruneExitedApps()
    {
        var remove = new List<string>(16);
        foreach (var pair in _apps)
        {
            if (pair.Value.ExitPending && (_lastFrameUtc - pair.Value.LastSeenUtc).TotalSeconds > 120)
            {
                remove.Add(pair.Key);
            }
        }

        foreach (var key in remove)
        {
            _apps.Remove(key);
        }
    }

    private struct Episode
    {
        public bool Active;
        public DateTime StartUtc;
        public int Samples;
        public double SumCpu;
        public double PeakCpu;
        public double SumMemory;
        public double PeakMemory;
        public int PeakProcesses;
    }

    private sealed class AppState
    {
        public AppState(string key, string name)
        {
            Key = key;
            Name = name;
            FirstSeenUtc = DateTime.Now;
        }

        public string Key { get; }
        public string Name { get; set; }
        public DateTime FirstSeenUtc { get; }
        public DateTime LastSeenUtc { get; set; }
        public Episode CpuEpisode;
        public DateTime LastCpuEpisodeEndUtc;
        public bool ExitPending;
        public bool GrowthActive;
        public DateTime GrowthStartUtc;
        public double GrowthStartMb;
        public double GrowthPeakMb;
        public double GrowthSlope;
        public int GrowthPeakProcesses;
        public readonly double[] MemoryMb = new double[MemoryWindow];
        public readonly double[] MemorySeconds = new double[MemoryWindow];
        public int MemoryCount;
        public int MemoryHead;
    }

    private sealed class DigestAccumulator
    {
        public DigestAccumulator(string name) => Name = name;

        public string Name { get; }
        public double CpuSum;
        public double CpuPeak;
        public double MemorySum;
        public double MemoryPeak;
        public int Samples;

        public void Add(double cpu, double memoryMb)
        {
            CpuSum += cpu;
            CpuPeak = Math.Max(CpuPeak, cpu);
            MemorySum += memoryMb;
            MemoryPeak = Math.Max(MemoryPeak, memoryMb);
            Samples++;
        }
    }
}

/// <summary>分析阈值（从设置里读，改设置即时生效）。</summary>
internal sealed record InsightThresholds(
    bool Enabled,
    double AppCpuPercent,
    double AppCpuEpisodeSeconds,
    double MemoryGrowthMbPerMinute,
    double SystemCpuPercent,
    double SystemEpisodeSeconds,
    double LowMemoryAvailablePercent,
    double DigestMinutes)
{
    public static InsightThresholds FromSettings(AppSettings settings) => new(
        settings.InsightEnabled,
        settings.InsightAppCpuPercent,
        settings.InsightAppCpuSeconds,
        settings.InsightMemoryGrowthMbPerMinute,
        settings.InsightSystemCpuPercent,
        settings.InsightSystemCpuSeconds,
        settings.InsightLowMemoryPercent,
        settings.InsightDigestMinutes);
}