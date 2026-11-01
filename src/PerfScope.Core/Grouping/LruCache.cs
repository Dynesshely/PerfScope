namespace PerfScope.Core.Grouping;

/// <summary>
/// 极简 LRU 缓存：容量上限 + TTL + 线程安全。归并引擎的版本信息 / 安装根 / 归类结果三张缓存都用它。
/// 命中时把节点移到链表头，容量溢出时从链表尾淘汰；读取时发现过期立即删除。
/// </summary>
/// <typeparam name="TValue">缓存值类型（引用类型，避免装箱）。</typeparam>
internal sealed class LruCache<TValue>
    where TValue : class
{
    private sealed class Entry
    {
        public Entry(string key, TValue value, long timestampMs)
        {
            Key = key;
            Value = value;
            TimestampMs = timestampMs;
        }

        public string Key { get; }

        public TValue Value { get; }

        public long TimestampMs { get; }
    }

    private readonly int _capacity;
    private readonly long _ttlMs;
    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly object _gate = new();

    /// <summary>创建缓存。</summary>
    /// <param name="capacity">最大条目数。</param>
    /// <param name="ttl">条目存活时间；传 <see cref="TimeSpan.Zero"/> 表示不过期。</param>
    /// <param name="comparer">键比较器（默认不区分大小写，适配 Windows 路径）。</param>
    public LruCache(int capacity, TimeSpan ttl, IEqualityComparer<string>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttlMs = ttl <= TimeSpan.Zero ? 0 : (long)ttl.TotalMilliseconds;
        _map = new Dictionary<string, LinkedListNode<Entry>>(
            Math.Min(_capacity, 256),
            comparer ?? StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>当前条目数（含尚未被惰性清理的过期条目）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>尝试读取；过期条目会被删除并返回 <c>false</c>。</summary>
    public bool TryGet(string key, long nowMs, out TValue? value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                value = null;
                return false;
            }

            var entry = node.Value;
            if (_ttlMs > 0 && nowMs - entry.TimestampMs > _ttlMs)
            {
                _map.Remove(key);
                _order.Remove(node);
                value = null;
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            value = entry.Value;
            return true;
        }
    }

    /// <summary>写入（覆盖同名键）并淘汰最久未使用的条目。</summary>
    public void Set(string key, TValue value, long nowMs)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _map.Remove(key);
                _order.Remove(existing);
            }

            var node = _order.AddFirst(new Entry(key, value, nowMs));
            _map[key] = node;

            while (_map.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    /// <summary>删除满足条件的条目，返回删除数量（用于「发现新的安装根归属后清理旧的目录级结果」）。</summary>
    public int RemoveWhere(Func<TValue, bool> predicate)
    {
        lock (_gate)
        {
            if (_map.Count == 0)
            {
                return 0;
            }

            List<string>? doomed = null;
            foreach (var pair in _map)
            {
                if (predicate(pair.Value.Value.Value))
                {
                    (doomed ??= new List<string>()).Add(pair.Key);
                }
            }

            if (doomed is null)
            {
                return 0;
            }

            foreach (var key in doomed)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _map.Remove(key);
                    _order.Remove(node);
                }
            }

            return doomed.Count;
        }
    }
}
