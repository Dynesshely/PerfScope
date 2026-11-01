using PerfScope.Core.Models;

namespace PerfScope.Core.History;

/// <summary>
/// 趋势点的定长环形缓冲：空间一次分配、写满后覆盖最旧的点，永不扩容，
/// 因此长时间运行不会产生 GC 抖动。非线程安全，由 <see cref="HistoryStore"/> 统一加锁。
/// </summary>
internal sealed class TrendRingBuffer
{
    private TrendPoint[] _items;
    private int _start;
    private int _count;

    /// <summary>按给定容量创建缓冲（容量小于 1 时按 1 处理）。</summary>
    public TrendRingBuffer(int capacity)
    {
        _items = new TrendPoint[Math.Max(1, capacity)];
    }

    /// <summary>当前容量（最大点数）。</summary>
    public int Capacity => _items.Length;

    /// <summary>当前已存点数。</summary>
    public int Count => _count;

    /// <summary>最后一次写入点的时间戳（ticks），用于淘汰最久未更新的应用。</summary>
    public long LastWriteUtcTicks { get; private set; }

    /// <summary>追加一个点；写满后覆盖最旧的点。</summary>
    public void Add(in TrendPoint point)
    {
        LastWriteUtcTicks = point.TimestampUtc.Ticks;

        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = point;
            _count++;
            return;
        }

        _items[_start] = point;
        _start = (_start + 1) % _items.Length;
    }

    /// <summary>调整容量，保留最新的点（必要时丢弃最旧的点）。</summary>
    public void Resize(int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (capacity == _items.Length)
        {
            return;
        }

        var ordered = ToArray(0);
        var take = Math.Min(ordered.Length, capacity);
        var next = new TrendPoint[capacity];
        Array.Copy(ordered, ordered.Length - take, next, 0, take);

        _items = next;
        _start = 0;
        _count = take;
    }

    /// <summary>
    /// 按时间升序导出最近 <paramref name="maxPoints"/> 个点（<c>0</c> 或负数表示全部）。
    /// </summary>
    public TrendPoint[] ToArray(int maxPoints)
    {
        var take = (maxPoints <= 0 || maxPoints > _count) ? _count : maxPoints;
        var result = new TrendPoint[take];
        var offset = _count - take;
        for (var i = 0; i < take; i++)
        {
            result[i] = _items[(_start + offset + i) % _items.Length];
        }

        return result;
    }
}
