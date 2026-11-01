namespace PerfScope.App.Infrastructure;

/// <summary>
/// 定长环形缓冲，用于 Sparkline 与迷你趋势。写入不加锁：
/// 只在 UI 线程读、在 UI 线程投递的帧处理里写，天然单线程。
/// </summary>
internal sealed class TrendRing
{
    private readonly double[] _buffer;
    private readonly double[] _scratch;
    private int _next;
    private int _count;

    /// <summary>创建一个容量为 <paramref name="capacity"/> 的环形缓冲。</summary>
    public TrendRing(int capacity)
    {
        if (capacity < 2)
        {
            capacity = 2;
        }

        _buffer = new double[capacity];
        _scratch = new double[capacity];
        _count = 0;
        _next = 0;
    }

    /// <summary>容量上限。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>当前已填充的点数。</summary>
    public int Count => _count;

    /// <summary>追加一个点，超出容量时覆盖最旧的点。</summary>
    public void Push(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            value = 0;
        }

        _buffer[_next] = value;
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length)
        {
            _count++;
        }
    }

    /// <summary>
    /// 取按时间升序排列的点。复用内部临时数组，返回的数组在下一次调用前有效；
    /// 调用方用于即时绘制，不要长期持有。
    /// </summary>
    public double[] Chronological()
    {
        int n = _count;
        if (n == 0)
        {
            return _scratch;
        }

        int start = (_next - n + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < n; i++)
        {
            _scratch[i] = _buffer[(start + i) % _buffer.Length];
        }

        return _scratch;
    }

    /// <summary>清空所有点。</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
    }
}
