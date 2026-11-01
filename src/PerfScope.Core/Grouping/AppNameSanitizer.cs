using System.Text.RegularExpressions;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 展示名清洗与「无意义产品名」判定。集中在这里的目的是：同一套清洗规则同时用于
/// 版本信息里的 ProductName、安装根末段目录名，避免 UI 上出现 <c>Google Chrome™ (x64)</c> 这类噪声。
/// </summary>
internal static class AppNameSanitizer
{
    private static readonly Regex TrademarkRegex = new(@"[™®©]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BitnessBracketRegex = new(
        @"\s*[\(\[\{]\s*(x64|x86|x86_64|amd64|arm64|64-bit|32-bit|64 位|32 位)\s*[\)\]\}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BitnessDashRegex = new(
        @"\s*[-–—]\s*(x64|x86|amd64|arm64|64-bit|32-bit)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPrefixRegex = new(
        @"^(?:v|ver\.?|version)?\s*\d+(?:\.\d+){1,3}[\s._\-–—]+(?=[A-Za-z\u4E00-\u9FFF])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>没有任何区分度的产品名（大小写不敏感）。</summary>
    private static readonly HashSet<string> GenericProductNames = new(StringComparer.Ordinal)
    {
        "microsoft windows operating system",
        "windows operating system",
        "microsoft windows",
        "windows",
        "windows 10",
        "windows 11",
        "microsoft corporation",
        "unknown",
        "application",
        "app",
        "setup",
        "installer",
        "program",
        "system",
        "n/a",
        "na",
    };

    private static readonly char[] TrimChars = { ' ', '\t', '-', '–', '—', '|', ',', ';', ':', '\u00A0' };

    /// <summary>清洗展示名：去商标符号、位宽后缀、多余空白与首尾分隔符；清洗后为空返回 <c>null</c>。</summary>
    public static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw!.Trim();
        text = TrademarkRegex.Replace(text, string.Empty);
        text = BitnessBracketRegex.Replace(text, string.Empty);
        text = BitnessDashRegex.Replace(text, string.Empty);
        text = WhitespaceRegex.Replace(text, " ");
        text = text.Trim(TrimChars);

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 清洗安装根末段目录名：在 <see cref="CleanName"/> 基础上再去掉开头的版本号前缀
    /// （例如 <c>1.2.3 AppName</c> → <c>AppName</c>）。清洗后为空返回 <c>null</c>。
    /// </summary>
    public static string? CleanSegment(string? raw)
    {
        var cleaned = CleanName(raw);
        if (cleaned is null)
        {
            return null;
        }

        var stripped = VersionPrefixRegex.Replace(cleaned, string.Empty).Trim(TrimChars);
        return stripped.Length == 0 ? null : stripped;
    }

    /// <summary>
    /// 判断版本信息里的产品名是否「没有区分度」，从而不应该作为归并依据。
    /// </summary>
    /// <param name="cleanedProduct">已清洗的产品名。</param>
    /// <param name="rawProduct">原始产品名。</param>
    /// <param name="version">完整版本信息。</param>
    /// <param name="imageNameLower">映像名（小写，含扩展名）。</param>
    /// <param name="exeBaseLower">映像名（小写，去扩展名）。</param>
    public static bool IsMeaninglessProductName(
        string? cleanedProduct,
        string? rawProduct,
        ExeVersionInfo? version,
        string imageNameLower,
        string exeBaseLower)
    {
        if (string.IsNullOrWhiteSpace(cleanedProduct))
        {
            return true;
        }

        var product = cleanedProduct!;
        var lowered = product.ToLowerInvariant();

        if (GenericProductNames.Contains(lowered))
        {
            return true;
        }

        // 产品名太短（1 个字符）基本没有信息量。
        if (product.Length < 2)
        {
            return true;
        }

        // 产品名不含任何字母（纯版本号 / 纯数字）。
        var hasLetter = false;
        foreach (var ch in product)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                break;
            }
        }

        if (!hasLetter)
        {
            return true;
        }

        // 产品名与文件名相同 —— 等于没有产品名。
        if (string.Equals(product, imageNameLower, StringComparison.OrdinalIgnoreCase)
            || string.Equals(product, exeBaseLower, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var originalName = CleanName(version?.OriginalFileName);
        if (originalName is not null && string.Equals(product, originalName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 「Google Chrome」与「chrome」这类只差空格/标点的情形也视为无意义。
        if (CollapseForCompare(product) == exeBaseLower)
        {
            return true;
        }

        // 产品名与公司名相同同样没有区分度（例如都叫 Microsoft Corporation）。
        var company = CleanName(version?.CompanyName);
        if (company is not null && string.Equals(product, company, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 原始产品名若明显是空壳（例如全空白）也放过。
        return string.IsNullOrWhiteSpace(rawProduct);
    }

    /// <summary>把名字压缩成只含字母数字的小写串，用于「产品名≈文件名」这类模糊比较。</summary>
    public static string CollapseForCompare(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(name!.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>构建产物目录 / 纯版本号目录（例如 net10.0-windows、net8.0、net48、v1.2.3、9.9.36-53644）。</summary>
    private static readonly Regex BuildOutputRegex = new(
        @"^(?:net|netfx|netcoreapp)\d*(?:\.\d+)*(?:-windows)?$|^v?\d+(?:\.\d+)+(?:[-_][0-9A-Za-z._]+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>没有区分度的通用容器目录名。</summary>
    private static readonly HashSet<string> NoisyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "bin", "obj", "build", "builds", "debug", "release",
        "dist", "out", "output", "publish", "artifacts", "resources", "current", "versions", "x64", "x86",
        "win-x64", "win-x86", "arm64", "amd64", "temp", "tmp", "programs", "windows",
        "windowsapps", "system32", "syswow64", "program files", "program files (x86)",
        "programdata", "users",
    };

    /// <summary>
    /// 目录名是否「不适合当应用展示名」：构建产物目录（bin / obj / Release / net10.0-windows …）、
    /// 通用容器目录（Application / app / resources / x64 …）或纯版本号目录。
    /// </summary>
    public static bool IsUnusableDirectoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var text = name!.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        return NoisyDirectoryNames.Contains(text) || BuildOutputRegex.IsMatch(text);
    }

    /// <summary>
    /// 从给定目录开始向上找第一个「可用」的目录段（最多 <paramref name="maxLevels"/> 层），
    /// 用于把 <c>…\PerfScope.Cli\bin\Release\net10.0-windows</c> 这类构建产物目录还原成 <c>PerfScope.Cli</c>。
    /// </summary>
    public static string? FindUsableAncestorSegment(string? directory, int maxLevels = 4)
    {
        var current = directory;
        for (var level = 0; level < maxLevels; level++)
        {
            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            var segment = CleanSegment(PathText.LastSegment(current));
            if (segment is not null && !IsUnusableDirectoryName(segment))
            {
                return segment;
            }

            current = PathText.Parent(current);
        }

        return null;
    }
}
