namespace PerfScope.Core.Sampling;

/// <summary>
/// 最近 N 个样本的滑动平均（环形缓冲）。窗口变化时就地调整为新窗口并保留最近的样本。
/// </summary>
internal sealed class SmoothingBuffer
{
    private double[] _values = [];
    private int _count;
    private int _head;
    private double _sum;

    /// <summary>当前窗口内的样本数量。</summary>
    public int Count => _count;

    /// <summary>
    /// 压入一个样本并返回窗口平均值。
    /// </summary>
    /// <param name="value">新样本。</param>
    /// <param name="window">窗口长度；小于等于 1 表示不平滑，直接返回原始值。</param>
    public double Push(double value, int window)
    {
        if (window <= 1)
        {
            _count = 0;
            _head = 0;
            _sum = 0;
            return value;
        }

        EnsureWindow(window);

        if (_count == _values.Length)
        {
            _sum -= _values[_head];
        }
        else
        {
            _count++;
        }

        _values[_head] = value;
        _sum += value;
        _head = (_head + 1) % _values.Length;
        return _sum / _count;
    }

    /// <summary>清空窗口（保留已分配的数组）。</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
        _sum = 0;
    }

    private void EnsureWindow(int window)
    {
        if (_values.Length == window)
        {
            return;
        }

        double[] previous = _values;
        if (previous.Length == 0)
        {
            _values = new double[window];
            _count = 0;
            _head = 0;
            _sum = 0;
            return;
        }

        int keep = Math.Min(_count, window);
        var fresh = new double[window];
        for (int index = 0; index < keep; index++)
        {
            int sourceIndex = ((_head - keep + index) % previous.Length + previous.Length) % previous.Length;
            fresh[index] = previous[sourceIndex];
        }

        _values = fresh;
        _count = keep;
        _head = keep % window;
        _sum = 0;
        for (int index = 0; index < keep; index++)
        {
            _sum += fresh[index];
        }
    }
}
