using System.Globalization;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Core.History;

/// <summary>
/// 历史与趋势存储实现。
/// <para>
/// 内存层：为最近进入 CPU Top <see cref="AppSettings.TrendAppSlots"/> 的应用各维护一个定长环形缓冲，
/// 容量 = <c>MemoryRetentionMinutes × 60000 / SampleIntervalMs</c>（上限 86400 点），设置变更时原地重建。
/// 掉出榜单的应用缓冲保留到被新应用挤出（按最近写入时间淘汰），因此内存占用恒定。
/// </para>
/// <para>
/// 磁盘层：<c>history\days\yyyy-MM-dd.bin</c>，每条记录固定 32 字节；<c>history\apps.json</c> 是
/// appKey → 整数 ID 的目录表。采样线程只做「分钟聚合」并把完成的分钟记录投递到内部队列，
/// 真正的磁盘写入由后台线程负责（跨分钟或每 30 秒一次），因此 <see cref="Append"/> 不会超过 1 毫秒。
/// </para>
/// <para>
/// 崩溃容忍：读取时忽略尾部不足 32 字节的残片与非法记录；同一应用同一分钟的重复记录（重启导致）
/// 按最大值合并，不会重复计入时长。文件被占用时重试一次，仍失败则降级跳过并记日志。
/// </para>
/// </summary>
public sealed class HistoryStore : IHistoryStore
{
    /// <summary>单个应用趋势缓冲的点数上限（一天 86400 秒）。</summary>
    private const int MaxTrendCapacity = 86400;

    /// <summary>后台落盘线程的固定唤醒间隔。</summary>
    private const int WriterIntervalMs = 30000;

    /// <summary>待写记录达到该数量时立即唤醒落盘线程，避免内存堆积。</summary>
    private const int PendingWriteThreshold = 32;

    /// <summary>小于该 CPU 百分比且工作集也很小的应用不写磁盘（明显空闲，记录无意义）。</summary>
    private const double MinRecordedCpuPercent = 0.05;

    /// <summary>工作集低于该值时视为「小且空闲」，不写磁盘。</summary>
    private const long MinRecordedWorkingSetBytes = 32L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly object _writeGate = new();
    private readonly ILog? _log;
    private readonly string _rootDirectory;
    private readonly string _daysDirectory;
    private readonly AppIdCatalog _catalog;
    private readonly Thread _writerThread;
    private readonly ManualResetEventSlim _writerWake = new(false);

    private readonly List<PendingRecord> _pending = new();
    private readonly Dictionary<int, MinuteAccumulator> _minuteAccumulators = new();
    private readonly Dictionary<string, TrendRingBuffer> _trends = new(StringComparer.Ordinal);

    private AppSettings _settings;
    private DateOnly _activeDate = DateOnly.MinValue;
    private int _activeMinute = -1;
    private bool _diskEnabled;
    private FileStream? _dayStream;
    private DateOnly _dayStreamDate = DateOnly.MinValue;
    private volatile bool _disposed;
    private volatile bool _writerExit;

    /// <summary>
    /// 创建历史存储。
    /// </summary>
    /// <param name="settings">设置（内部会深拷贝，之后通过 <see cref="OnSettingsChanged"/> 更新）。</param>
    /// <param name="log">日志接口，可为 <c>null</c>。</param>
    /// <param name="rootDirectory">历史根目录；为 <c>null</c> 时使用 <see cref="AppPaths.HistoryDirectory"/>。</param>
    public HistoryStore(AppSettings settings, ILog? log = null, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _log = log;
        _settings = settings.Clone();
        var constructorFixes = SettingsValidator.Normalize(_settings);
        foreach (var fix in constructorFixes)
        {
            _log?.Info($"历史存储初始化时修正设置：{fix}");
        }

        _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? AppPaths.HistoryDirectory : rootDirectory);
        _daysDirectory = Path.Combine(_rootDirectory, "days");
        _catalog = new AppIdCatalog(Path.Combine(_rootDirectory, "apps.json"), log);
        _diskEnabled = _settings.EnableDiskHistory && _settings.DiskRetentionDays > 0;

        try
        {
            Directory.CreateDirectory(_daysDirectory);
        }
        catch (Exception ex)
        {
            _log?.Warn($"创建历史目录失败：{_daysDirectory}", ex);
        }

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "PerfScope.HistoryWriter",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();

        if (_diskEnabled)
        {
            Trim();
        }
    }

    /// <inheritdoc />
    public void Append(SnapshotFrame frame)
    {
        if (_disposed || frame is null || frame.Apps.Length == 0)
        {
            return;
        }

        var wake = false;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var settings = _settings;
            var localTime = frame.TimestampUtc.Kind == DateTimeKind.Local ? frame.TimestampUtc : frame.TimestampUtc.ToLocalTime();
            var date = DateOnly.FromDateTime(localTime);
            var minute = localTime.Hour * 60 + localTime.Minute;

            if (_activeMinute >= 0 && (date != _activeDate || minute != _activeMinute))
            {
                RetireActiveMinuteLocked();
            }

            _activeDate = date;
            _activeMinute = minute;

            AppendTrendsLocked(frame, settings);
            AppendMinuteAggregationLocked(frame, settings);

            wake = _pending.Count >= PendingWriteThreshold;
        }

        if (wake)
        {
            SignalWriter();
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_writeGate)
            {
                WritePendingCore();
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("刷新历史到磁盘失败（将在下一轮自动重试）。", ex);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TrendPoint> GetTrend(string appKey, int maxPoints)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return Array.Empty<TrendPoint>();
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return Array.Empty<TrendPoint>();
            }

            return _trends.TryGetValue(appKey, out var buffer)
                ? buffer.ToArray(maxPoints)
                : Array.Empty<TrendPoint>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DailyAppSummary> QueryDay(DateOnly date)
    {
        if (_disposed)
        {
            return Array.Empty<DailyAppSummary>();
        }

        var today = Today();

        // 当天：先把已完成分钟落盘，再合并仍在聚合中的当前分钟，UI 拿到的就是「准实时」数据。
        if (date == today)
        {
            Flush();
        }

        var path = DayPath(date);
        List<DayRecord> records;
        try
        {
            records = File.Exists(path) ? ReadDayRecords(path) : new List<DayRecord>(0);
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取历史文件失败：{path}", ex);
            records = new List<DayRecord>(0);
        }

        var liveRecords = date == today ? SnapshotActiveMinute() : Array.Empty<DayRecord>();
        if (records.Count == 0 && liveRecords.Length == 0)
        {
            return Array.Empty<DailyAppSummary>();
        }

        // 同一 (appId, 分钟) 去重：崩溃重启可能重复写同一分钟，取较大值避免重复计时长。
        var perMinute = new Dictionary<long, DayRecord>(records.Count + liveRecords.Length);
        foreach (var record in records)
        {
            MergeIntoPerMinute(perMinute, record);
        }

        foreach (var record in liveRecords)
        {
            MergeIntoPerMinute(perMinute, record);
        }

        var perApp = new Dictionary<int, AppAggregate>();
        foreach (var record in perMinute.Values)
        {
            if (!perApp.TryGetValue(record.AppId, out var aggregate))
            {
                aggregate = new AppAggregate();
                perApp[record.AppId] = aggregate;
            }

            aggregate.CpuPercentSeconds += record.CpuAvgPercent * 60.0;
            aggregate.PeakCpuPercent = Math.Max(aggregate.PeakCpuPercent, record.CpuMaxPercent);
            aggregate.PeakWorkingSetBytes = Math.Max(aggregate.PeakWorkingSetBytes, record.WorkingSetPeakBytes);
            aggregate.MinuteCount++;
        }

        var result = new List<DailyAppSummary>(perApp.Count);
        foreach (var (appId, aggregate) in perApp)
        {
            var key = "app#" + appId.ToString(CultureInfo.InvariantCulture);
            var displayName = "应用 #" + appId.ToString(CultureInfo.InvariantCulture);
            if (_catalog.TryGet(appId, out var catalogKey, out var catalogName))
            {
                key = catalogKey;
                if (!string.IsNullOrWhiteSpace(catalogName))
                {
                    displayName = catalogName;
                }
            }

            result.Add(new DailyAppSummary(
                date,
                key,
                displayName,
                aggregate.CpuPercentSeconds,
                aggregate.PeakCpuPercent,
                aggregate.PeakWorkingSetBytes,
                aggregate.MinuteCount));
        }

        result.Sort(static (left, right) => right.CpuPercentSeconds.CompareTo(left.CpuPercentSeconds));
        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> GetAvailableDays()
    {
        var days = new List<DateOnly>(32);

        try
        {
            if (!Directory.Exists(_daysDirectory))
            {
                return days;
            }

            foreach (var file in Directory.EnumerateFiles(_daysDirectory, "*.bin"))
            {
                if (DayRecord.TryParseFileName(Path.GetFileName(file), out var date))
                {
                    days.Add(date);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"枚举历史日期失败：{_daysDirectory}", ex);
        }

        days.Sort();
        return days;
    }

    /// <inheritdoc />
    public void Trim()
    {
        if (_disposed)
        {
            return;
        }

        int retention;
        DateOnly streamDate;
        lock (_sync)
        {
            retention = _settings.DiskRetentionDays;
            streamDate = _dayStreamDate;
        }

        try
        {
            if (!Directory.Exists(_daysDirectory))
            {
                return;
            }

            var today = Today();
            var keepDays = retention > 0 ? retention : 0;
            var threshold = keepDays > 0 ? today.AddDays(-(keepDays - 1)) : today.AddDays(1);
            var removed = 0;

            foreach (var file in Directory.EnumerateFiles(_daysDirectory, "*.bin"))
            {
                if (!DayRecord.TryParseFileName(Path.GetFileName(file), out var date))
                {
                    continue;
                }

                // 正在写入的当天文件绝不删除。
                if (date == streamDate)
                {
                    continue;
                }

                if (date >= threshold)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    _log?.Warn($"删除过期历史文件失败：{file}", ex);
                }
            }

            if (removed > 0)
            {
                _log?.Info($"已清理 {removed} 个过期历史文件（磁盘保留 {retention} 天）。");
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("清理过期历史文件失败。", ex);
        }
    }

    /// <inheritdoc />
    public void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
        {
            return;
        }

        var next = settings.Clone();
        SettingsValidator.Normalize(next);

        var nextDiskEnabled = next.EnableDiskHistory && next.DiskRetentionDays > 0;

        // 关闭落盘前先把当前分钟与待写记录刷掉，避免丢数据。
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_diskEnabled && !nextDiskEnabled)
            {
                RetireActiveMinuteLocked();
            }
        }

        if (_diskEnabled && !nextDiskEnabled)
        {
            Flush();
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var previous = _settings;
            var capacityChanged = previous.MemoryRetentionMinutes != next.MemoryRetentionMinutes
                || previous.SampleIntervalMs != next.SampleIntervalMs;

            _settings = next;
            _diskEnabled = nextDiskEnabled;

            if (!_diskEnabled)
            {
                _minuteAccumulators.Clear();
                _pending.Clear();
            }

            if (capacityChanged)
            {
                var capacity = ComputeTrendCapacity(next);
                foreach (var buffer in _trends.Values)
                {
                    buffer.Resize(capacity);
                }
            }

            EvictTrendsLocked(Math.Clamp(next.TrendAppSlots, 1, SettingsValidator.TrendAppSlotsMax));
        }

        if (_diskEnabled)
        {
            Trim();
        }
    }

    /// <summary>把队列与内存缓冲全部落盘，并关闭文件句柄。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RetireActiveMinuteLocked();
        }

        _writerExit = true;
        try
        {
            _writerWake.Set();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (_writerThread != Thread.CurrentThread)
            {
                _writerThread.Join(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // 忽略。
        }

        try
        {
            lock (_writeGate)
            {
                WritePendingCore();
                CloseDayStream();
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("退出时落盘历史失败。", ex);
        }

        try
        {
            _writerWake.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    // ================= 内存层：趋势环形缓冲 =================

    /// <summary>把本帧 CPU 排名前 N 的应用写入各自的趋势环形缓冲。</summary>
    private void AppendTrendsLocked(SnapshotFrame frame, AppSettings settings)
    {
        var slots = Math.Clamp(settings.TrendAppSlots, 1, SettingsValidator.TrendAppSlotsMax);
        var capacity = ComputeTrendCapacity(settings);
        var count = Math.Min(frame.Apps.Length, slots);

        for (var i = 0; i < count; i++)
        {
            var app = frame.Apps[i];
            if (string.IsNullOrEmpty(app.Key))
            {
                continue;
            }

            var isNew = false;
            if (!_trends.TryGetValue(app.Key, out var buffer))
            {
                buffer = new TrendRingBuffer(capacity);
                _trends[app.Key] = buffer;
                isNew = true;
            }
            else if (buffer.Capacity != capacity)
            {
                buffer.Resize(capacity);
            }

            buffer.Add(new TrendPoint(frame.TimestampUtc, app.CpuPercent, app.WorkingSetBytes));

            if (isNew)
            {
                // 新应用加入后若超出槽位预算，淘汰最久未更新的缓冲。
                EvictTrendsLocked(slots);
            }
        }
    }

    /// <summary>淘汰最久未写入的趋势缓冲，保证缓冲个数不超过 <paramref name="slots"/>。</summary>
    private void EvictTrendsLocked(int slots)
    {
        while (_trends.Count > slots)
        {
            string? victimKey = null;
            var oldest = long.MaxValue;

            foreach (var (key, buffer) in _trends)
            {
                if (buffer.LastWriteUtcTicks < oldest)
                {
                    oldest = buffer.LastWriteUtcTicks;
                    victimKey = key;
                }
            }

            if (victimKey is null)
            {
                return;
            }

            _trends.Remove(victimKey);
        }
    }

    /// <summary>趋势缓冲容量：保留分钟数 / 采样间隔，最多 86400 点。</summary>
    private static int ComputeTrendCapacity(AppSettings settings)
    {
        var minutes = Math.Clamp(settings.MemoryRetentionMinutes, SettingsValidator.MemoryRetentionMinutesMin, SettingsValidator.MemoryRetentionMinutesMax);
        var interval = Math.Max(1, settings.SampleIntervalMs);
        var capacity = (long)minutes * 60000L / interval;
        return (int)Math.Clamp(capacity, 1, MaxTrendCapacity);
    }

    // ================= 分钟聚合与磁盘落盘 =================

    /// <summary>把本帧应用累计到当前分钟的聚合桶。</summary>
    private void AppendMinuteAggregationLocked(SnapshotFrame frame, AppSettings settings)
    {
        if (!_diskEnabled)
        {
            return;
        }

        var slots = Math.Clamp(settings.TrendAppSlots, 1, SettingsValidator.TrendAppSlotsMax);
        var budget = Math.Min(frame.Apps.Length, Math.Max(settings.TopN, slots));

        for (var i = 0; i < frame.Apps.Length && i < budget; i++)
        {
            var app = frame.Apps[i];
            if (string.IsNullOrEmpty(app.Key))
            {
                continue;
            }

            var cpu = double.IsFinite(app.CpuPercent) ? Math.Max(0, app.CpuPercent) : 0;
            var workingSet = Math.Max(0, app.WorkingSetBytes);

            // 明显空闲的小应用不落盘，控制日文件体积（30 天占用更可控）。
            if (cpu < MinRecordedCpuPercent && workingSet < MinRecordedWorkingSetBytes)
            {
                continue;
            }

            var appId = _catalog.GetOrAdd(app.Key, app.DisplayName);
            if (appId <= 0)
            {
                continue;
            }

            if (!_minuteAccumulators.TryGetValue(appId, out var accumulator))
            {
                accumulator = new MinuteAccumulator();
                _minuteAccumulators[appId] = accumulator;
            }

            accumulator.SumCpu += cpu;
            accumulator.MaxCpu = Math.Max(accumulator.MaxCpu, cpu);
            accumulator.WorkingSetPeak = Math.Max(accumulator.WorkingSetPeak, workingSet);
            accumulator.Count++;
        }
    }

    /// <summary>把当前分钟的聚合桶转成待写记录并清空。</summary>
    private void RetireActiveMinuteLocked()
    {
        if (_minuteAccumulators.Count == 0 || _activeMinute < 0)
        {
            return;
        }

        var date = _activeDate;
        var minute = _activeMinute;

        foreach (var (appId, accumulator) in _minuteAccumulators)
        {
            if (accumulator.Count <= 0)
            {
                continue;
            }

            _pending.Add(new PendingRecord(date, new DayRecord(
                appId,
                minute,
                (float)(accumulator.SumCpu / accumulator.Count),
                (float)accumulator.MaxCpu,
                accumulator.WorkingSetPeak)));
        }

        _minuteAccumulators.Clear();
    }

    /// <summary>取当前分钟聚合桶的快照（供 QueryDay 显示准实时数据，不修改状态）。</summary>
    private DayRecord[] SnapshotActiveMinute()
    {
        lock (_sync)
        {
            if (_disposed || _minuteAccumulators.Count == 0 || _activeMinute < 0 || _activeDate != Today())
            {
                return Array.Empty<DayRecord>();
            }

            var result = new DayRecord[_minuteAccumulators.Count];
            var index = 0;
            foreach (var (appId, accumulator) in _minuteAccumulators)
            {
                result[index++] = new DayRecord(
                    appId,
                    _activeMinute,
                    accumulator.Count > 0 ? (float)(accumulator.SumCpu / accumulator.Count) : 0f,
                    (float)accumulator.MaxCpu,
                    accumulator.WorkingSetPeak);
            }

            return result;
        }
    }

    /// <summary>后台落盘线程：每 30 秒或收到信号时刷一次。</summary>
    private void WriterLoop()
    {
        while (!_writerExit)
        {
            try
            {
                _writerWake.Wait(WriterIntervalMs);
                _writerWake.Reset();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // 等待异常不终止线程。
            }

            if (_writerExit)
            {
                break;
            }

            try
            {
                lock (_writeGate)
                {
                    WritePendingCore();
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("历史落盘线程异常，已忽略并继续。", ex);
            }
        }

        try
        {
            lock (_writeGate)
            {
                WritePendingCore();
                CloseDayStream();
            }
        }
        catch
        {
            // 退出路径上的异常忽略。
        }
    }

    /// <summary>唤醒落盘线程（非阻塞）。</summary>
    private void SignalWriter()
    {
        try
        {
            _writerWake.Set();
        }
        catch
        {
            // 已释放时忽略。
        }
    }

    /// <summary>把待写记录写入磁盘；调用方必须持有 <see cref="_writeGate"/>。</summary>
    private void WritePendingCore()
    {
        List<PendingRecord> batch;

        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                FlushDayStream();
                return;
            }

            if (!_diskEnabled)
            {
                _pending.Clear();
                return;
            }

            batch = new List<PendingRecord>(_pending);
            _pending.Clear();
        }

        // 先落目录表，保证重启后 appId 一定能解析出应用名。
        _catalog.SaveIfDirty();

        try
        {
            WriteBatchCore(batch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("历史文件写入失败，120 毫秒后重试一次。", ex);
            CloseDayStream();
            try
            {
                Thread.Sleep(120);
                WriteBatchCore(batch);
            }
            catch (Exception retryEx)
            {
                _log?.Warn("历史文件重试仍失败，本批记录跳过（下次采样继续累积）。", retryEx);
                CloseDayStream();
            }
        }
    }

    /// <summary>把一批记录按日期分组写入对应的日文件。</summary>
    private void WriteBatchCore(List<PendingRecord> batch)
    {
        var index = 0;
        while (index < batch.Count)
        {
            var date = batch[index].Date;
            var end = index;
            while (end < batch.Count && batch[end].Date == date)
            {
                end++;
            }

            OpenDayStream(date);

            var stream = _dayStream;
            if (stream is not null)
            {
                var count = end - index;
                var bytes = new byte[count * DayRecord.Size];
                for (var i = 0; i < count; i++)
                {
                    batch[index + i].Record.Encode(bytes.AsSpan(i * DayRecord.Size, DayRecord.Size));
                }

                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            index = end;
        }
    }

    /// <summary>打开（或切换到）指定日期的追加写入流。</summary>
    private void OpenDayStream(DateOnly date)
    {
        if (_dayStream is not null && _dayStreamDate == date)
        {
            return;
        }

        CloseDayStream();

        try
        {
            Directory.CreateDirectory(_daysDirectory);
            var path = DayPath(date);
            _dayStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.None);
            _dayStreamDate = date;
        }
        catch (Exception ex)
        {
            _log?.Warn($"打开历史文件失败：{DayPath(date)}", ex);
            CloseDayStream();
        }
    }

    /// <summary>刷新并关闭日文件流。</summary>
    private void CloseDayStream()
    {
        var stream = _dayStream;
        _dayStream = null;
        _dayStreamDate = DateOnly.MinValue;

        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Flush();
            stream.Dispose();
        }
        catch
        {
            // 关闭失败忽略。
        }
    }

    /// <summary>刷新日文件流缓冲（不关闭）。</summary>
    private void FlushDayStream()
    {
        try
        {
            _dayStream?.Flush();
        }
        catch (Exception ex)
        {
            _log?.Warn("刷新历史文件缓冲失败，准备重新打开。", ex);
            CloseDayStream();
        }
    }

    // ================= 读取 =================

    /// <summary>读取一个日文件的全部有效记录，尾部残片与非法记录自动跳过。</summary>
    private List<DayRecord> ReadDayRecords(string path)
    {
        var result = new List<DayRecord>(512);

        byte[] bytes;
        try
        {
            bytes = ReadAllBytesWithRetry(path);
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取历史文件失败：{path}", ex);
            return result;
        }

        var offset = 0;
        while (offset + DayRecord.Size <= bytes.Length)
        {
            if (DayRecord.TryDecode(bytes.AsSpan(offset, DayRecord.Size), out var record))
            {
                result.Add(record);
            }

            offset += DayRecord.Size;
        }

        // 尾部不足 32 字节的残片（崩溃时写了一半）直接丢弃。
        return result;
    }

    /// <summary>读取字节，共享冲突时重试一次。</summary>
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, int.MaxValue);
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var chunk = stream.Read(buffer, read, length - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            return read == length ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            Thread.Sleep(80);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, int.MaxValue);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return read == length ? buffer : buffer[..read];
        }
    }

    /// <summary>把一条记录合并进「每应用每分钟」字典。</summary>
    private static void MergeIntoPerMinute(Dictionary<long, DayRecord> perMinute, in DayRecord record)
    {
        var key = ((long)record.AppId << 16) | (uint)record.MinuteOfDay;
        if (perMinute.TryGetValue(key, out var existing))
        {
            perMinute[key] = existing.MergeMax(record);
            return;
        }

        perMinute[key] = record;
    }

    // ================= 小工具 =================

    /// <summary>本地日期。</summary>
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>某天的历史文件路径。</summary>
    private string DayPath(DateOnly date) => Path.Combine(_daysDirectory, DayRecord.BuildFileName(date));

    /// <summary>单帧内单个应用的分钟聚合桶（类，便于在字典里就地累加）。</summary>
    private sealed class MinuteAccumulator
    {
        public double SumCpu;

        public double MaxCpu;

        public long WorkingSetPeak;

        public int Count;
    }

    /// <summary>查询结果的中间聚合。</summary>
    private sealed class AppAggregate
    {
        public double CpuPercentSeconds;

        public double PeakCpuPercent;

        public long PeakWorkingSetBytes;

        public int MinuteCount;
    }

    /// <summary>待写盘的一条记录（带日期）。</summary>
    private readonly record struct PendingRecord(DateOnly Date, DayRecord Record);
}
