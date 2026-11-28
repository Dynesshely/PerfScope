using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Demo;

/// <summary>
/// 演示用历史存储：只在内存里保留最近若干帧，发牌给趋势曲线与「历史」页，
/// 完全不走磁盘，保证演示不污染真实数据目录。
/// </summary>
internal sealed class DemoHistory : IHistoryStore
{
    private const int MaxFrames = 900;

    private readonly object _sync = new();
    private readonly List<SnapshotFrame> _frames = new();
    private AppSettings _settings;
    private bool _disposed;

    /// <summary>创建演示历史存储。</summary>
    public DemoHistory(AppSettings settings)
    {
        _settings = settings.Clone();
    }

    /// <inheritdoc />
    public void Append(SnapshotFrame frame)
    {
        if (_disposed || frame is null)
        {
            return;
        }

        lock (_sync)
        {
            _frames.Add(frame);
            TrimCore();
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        // 演示模式不落盘
    }

    /// <inheritdoc />
    public IReadOnlyList<TrendPoint> GetTrend(string appKey, int maxPoints)
    {
        if (string.IsNullOrEmpty(appKey) || maxPoints <= 0)
        {
            return Array.Empty<TrendPoint>();
        }

        var points = new List<TrendPoint>(Math.Min(maxPoints, 128));
        lock (_sync)
        {
            for (int i = _frames.Count - 1; i >= 0 && points.Count < maxPoints; i--)
            {
                var frame = _frames[i];
                foreach (var app in frame.Apps)
                {
                    if (!string.Equals(app.Key, appKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    points.Add(new TrendPoint(frame.TimestampUtc, app.CpuPercent, app.WorkingSetBytes));
                    break;
                }
            }
        }

        points.Reverse();
        return points;
    }

    /// <inheritdoc />
    public IReadOnlyList<DailyAppSummary> QueryDay(DateOnly date)
    {
        var accumulator = new Dictionary<string, DailyAccumulator>(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var frame in _frames)
            {
                if (DateOnly.FromDateTime(frame.TimestampUtc.ToLocalTime()) != date)
                {
                    continue;
                }

                double seconds = Math.Max(0.001, frame.Interval.TotalSeconds);
                foreach (var app in frame.Apps)
                {
                    if (!accumulator.TryGetValue(app.Key, out var acc))
                    {
                        acc = new DailyAccumulator(app.DisplayName);
                        accumulator[app.Key] = acc;
                    }

                    acc.CpuPercentSeconds += app.CpuPercent * seconds;
                    acc.PeakCpuPercent = Math.Max(acc.PeakCpuPercent, app.CpuPercent);
                    acc.PeakWorkingSetBytes = Math.Max(acc.PeakWorkingSetBytes, app.WorkingSetBytes);
                    acc.SampleCount++;
                }
            }
        }

        var result = new List<DailyAppSummary>(accumulator.Count);
        foreach (var pair in accumulator)
        {
            result.Add(new DailyAppSummary(
                date,
                pair.Key,
                pair.Value.DisplayName,
                Math.Round(pair.Value.CpuPercentSeconds, 1),
                Math.Round(pair.Value.PeakCpuPercent, 2),
                pair.Value.PeakWorkingSetBytes,
                pair.Value.SampleCount));
        }

        result.Sort(static (a, b) => b.CpuPercentSeconds.CompareTo(a.CpuPercentSeconds));
        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> GetAvailableDays()
    {
        var days = new SortedSet<DateOnly>();
        lock (_sync)
        {
            foreach (var frame in _frames)
            {
                days.Add(DateOnly.FromDateTime(frame.TimestampUtc.ToLocalTime()));
            }
        }

        return days.ToList();
    }

    /// <inheritdoc />
    public void Trim()
    {
        lock (_sync)
        {
            TrimCore();
        }
    }

    /// <inheritdoc />
    public void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        lock (_sync)
        {
            _frames.Clear();
        }
    }

    private void TrimCore()
    {
        while (_frames.Count > MaxFrames)
        {
            _frames.RemoveAt(0);
        }

        int retentionMinutes = Math.Clamp(_settings.MemoryRetentionMinutes, 5, 1440);
        DateTime oldest = DateTime.UtcNow.AddMinutes(-retentionMinutes);
        int drop = 0;
        while (drop < _frames.Count && _frames[drop].TimestampUtc < oldest)
        {
            drop++;
        }

        if (drop > 0)
        {
            _frames.RemoveRange(0, drop);
        }
    }

    private sealed class DailyAccumulator
    {
        public DailyAccumulator(string displayName)
        {
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public double CpuPercentSeconds { get; set; }

        public double PeakCpuPercent { get; set; }

        public long PeakWorkingSetBytes { get; set; }

        public int SampleCount { get; set; }
    }
}
