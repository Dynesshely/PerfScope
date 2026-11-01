using System.Globalization;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 展示名唯一性注册表：保证「同一帧内一个 DisplayName 只对应一个应用」。
/// <para>
/// 同名但 Key 不同的应用（例如同一款软件装在两处、多账号沙箱根目录）会追加确定性后缀，展示成
/// <c>名称（区分段）</c>。区分段按以下顺序挑选：
/// ① 存在某一层尾部路径段使所有成员互不相同 → 用该段（例如 <c>Weixin（Mainland_Apps）</c>）；
/// ② 混合场景（有成员拿不到路径）→ 无路径的标成「进程」，有路径的取「唯一且不等于自身名字」的尾部段；
/// ③ 只有两个成员 → 用最短唯一尾部切片；
/// ④ 都不行 → 序号（2）（3）……
/// </para>
/// <para>
/// 决策按 Key 缓存：同一个 Key 的展示名一旦确定就固定下来（即使同 Key 下不同 exe 读到的版本信息不一致），
/// 相关 Key 超过 5 分钟不再出现后自动释放。
/// </para>
/// </summary>
internal sealed class DisplayNameRegistry
{
    private const long EntryIdleTtlMs = 5 * 60 * 1000L;
    private const int PruneInterval = 1024;
    private const string NoPathSuffix = "进程";

    private readonly object _gate = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private int _registerCount;

    /// <summary>
    /// 登记一个归并结果，返回本帧应展示的名字（必要时带区分后缀）。
    /// </summary>
    /// <param name="key">归并键（稳定标识）。</param>
    /// <param name="baseDisplay">不带区分后缀的基础展示名。</param>
    /// <param name="rootPath">应用根目录，可为 <c>null</c>（用于生成区分段）。</param>
    /// <param name="exeBase">映像名（去扩展名），区分段兜底用。</param>
    /// <param name="nowMs">当前时间（单调毫秒）。</param>
    public string Register(string key, string baseDisplay, string? rootPath, string? exeBase, long nowMs)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(baseDisplay))
        {
            return baseDisplay;
        }

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var known))
            {
                known.LastSeenMs = nowMs;
                known.Group.LastSeenMs = nowMs;
                return Compose(known);
            }

            if (!_groups.TryGetValue(baseDisplay, out var group))
            {
                group = new Group(baseDisplay);
                _groups[baseDisplay] = group;
            }

            var entry = new Entry(key, baseDisplay, rootPath, exeBase)
            {
                Group = group,
                LastSeenMs = nowMs,
            };

            group.Entries[key] = entry;
            group.LastSeenMs = nowMs;
            _byKey[key] = entry;

            if (group.Entries.Count > 1)
            {
                RecomputeSuffixes(group);
            }

            if (++_registerCount >= PruneInterval)
            {
                _registerCount = 0;
                PruneStale(nowMs);
            }

            return Compose(entry);
        }
    }

    /// <summary>清空全部登记（设置变更 / 重置缓存时调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _groups.Clear();
            _byKey.Clear();
            _registerCount = 0;
        }
    }

    private void PruneStale(long nowMs)
    {
        List<string>? doomed = null;
        foreach (var pair in _groups)
        {
            if (nowMs - pair.Value.LastSeenMs > EntryIdleTtlMs)
            {
                (doomed ??= new List<string>()).Add(pair.Key);
            }
        }

        if (doomed is null)
        {
            return;
        }

        foreach (var display in doomed)
        {
            if (!_groups.TryGetValue(display, out var group))
            {
                continue;
            }

            foreach (var key in group.Entries.Keys)
            {
                _byKey.Remove(key);
            }

            _groups.Remove(display);
        }
    }

    private static string Compose(Entry entry)
        => entry.Suffix is { Length: > 0 } suffix ? entry.BaseDisplay + "（" + suffix + "）" : entry.BaseDisplay;

    private static void RecomputeSuffixes(Group group)
    {
        var entries = group.Entries.Values
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count <= 1)
        {
            if (entries.Count == 1)
            {
                entries[0].Suffix = null;
            }

            return;
        }

        var segments = entries.Select(ExtractSegments).ToList();

        // ① 单段区分：存在某一层尾部段，使所有成员互不相同 → 各取该段
        if (TryAssignDistinctSegment(entries, segments))
        {
            return;
        }

        // ② 混合场景：没有路径信息的成员标成「进程」，有路径的取「唯一且不等于自身名字」的尾部段
        if (TryAssignMixedSuffixes(entries, segments))
        {
            return;
        }

        // ③ 只有两个成员时，退回「最短唯一尾部切片」
        if (entries.Count == 2)
        {
            var slices = Enumerable.Range(0, entries.Count).Select(index => FindUniqueSlice(segments, index)).ToList();
            if (slices.All(static slice => slice is not null) && AllDistinct(slices))
            {
                Assign(entries, slices!);
                return;
            }
        }

        // ④ 序号：第一个保持原名，后面的加（2）（3）…
        entries[0].Suffix = null;
        for (var index = 1; index < entries.Count; index++)
        {
            entries[index].Suffix = (index + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool TryAssignDistinctSegment(List<Entry> entries, List<string[]> segments)
    {
        var maxDepth = segments.Max(static item => item.Length);
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var values = new List<string>(entries.Count);
            var incomplete = false;
            foreach (var item in segments)
            {
                var value = SegmentAt(item, depth);
                if (string.IsNullOrEmpty(value))
                {
                    incomplete = true;
                    break;
                }

                values.Add(value!);
            }

            if (incomplete || !AllDistinct(values))
            {
                continue;
            }

            Assign(entries, values);
            return true;
        }

        return false;
    }

    private static bool TryAssignMixedSuffixes(List<Entry> entries, List<string[]> segments)
    {
        var values = new List<string?>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var mine = segments[index];
            if (mine.Length == 0)
            {
                // 拿不到路径的进程：标明这行是「仅凭映像名聚出来」的。
                values.Add(NoPathSuffix);
                continue;
            }

            string? chosen = null;
            for (var depth = 1; depth <= mine.Length; depth++)
            {
                var candidate = mine[depth - 1];
                if (string.IsNullOrEmpty(candidate) || IsValueSharedWithOthers(segments, index, candidate))
                {
                    continue;
                }

                var trimmed = TrimExeSuffix(candidate);
                if (trimmed.Length == 0
                    || string.Equals(trimmed, entries[index].BaseDisplay, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                chosen = candidate;
                break;
            }

            values.Add(chosen);
        }

        if (values.Any(static value => value is null))
        {
            return false;
        }

        var concrete = values.Select(static value => value!).ToList();
        if (!AllDistinct(concrete))
        {
            return false;
        }

        Assign(entries, concrete);
        return true;
    }

    private static bool IsValueSharedWithOthers(List<string[]> segments, int index, string value)
    {
        for (var other = 0; other < segments.Count; other++)
        {
            if (other == index)
            {
                continue;
            }

            foreach (var item in segments[other])
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? FindUniqueSlice(List<string[]> segments, int index)
    {
        var mine = segments[index];
        for (var depth = 1; depth <= mine.Length; depth++)
        {
            var slice = JoinTail(mine, depth);
            var unique = true;
            for (var other = 0; other < segments.Count; other++)
            {
                if (other == index)
                {
                    continue;
                }

                var theirs = segments[other];
                var otherDepth = Math.Min(depth, theirs.Length);
                if (otherDepth > 0 && string.Equals(JoinTail(theirs, otherDepth), slice, StringComparison.OrdinalIgnoreCase))
                {
                    unique = false;
                    break;
                }
            }

            if (unique)
            {
                return slice;
            }
        }

        return null;
    }

    private static void Assign(List<Entry> entries, IReadOnlyList<string> values)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Suffix = TrimExeSuffix(values[index]);
        }
    }

    private static bool AllDistinct(IEnumerable<string?> values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!set.Add(TrimExeSuffix(value)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>区分段里去掉 <c>.exe</c>：展示成「NvContainer（nvcontainer）」比带扩展名干净。</summary>
    private static string TrimExeSuffix(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value!.Trim();
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? text.Substring(0, text.Length - 4)
            : text;
    }

    /// <summary>取「尾部优先」的路径段数组：索引 0 是最后一段。</summary>
    private static string[] ExtractSegments(Entry entry)
    {
        var root = entry.RootPath;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var parts = root!.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                Array.Reverse(parts);
                return parts;
            }
        }

        var key = entry.Key;
        const string directoryPrefix = "dir:";
        const string productPrefix = "product:";
        const string exePrefix = "exe:";

        if (key.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Substring(directoryPrefix.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                Array.Reverse(parts);
                return parts;
            }

            return Array.Empty<string>();
        }

        if (key.StartsWith(productPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = key.Substring(productPrefix.Length);
            var separator = body.IndexOf('|', StringComparison.Ordinal);
            if (separator < 0)
            {
                return body.Length > 0 ? new[] { body } : Array.Empty<string>();
            }

            var product = body.Substring(separator + 1);
            var company = body.Substring(0, separator);
            return company.Length > 0 ? new[] { product, company } : new[] { product };
        }

        if (key.StartsWith(exePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { key.Substring(exePrefix.Length) };
        }

        return Array.Empty<string>();
    }

    private static string? SegmentAt(string[] segments, int depth)
        => depth >= 1 && depth <= segments.Length ? segments[depth - 1] : null;

    /// <summary>把「尾部优先」的数组前 <paramref name="depth"/> 段按自然顺序（由外到内）拼起来。</summary>
    private static string JoinTail(string[] segments, int depth)
    {
        var count = Math.Min(depth, segments.Length);
        if (count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        for (var index = count - 1; index >= 0; index--)
        {
            if (builder.Length > 0)
            {
                builder.Append(" / ");
            }

            builder.Append(segments[index]);
        }

        return builder.ToString();
    }

    private sealed class Entry
    {
        public Entry(string key, string baseDisplay, string? rootPath, string? exeBase)
        {
            Key = key;
            BaseDisplay = baseDisplay;
            RootPath = rootPath;
            ExeBase = exeBase;
        }

        public string Key { get; }

        public string BaseDisplay { get; }

        public string? RootPath { get; }

        public string? ExeBase { get; }

        public string? Suffix { get; set; }

        public long LastSeenMs { get; set; }

        public Group Group { get; set; } = null!;
    }

    private sealed class Group
    {
        public Group(string baseDisplay)
        {
            BaseDisplay = baseDisplay;
        }

        public string BaseDisplay { get; }

        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long LastSeenMs { get; set; }
    }
}
