using System.Globalization;

namespace PerfScope.App.Infrastructure;

/// <summary>界面上的数值格式化，统一单位与小数位，避免各处写法不一致。</summary>
internal static class FormatUtil
{
    private const double Kilo = 1024d;
    private const double Mega = Kilo * 1024d;
    private const double Giga = Mega * 1024d;

    /// <summary>CPU 百分比，形如 <c>23.4%</c>。</summary>
    public static string Percent(double value)
    {
        double v = double.IsFinite(value) ? Math.Max(0d, value) : 0d;
        return v.ToString("F1", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>字节容量，自动选择 KB / MB / GB。</summary>
    public static string Bytes(long bytes) => Bytes((double)bytes);

    /// <summary>字节容量，自动选择 KB / MB / GB。</summary>
    public static string Bytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes <= 0)
        {
            return "0 B";
        }

        if (bytes >= Giga)
        {
            return (bytes / Giga).ToString("F1", CultureInfo.CurrentCulture) + " GB";
        }

        if (bytes >= Mega)
        {
            return (bytes / Mega).ToString("F1", CultureInfo.CurrentCulture) + " MB";
        }

        if (bytes >= Kilo)
        {
            return (bytes / Kilo).ToString("F0", CultureInfo.CurrentCulture) + " KB";
        }

        return bytes.ToString("F0", CultureInfo.CurrentCulture) + " B";
    }

    /// <summary>速率（字节/秒），形如 <c>1.2 MB/s</c>。</summary>
    public static string BytesPerSecond(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return "0 B/s";
        }

        return Bytes(bytesPerSecond) + "/s";
    }

    /// <summary>把进程数格式化为纯数字文本。</summary>
    public static string Count(int value) => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>把 0~1 的比例转成百分比文本。</summary>
    public static string Ratio(double ratio) => Percent(ratio * 100d);
}
