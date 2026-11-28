namespace PerfScope.App.ViewModels;

/// <summary>下拉框选项：显示文本 + 实际枚举值。界面只认 <see cref="Display"/>，逻辑只认 <see cref="Value"/>。</summary>
public sealed class OptionItem<T>
{
    /// <summary>创建选项。</summary>
    public OptionItem(T value, string display)
    {
        Value = value;
        Display = display;
    }

    /// <summary>实际值。</summary>
    public T Value { get; }

    /// <summary>显示文本（已本地化）。</summary>
    public string Display { get; }

    /// <inheritdoc />
    public override string ToString() => Display;
}
