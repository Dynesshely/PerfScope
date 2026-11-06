namespace PerfScope.Core.Updates;

/// <summary>
/// 语义化版本（只覆盖 PerfScope 实际用到的部分：主.次.补丁 + 可选预发布后缀 + 可选 build 元数据）。
/// 用于「本地版本 vs Releases 最新 tag」的比较，不追求完整的 SemVer 排序细则。
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<AppVersion>
{
    /// <summary>尝试解析版本字符串（允许 v 前缀、允许 1.2、允许 1.2.3-beta.1、忽略 +build）。</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        int plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? pre = null;
        int dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = value[(dash + 1)..];
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    /// <summary>解析失败时返回 <see cref="AppVersion"/> 0.0.0。</summary>
    public static AppVersion Parse(string? text) => TryParse(text, out var parsed) ? parsed : default;

    /// <inheritdoc />
    public int CompareTo(AppVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // 正式版高于同号预发布（1.0.0 > 1.0.0-beta）；同为预发布时按字符串比较，够用且稳定
        bool selfPre = !string.IsNullOrEmpty(PreRelease);
        bool otherPre = !string.IsNullOrEmpty(other.PreRelease);
        if (selfPre != otherPre)
        {
            return selfPre ? -1 : 1;
        }

        return selfPre
            ? string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase)
            : 0;
    }

    /// <summary>格式化为最短形式（预发布保留后缀）。</summary>
    public override string ToString()
        => Major + "." + Minor + "." + Patch + (string.IsNullOrEmpty(PreRelease) ? string.Empty : "-" + PreRelease);

    /// <summary>判断 <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var newer) || !TryParse(current, out var older))
        {
            return false;
        }

        return newer.CompareTo(older) > 0;
    }
}
