using System.Collections.ObjectModel;
using PerfScope.App.Mvvm;

namespace PerfScope.App.ViewModels;

/// <summary>趋势曲线上的一个样本点（归一化坐标由视图层换算，VM 只存原始值）。</summary>
public sealed class TrendSampleViewModel
{
    /// <summary>创建样本。</summary>
    public TrendSampleViewModel(int index, double value)
    {
        Index = index;
        Value = value;
    }

    /// <summary>样本序号（0 最旧）。</summary>
    public int Index { get; }

    /// <summary>原始值（CPU 为百分比，内存为百分比）。</summary>
    public double Value { get; }
}

/// <summary>
/// 一条趋势曲线：固定容量的滚动窗口。视图把它换算成折线；
/// 由于 <see cref="ObservableCollection{T}"/> 的增删会触发视图更新，这里每帧只做一次「移除最旧 + 追加最新」。
/// </summary>
public sealed class TrendSeriesViewModel : ObservableObject
{
    private readonly int _capacity;
    private int _totalPushed;
    private double _maxValue = 100;
    private double _minValue = 0;

    /// <summary>创建容量为 <paramref name="capacity"/> 点的趋势曲线。</summary>
    public TrendSeriesViewModel(int capacity = 60)
    {
        _capacity = Math.Max(2, capacity);
    }

    /// <summary>当前样本点（按时间升序）。</summary>
    public ObservableCollection<TrendSampleViewModel> Points { get; } = new();

    /// <summary>纵轴上限（用于把值换算成 0~1 的比例）。</summary>
    public double MaxValue
    {
        get => _maxValue;
        private set => SetProperty(ref _maxValue, value);
    }

    /// <summary>纵轴下限。</summary>
    public double MinValue
    {
        get => _minValue;
        private set => SetProperty(ref _minValue, value);
    }

    /// <summary>最新值。</summary>
    public double Latest => Points.Count == 0 ? 0 : Points[^1].Value;

    /// <summary>追加一个样本；超出容量时丢弃最旧的。</summary>
    public void Push(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            value = 0;
        }

        if (Points.Count >= _capacity)
        {
            Points.RemoveAt(0);
        }

        Points.Add(new TrendSampleViewModel(_totalPushed++, value));
        MaxValue = Math.Max(100d, Math.Ceiling(value / 10d) * 10d);
        if (value < 0)
        {
            MinValue = value;
        }
    }

    /// <summary>清空曲线（重新开始采样时调用）。</summary>
    public void Clear()
    {
        Points.Clear();
        MaxValue = 100;
        MinValue = 0;
    }
}
