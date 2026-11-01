using System.Diagnostics;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Sampling;

/// <summary>
/// 把原始采样合成为「应用级」帧：差分 CPU 与 IO、按归并键聚合、平滑、按 CPU 降序排序。
/// </summary>
/// <remarks>
/// <para><b>差分</b>：内部保存上一帧的进程样本字典与系统计数器；同一 PID 只有
/// <c>CreateTimeUtcTicks</c> 一致才算同一个进程，PID 被复用时按新进程处理（本帧 CPU 记 0）。</para>
/// <para><b>经过时间</b>：用单调时钟（默认 <see cref="Stopwatch.GetTimestamp"/>）测量，绝不用墙上时间相减，
/// 因此系统时钟跳变不会污染 CPU 百分比；时间未前进时直接跳过该帧且不更新基线。</para>
/// <para><b>口径</b>：<see cref="CpuNormalization.MachinePercent"/> 为 0~100；<see cref="CpuNormalization.PerCorePercent"/>
/// 为 0~100*逻辑核心数。单进程差值上限按整机 200% 钳制，用于兜住计数器异常。</para>
/// <para><b>平滑</b>：每个应用归并键维护最近 N 帧的 CPU 滑动平均（N = <see cref="AppSettings.SmoothingWindow"/>）；
/// 应用消失后立刻丢弃它的环形缓冲，保证长期运行不泄漏。</para>
/// <para><b>排除</b>：<see cref="AppIdentity.Excluded"/> 的进程不进入应用列表，也不计入进程总数。</para>
/// </remarks>
public sealed class SnapshotComposer : ISnapshotComposer, ISnapshotProducer
{
    /// <summary>单进程 CPU 的整机口径上限（%）。超过即视为计数器异常。</summary>
    private const double MaxMachineCpuPercent = 200d;

    /// <summary>100ns 计时单位换算：1 秒 = 10,000,000 个 100ns。</summary>
    private const double TicksPerSecond = 10_000_000d;

    private static readonly Comparison<AppProcessInfo> MemberComparison =
        static (left, right) => right.CpuPercent.CompareTo(left.CpuPercent);

    private static readonly Comparison<AppSample> AppComparison = static (left, right) =>
    {
        int byCpu = right.CpuPercent.CompareTo(left.CpuPercent);
        return byCpu != 0 ? byCpu : string.CompareOrdinal(left.DisplayName, right.DisplayName);
    };

    private readonly ICapture _capture;
    private readonly IAppClassifier _classifier;
    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessRawSample> _previousProcesses = new();
    private readonly Dictionary<int, ProcessRawSample> _currentProcesses = new();
    private readonly Dictionary<string, AppGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SmoothingBuffer> _appSmoothing = new(StringComparer.Ordinal);
    private readonly SmoothingBuffer _systemSmoothing = new();
    private readonly List<string> _staleKeys = new();
    private readonly Dictionary<string, int> _displayNameOwner = new(StringComparer.Ordinal);
    private readonly List<int> _duplicateIndexes = new();
    private readonly Dictionary<string, string> _displayNameOverride = new(StringComparer.Ordinal);
    private static readonly char[] PathSeparators = ['\\', '/'];

    private AppSettings _settings;
    private SystemRawSample? _previousSample;
    private long _previousTimestamp;
    private bool _hasPrevious;

    /// <summary>构造合成器。</summary>
    /// <param name="capture">原始采样来源。</param>
    /// <param name="classifier">进程 → 应用的归并器。</param>
    /// <param name="settings">初始设置（内部保存副本，之后通过 <see cref="OnSettingsChanged"/> 热更新）。</param>
    public SnapshotComposer(ICapture capture, IAppClassifier classifier, AppSettings settings)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();
    }

    /// <summary>原始采样来源（引擎需要它来挂接原生采集器的诊断回调）。</summary>
    public ICapture Capture => _capture;

    /// <summary>
    /// 单调时间源的频率，默认 <see cref="Stopwatch.Frequency"/>。仅供单测注入固定时钟。
    /// </summary>
    public long TimestampFrequency { get; set; } = Stopwatch.Frequency;

    /// <summary>
    /// 单调时间源，默认 <see cref="Stopwatch.GetTimestamp"/>。仅供单测注入固定时钟。
    /// 返回值的单位由 <see cref="TimestampFrequency"/> 决定。
    /// </summary>
    public Func<long> TimestampProvider { get; set; } = static () => Stopwatch.GetTimestamp();

    /// <summary>采集并合成一帧（先调用 <see cref="ICapture"/>）。首帧或时间源异常时返回 <c>null</c>。</summary>
    public SnapshotFrame? Compose() => Compose(_capture.Capture());

    /// <inheritdoc />
    public SnapshotFrame? Compose(SystemRawSample current)
    {
        ArgumentNullException.ThrowIfNull(current);

        lock (_gate)
        {
            long frequency = TimestampFrequency > 0 ? TimestampFrequency : Stopwatch.Frequency;
            long timestamp = TimestampProvider();

            if (!_hasPrevious)
            {
                StoreBaseline(current, timestamp);
                return null;
            }

            long elapsedTicks = timestamp - _previousTimestamp;
            if (elapsedTicks <= 0)
            {
                // 时间源未前进或回退：跳过本帧，且保持基线不变，让下一帧覆盖整段区间。
                return null;
            }

            double elapsedSeconds = elapsedTicks / (double)frequency;
            int logicalProcessors = current.LogicalProcessorCount > 0 ? current.LogicalProcessorCount : Environment.ProcessorCount;
            if (logicalProcessors <= 0)
            {
                logicalProcessors = 1;
            }

            AppSettings settings = _settings;
            int smoothingWindow = Math.Max(1, settings.SmoothingWindow);
            bool perCore = settings.CpuMode == CpuNormalization.PerCorePercent;

            RefreshCurrentProcesses(current);
            _groups.Clear();

            int selfPid = Environment.ProcessId;
            double selfCpuMachinePercent = 0;
            long selfWorkingSetBytes = 0;
            int countedProcessCount = 0;

            ProcessRawSample[] processes = current.Processes;
            for (int index = 0; index < processes.Length; index++)
            {
                ProcessRawSample process = processes[index];

                // PID 0 是内核的「空闲计时桶」，它的 CPU 时间等于全系统空闲时间，
                // 既不是应用也没有内存，计入后会凭空多出一行巨大的「Idle 应用」并污染聚合结果。
                if (process.Pid == 0)
                {
                    continue;
                }

                ProcessRawSample? parent = process.ParentPid != 0 && _currentProcesses.TryGetValue(process.ParentPid, out ProcessRawSample parentSample)
                    ? parentSample
                    : null;

                AppIdentity identity = _classifier.Classify(process, parent);
                if (identity.Excluded)
                {
                    continue;
                }

                countedProcessCount++;

                TryComputeDelta(process, out long cpuTicks, out long readBytes, out long writeBytes);
                double machinePercent = cpuTicks <= 0
                    ? 0
                    : Math.Min(cpuTicks / (elapsedSeconds * TicksPerSecond * logicalProcessors) * 100d, MaxMachineCpuPercent);
                double displayPercent = perCore ? machinePercent * logicalProcessors : machinePercent;

                if (process.Pid == selfPid)
                {
                    selfCpuMachinePercent = machinePercent;
                    selfWorkingSetBytes = process.WorkingSetBytes;
                }

                Accumulate(identity, process, machinePercent, displayPercent, readBytes, writeBytes);
            }

            double systemCpuPercent = _systemSmoothing.Push(ComputeSystemCpuPercent(current), smoothingWindow);
            double memoryPercent = ComputeMemoryPercent(current);

            AppSample[] apps = ApplyDisplayNameDedup(BuildApps(elapsedSeconds, smoothingWindow));
            CleanupSmoothing();

            StoreBaseline(current, timestamp);

            return new SnapshotFrame(
                current.TimestampUtc,
                systemCpuPercent,
                memoryPercent,
                current.TotalPhysicalBytes,
                current.AvailablePhysicalBytes,
                TimeSpan.FromSeconds(elapsedSeconds),
                selfCpuMachinePercent,
                selfWorkingSetBytes,
                countedProcessCount,
                apps);
        }
    }

    /// <inheritdoc />
    public void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings.Clone();
        }

        // 归并规则、系统组名、兜底组名等也在设置里：必须同步给归并器，否则改了规则不生效。
        _classifier.OnSettingsChanged(settings);
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            _hasPrevious = false;
            _previousSample = null;
            _previousTimestamp = 0;
            _previousProcesses.Clear();
            _currentProcesses.Clear();
            _groups.Clear();
            _appSmoothing.Clear();
            _systemSmoothing.Clear();
        }
    }

    // ================= 差分 =================

    private bool TryComputeDelta(in ProcessRawSample current, out long cpuTicks, out long readBytes, out long writeBytes)
    {
        cpuTicks = 0;
        readBytes = 0;
        writeBytes = 0;

        if (!_previousProcesses.TryGetValue(current.Pid, out ProcessRawSample previous))
        {
            // 新进程：本帧 CPU / IO 记 0，不伪造数据。
            return false;
        }

        if (previous.CreateTimeUtcTicks != current.CreateTimeUtcTicks)
        {
            // PID 复用：创建时间不同，视为全新进程。
            return false;
        }

        cpuTicks = current.CpuTimeTicks - previous.CpuTimeTicks;
        if (cpuTicks < 0)
        {
            cpuTicks = 0;
        }

        readBytes = current.ReadBytesTransferred - previous.ReadBytesTransferred;
        if (readBytes < 0)
        {
            readBytes = 0;
        }

        writeBytes = current.WriteBytesTransferred - previous.WriteBytesTransferred;
        if (writeBytes < 0)
        {
            writeBytes = 0;
        }

        return true;
    }

    private double ComputeSystemCpuPercent(SystemRawSample current)
    {
        SystemRawSample? previous = _previousSample;
        if (previous is null)
        {
            return 0;
        }

        long idleDelta = Math.Max(0, current.SystemCpuIdleTicks - previous.SystemCpuIdleTicks);
        long kernelDelta = Math.Max(0, current.SystemCpuKernelTicks - previous.SystemCpuKernelTicks);
        long userDelta = Math.Max(0, current.SystemCpuUserTicks - previous.SystemCpuUserTicks);

        // 内核时间已包含空闲时间，因此总量 = 内核 + 用户，忙碌 = 总量 - 空闲。
        long totalDelta = kernelDelta + userDelta;
        if (totalDelta <= 0)
        {
            return 0;
        }

        double percent = (totalDelta - idleDelta) * 100d / totalDelta;
        return Math.Clamp(percent, 0d, 100d);
    }

    private static double ComputeMemoryPercent(SystemRawSample current)
    {
        if (current.TotalPhysicalBytes <= 0)
        {
            return 0;
        }

        double used = current.TotalPhysicalBytes - current.AvailablePhysicalBytes;
        double percent = used * 100d / current.TotalPhysicalBytes;
        return Math.Clamp(percent, 0d, 100d);
    }

    // ================= 聚合 =================

    private void Accumulate(AppIdentity identity, in ProcessRawSample process, double machinePercent, double displayPercent, long readBytes, long writeBytes)
    {
        if (!_groups.TryGetValue(identity.Key, out AppGroup? group))
        {
            group = new AppGroup { Key = identity.Key, Identity = identity, MaxMemberCpuMachinePercent = -1 };
            _groups.Add(identity.Key, group);
        }

        group.CpuDisplayPercent += displayPercent;
        group.WorkingSetBytes += process.WorkingSetBytes;
        group.PrivateBytes += process.PrivateBytes;
        group.ProcessCount++;
        group.ReadBytes += readBytes;
        group.WriteBytes += writeBytes;
        group.Members.Add(new AppProcessInfo(
            process.Pid,
            process.ImageName,
            process.ExecutablePath,
            displayPercent,
            process.WorkingSetBytes,
            process.PrivateBytes));

        if (machinePercent > group.MaxMemberCpuMachinePercent)
        {
            group.MaxMemberCpuMachinePercent = machinePercent;
            group.Identity = identity;
            if (process.ExecutablePath is not null)
            {
                group.IconSourcePath = process.ExecutablePath;
            }
        }
        else if (group.IconSourcePath is null && process.ExecutablePath is not null)
        {
            group.IconSourcePath = process.ExecutablePath;
        }
    }

    private AppSample[] BuildApps(double elapsedSeconds, int smoothingWindow)
    {
        var apps = new AppSample[_groups.Count];
        int index = 0;

        foreach (AppGroup group in _groups.Values)
        {
            if (!_appSmoothing.TryGetValue(group.Key, out SmoothingBuffer? buffer))
            {
                buffer = new SmoothingBuffer();
                _appSmoothing.Add(group.Key, buffer);
            }

            double smoothedCpuPercent = buffer.Push(group.CpuDisplayPercent, smoothingWindow);
            group.Members.Sort(MemberComparison);

            apps[index++] = new AppSample(
                group.Key,
                group.Identity.DisplayName,
                group.Identity.RootPath,
                group.Identity.Kind,
                group.IconSourcePath,
                smoothedCpuPercent,
                group.WorkingSetBytes,
                group.PrivateBytes,
                group.ProcessCount,
                group.ReadBytes / elapsedSeconds,
                group.WriteBytes / elapsedSeconds,
                group.Identity.Reason,
                group.Members.ToArray());
        }

        Array.Sort(apps, AppComparison);
        return apps;
    }

    // ================= 显示名去重 =================

    /// <summary>
    /// 同一帧里多个不同归并键撞了同一个 <see cref="AppSample.DisplayName"/> 时（例如两个安装目录下的 Weixin），
    /// 给它们追加确定性的区分后缀，避免 UI 上出现两行同名应用。
    /// </summary>
    /// <remarks>
    /// <para><b>只改 DisplayName，不动 Key</b>（历史与趋势依赖 Key）。</para>
    /// <para><b>跨帧稳定</b>：后缀只由「该组应用自身的 Key / RootPath / IconSourcePath」决定，
    /// 组内先按 Key 排序再计算，因此与进程顺序、CPU 排名无关。</para>
    /// <para><b>开销</b>：没有同名应用时只做一遍「名字 → 首个下标」字典写入；有同名时才多一次分组与后缀计算。</para>
    /// </remarks>
    private AppSample[] ApplyDisplayNameDedup(AppSample[] apps)
    {
        if (apps.Length < 2)
        {
            return apps;
        }

        _displayNameOwner.Clear();
        _duplicateIndexes.Clear();

        for (int index = 0; index < apps.Length; index++)
        {
            string name = apps[index].DisplayName;
            if (_displayNameOwner.TryGetValue(name, out int firstIndex))
            {
                _duplicateIndexes.Add(index);
                if (firstIndex >= 0)
                {
                    // 首次发现重名：把先出现的那一项也补进来，并把 owner 标记成 -1 表示「已登记」。
                    _duplicateIndexes.Add(firstIndex);
                    _displayNameOwner[name] = -1;
                }
            }
            else
            {
                _displayNameOwner.Add(name, index);
            }
        }

        if (_duplicateIndexes.Count == 0)
        {
            return apps;
        }

        var groups = new Dictionary<string, List<AppSample>>(StringComparer.Ordinal);
        foreach (int index in _duplicateIndexes)
        {
            AppSample app = apps[index];
            if (!groups.TryGetValue(app.DisplayName, out List<AppSample>? group))
            {
                group = new List<AppSample>(2);
                groups.Add(app.DisplayName, group);
            }

            group.Add(app);
        }

        _displayNameOverride.Clear();
        foreach (KeyValuePair<string, List<AppSample>> pair in groups)
        {
            List<AppSample> group = pair.Value;
            if (group.Count < 2)
            {
                continue;
            }

            // 按 Key 排序，保证后缀分配与帧内顺序无关。
            group.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

            string[] suffixes = ResolveDistinctSuffixes(group);
            for (int index = 0; index < group.Count; index++)
            {
                _displayNameOverride[group[index].Key] = group[index].DisplayName + "（" + suffixes[index] + "）";
            }
        }

        if (_displayNameOverride.Count == 0)
        {
            return apps;
        }

        for (int index = 0; index < apps.Length; index++)
        {
            if (_displayNameOverride.TryGetValue(apps[index].Key, out string? displayName))
            {
                apps[index] = apps[index] with { DisplayName = displayName };
            }
        }

        return apps;
    }

    /// <summary>
    /// 为同名的 k 个应用算出互不相同的区分后缀，按「由俭入奢」的顺序退让。
    /// </summary>
    private static string[] ResolveDistinctSuffixes(List<AppSample> group)
    {
        return TryPathTailSuffixes(group)
            ?? TryDriveAndLeadingSegments(group)
            ?? TryIconFileNames(group)
            ?? TryFullRootPaths(group)
            ?? TryKeySegments(group)
            ?? BuildKeySuffixes(group);
    }

    /// <summary>
    /// 候选一：RootPath 的「最短区分尾部段」。例如
    /// <c>C:\Program Files\Tencent\Weixin</c> 与 <c>D:\Mainland_Apps\Weixin</c> 得到 Tencent / Mainland_Apps。
    /// </summary>
    private static string[]? TryPathTailSuffixes(List<AppSample> group)
    {
        string[][] segments = SplitRootPaths(group, out int maxDepth);
        if (segments.Length == 0 || maxDepth < 2)
        {
            return null;
        }

        // 整条路径留给后面的候选，这里最多取到 maxDepth-1 层。
        for (int depth = 1; depth < maxDepth; depth++)
        {
            var tails = new string[group.Count];
            var leading = new string[group.Count];
            for (int index = 0; index < group.Count; index++)
            {
                string[] parts = segments[index];
                int take = Math.Min(depth, parts.Length);
                tails[index] = string.Join('\\', parts, parts.Length - take, take);
                leading[index] = parts[parts.Length - take];
            }

            if (!AllDistinct(tails))
            {
                continue;
            }

            return AllDistinct(leading) ? leading : tails;
        }

        return null;
    }

    /// <summary>候选二：盘符 + 首段，例如 <c>C:\Program Files</c>。</summary>
    private static string[]? TryDriveAndLeadingSegments(List<AppSample> group)
    {
        string[][] segments = SplitRootPaths(group, out _);
        if (segments.Length == 0)
        {
            return null;
        }

        var suffixes = new string[group.Count];
        for (int index = 0; index < group.Count; index++)
        {
            string[] parts = segments[index];
            suffixes[index] = parts.Length >= 2 ? parts[0] + "\\" + parts[1] : parts[0];
        }

        return AllDistinct(suffixes) ? suffixes : null;
    }

    /// <summary>候选三：IconSourcePath 的文件名。</summary>
    private static string[]? TryIconFileNames(List<AppSample> group)
    {
        var suffixes = new string[group.Count];
        for (int index = 0; index < group.Count; index++)
        {
            string? iconPath = group[index].IconSourcePath;
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return null;
            }

            string fileName = Path.GetFileName(iconPath);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            suffixes[index] = fileName;
        }

        return AllDistinct(suffixes) ? suffixes : null;
    }

    /// <summary>候选四：完整 RootPath。</summary>
    private static string[]? TryFullRootPaths(List<AppSample> group)
    {
        var suffixes = new string[group.Count];
        for (int index = 0; index < group.Count; index++)
        {
            string? rootPath = group[index].RootPath;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            suffixes[index] = rootPath;
        }

        return AllDistinct(suffixes) ? suffixes : null;
    }

    /// <summary>候选五：归并键里冒号之后的最后一段（例如 <c>dir:C:\...\Weixin</c> → Weixin）。</summary>
    private static string[]? TryKeySegments(List<AppSample> group)
    {
        var suffixes = new string[group.Count];
        for (int index = 0; index < group.Count; index++)
        {
            string key = group[index].Key;
            int colon = key.IndexOf(':', StringComparison.Ordinal);
            string tail = colon >= 0 && colon + 1 < key.Length ? key[(colon + 1)..] : key;
            string[] parts = tail.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            suffixes[index] = parts.Length > 0 ? parts[^1] : tail;
        }

        return AllDistinct(suffixes) ? suffixes : null;
    }

    /// <summary>最终兜底：完整归并键（归并键在一帧内必然唯一）。</summary>
    private static string[] BuildKeySuffixes(List<AppSample> group)
    {
        var suffixes = new string[group.Count];
        for (int index = 0; index < group.Count; index++)
        {
            suffixes[index] = group[index].Key;
        }

        return suffixes;
    }

    private static string[][] SplitRootPaths(List<AppSample> group, out int maxDepth)
    {
        var segments = new string[group.Count][];
        maxDepth = 0;
        for (int index = 0; index < group.Count; index++)
        {
            string? rootPath = group[index].RootPath;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                maxDepth = 0;
                return [];
            }

            string[] parts = rootPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                maxDepth = 0;
                return [];
            }

            segments[index] = parts;
            maxDepth = Math.Max(maxDepth, parts.Length);
        }

        return segments;
    }

    private static bool AllDistinct(string[] values)
    {
        var seen = new HashSet<string>(values.Length, StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>丢弃已经消失的应用的平滑缓冲，保证长跑不泄漏。</summary>
    private void CleanupSmoothing()
    {
        _staleKeys.Clear();
        foreach (string key in _appSmoothing.Keys)
        {
            if (!_groups.ContainsKey(key))
            {
                _staleKeys.Add(key);
            }
        }

        foreach (string key in _staleKeys)
        {
            _appSmoothing.Remove(key);
        }

        _staleKeys.Clear();
    }

    // ================= 基线 =================

    private void RefreshCurrentProcesses(SystemRawSample sample)
    {
        _currentProcesses.Clear();
        ProcessRawSample[] processes = sample.Processes;
        for (int index = 0; index < processes.Length; index++)
        {
            _currentProcesses[processes[index].Pid] = processes[index];
        }
    }

    private void StoreBaseline(SystemRawSample sample, long timestamp)
    {
        _previousProcesses.Clear();
        ProcessRawSample[] processes = sample.Processes;
        for (int index = 0; index < processes.Length; index++)
        {
            _previousProcesses[processes[index].Pid] = processes[index];
        }

        _previousSample = sample;
        _previousTimestamp = timestamp;
        _hasPrevious = true;
    }

    /// <summary>一个应用在一帧内的聚合中间态（仅合成过程中使用）。</summary>
    private sealed class AppGroup
    {
        /// <summary>归并键。</summary>
        public string Key = string.Empty;

        /// <summary>代表性成员的归类结果（取 CPU 最高的成员）。</summary>
        public AppIdentity Identity = null!;

        /// <summary>按设置口径累加的 CPU 百分比。</summary>
        public double CpuDisplayPercent;

        /// <summary>成员里最大的整机口径 CPU 百分比，用于挑代表成员。</summary>
        public double MaxMemberCpuMachinePercent;

        /// <summary>工作集合计。</summary>
        public long WorkingSetBytes;

        /// <summary>私有字节合计。</summary>
        public long PrivateBytes;

        /// <summary>成员进程数。</summary>
        public int ProcessCount;

        /// <summary>本区间读字节合计。</summary>
        public long ReadBytes;

        /// <summary>本区间写字节合计。</summary>
        public long WriteBytes;

        /// <summary>取图标用的可执行文件路径。</summary>
        public string? IconSourcePath;

        /// <summary>成员明细。</summary>
        public List<AppProcessInfo> Members = new(4);
    }
}
