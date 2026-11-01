using System.Buffers.Binary;
using System.Globalization;

namespace PerfScope.Core.History;

/// <summary>
/// 磁盘日历史的一条定长记录（32 字节，小端）。
/// 布局：<c>int32 appId | int32 minuteOfDay | float cpuAvgPercent | float cpuMaxPercent | int64 workingSetPeak | 8 字节保留</c>。
/// 日期不写进记录，由文件名 <c>yyyy-MM-dd.bin</c> 表达。
/// </summary>
internal readonly struct DayRecord
{
    /// <summary>单条记录字节数。</summary>
    public const int Size = 32;

    /// <summary>一天的最大分钟序号（0~1439）。</summary>
    public const int MaxMinuteOfDay = 1439;

    /// <summary>创建记录。</summary>
    public DayRecord(int appId, int minuteOfDay, float cpuAvgPercent, float cpuMaxPercent, long workingSetPeakBytes)
    {
        AppId = appId;
        MinuteOfDay = minuteOfDay;
        CpuAvgPercent = cpuAvgPercent;
        CpuMaxPercent = cpuMaxPercent;
        WorkingSetPeakBytes = workingSetPeakBytes;
    }

    /// <summary>应用目录表里的数字 ID。</summary>
    public int AppId { get; }

    /// <summary>本地时间当天的分钟序号（0~1439）。</summary>
    public int MinuteOfDay { get; }

    /// <summary>该分钟内的 CPU 平均值（百分比）。</summary>
    public float CpuAvgPercent { get; }

    /// <summary>该分钟内的 CPU 峰值（百分比）。</summary>
    public float CpuMaxPercent { get; }

    /// <summary>该分钟内的峰值工作集（字节）。</summary>
    public long WorkingSetPeakBytes { get; }

    /// <summary>把记录编码到 32 字节目标缓冲区。</summary>
    public void Encode(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[..4], AppId);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), MinuteOfDay);
        BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(8, 4), CpuAvgPercent);
        BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(12, 4), CpuMaxPercent);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16, 8), WorkingSetPeakBytes);
        destination.Slice(24, 8).Clear();
    }

    /// <summary>
    /// 从 32 字节缓冲区解码并做基本合法性校验（拒绝崩溃残留的垃圾/零值记录）。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out DayRecord record)
    {
        record = default;
        if (source.Length < Size)
        {
            return false;
        }

        var appId = BinaryPrimitives.ReadInt32LittleEndian(source[..4]);
        var minute = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        var cpuAvg = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(8, 4));
        var cpuMax = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(12, 4));
        var wsPeak = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16, 8));

        if (appId <= 0 || minute < 0 || minute > MaxMinuteOfDay)
        {
            return false;
        }

        if (!float.IsFinite(cpuAvg) || !float.IsFinite(cpuMax) || cpuAvg < 0 || cpuMax < 0 || wsPeak < 0)
        {
            return false;
        }

        record = new DayRecord(appId, minute, cpuAvg, cpuMax, wsPeak);
        return true;
    }

    /// <summary>同一天同一应用同一分钟出现重复记录时（崩溃重启导致），取更保守的较大值合并，避免重复计入时长。</summary>
    public DayRecord MergeMax(in DayRecord other) => new(
        AppId,
        MinuteOfDay,
        Math.Max(CpuAvgPercent, other.CpuAvgPercent),
        Math.Max(CpuMaxPercent, other.CpuMaxPercent),
        Math.Max(WorkingSetPeakBytes, other.WorkingSetPeakBytes));

    /// <summary>日期对应的文件名（<c>yyyy-MM-dd.bin</c>）。</summary>
    public static string BuildFileName(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".bin";

    /// <summary>从文件名解析日期，失败返回 <c>false</c>。</summary>
    public static bool TryParseFileName(string fileName, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        return DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
