using System.Text;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 纯字符串的路径工具：不访问文件系统、不调用 <see cref="Path.GetFullPath(string)"/>（那会读盘甚至抛异常），
/// 只做规范化与比较。归并引擎要在每帧的热路径上工作，这里所有方法都必须是 O(路径长度)。
/// </summary>
internal static class PathText
{
    private const string UncPrefix = @"\\";

    /// <summary>
    /// 规范化可执行文件路径：去引号与首尾空白、统一分隔符、展开环境变量、剥离 NT 前缀
    /// （<c>\??\</c> / <c>\\?\</c> / <c>\SystemRoot\</c>），折叠重复分隔符，去掉末尾反斜杠。
    /// 输入为空时返回 <see cref="string.Empty"/>。
    /// </summary>
    /// <param name="rawPath">原始路径。</param>
    /// <param name="windowsDirectory">用于展开 <c>\SystemRoot\</c> 前缀的 Windows 目录，可为 <c>null</c>。</param>
    public static string Normalize(string? rawPath, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var text = rawPath!.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\''))
        {
            var quote = text[0];
            var end = text.IndexOf(quote, 1);
            text = end > 0 ? text.Substring(1, end - 1) : text.Trim(quote);
            text = text.Trim();
        }

        text = text.Replace('/', '\\');

        if (text.IndexOf('%') >= 0)
        {
            try
            {
                text = Environment.ExpandEnvironmentVariables(text);
            }
            catch
            {
                // 展开失败就按原样继续，绝不因为一个奇怪的路径把归类搞崩。
            }
        }

        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            text = UncPrefix + text.Substring(8);
        }
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text.Substring(4);
        }
        else if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text.Substring(4);
        }

        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            var win = TrimTrailingSeparators(windowsDirectory.Replace('/', '\\'));
            if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                text = Combine(win, text.Substring(12));
            }
            else if (text.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                text = Combine(win, text.Substring(11));
            }
        }

        text = text.TrimEnd(' ');
        text = CollapseSeparators(text);
        return TrimTrailingSeparators(text);
    }

    /// <summary>取文件所在目录；没有目录部分时返回 <see cref="string.Empty"/>。</summary>
    public static string DirectoryOf(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        var path = TrimTrailingSeparators(filePath);
        var index = path.LastIndexOf('\\');
        if (index < 0)
        {
            return string.Empty;
        }

        if (index == 0)
        {
            return @"\";
        }

        var directory = path.Substring(0, index);
        if (directory.Length == 2 && directory[1] == ':')
        {
            return directory + "\\";
        }

        return directory;
    }

    /// <summary>取上一级目录；已在盘符根（或 UNC 根）时返回 <c>null</c>。</summary>
    public static string? Parent(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var path = TrimTrailingSeparators(directory);
        if (path.Length <= 3 && path.Length >= 2 && path[1] == ':')
        {
            return null;
        }

        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var index = path.LastIndexOf('\\');
        if (index < 0)
        {
            return null;
        }

        if (index <= 2)
        {
            return path.Length >= 2 && path[1] == ':' ? path.Substring(0, 2) + "\\" : null;
        }

        return path.Substring(0, index);
    }

    /// <summary>拼接两个路径片段（自动补一个反斜杠）。</summary>
    public static string Combine(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return TrimTrailingSeparators((right ?? string.Empty).Replace('/', '\\'));
        }

        if (string.IsNullOrEmpty(right))
        {
            return TrimTrailingSeparators(left.Replace('/', '\\'));
        }

        return TrimTrailingSeparators(left.TrimEnd('\\') + "\\" + right.TrimStart('\\'));
    }

    /// <summary><paramref name="path"/> 是否等于 <paramref name="root"/> 或位于其下（不区分大小写，按目录边界判断）。</summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var normalizedRoot = TrimTrailingSeparators(root!);
        if (normalizedRoot.Length == 0)
        {
            return false;
        }

        if (!path!.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == normalizedRoot.Length)
        {
            return true;
        }

        if (path[normalizedRoot.Length] == '\\')
        {
            return true;
        }

        // 盘符根（C:\）作为 root 时，相对部分紧跟在分隔符之后。
        return normalizedRoot.Length == 3 && normalizedRoot[1] == ':' && normalizedRoot[2] == '\\';
    }

    /// <summary>取 <paramref name="path"/> 相对 <paramref name="root"/> 的各级目录/文件名；不属于该根时返回空数组。</summary>
    public static string[] SegmentsAfter(string? path, string? root)
    {
        if (!IsUnder(path, root))
        {
            return Array.Empty<string>();
        }

        var normalizedRoot = TrimTrailingSeparators(root!);
        var start = normalizedRoot.Length;
        if (start < path!.Length && path[start] == '\\')
        {
            start++;
        }

        if (start >= path.Length)
        {
            return Array.Empty<string>();
        }

        return path.Substring(start).Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>取最后一段（末级目录名或文件名）；盘符根返回空字符串。</summary>
    public static string LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = TrimTrailingSeparators(path!);
        var index = normalized.LastIndexOf('\\');
        return index < 0 ? normalized : normalized.Substring(index + 1);
    }

    /// <summary>是否是盘符根（例如 <c>C:\</c>）。</summary>
    public static bool IsDriveRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = TrimTrailingSeparators(path!);
        return normalized.Length == 3
            && char.IsLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '\\';
    }

    /// <summary>归并键里使用的路径形式：统一小写，保证跨帧、跨重启稳定。</summary>
    public static string ToKeyPath(string normalizedPath)
    {
        return string.IsNullOrEmpty(normalizedPath) ? string.Empty : normalizedPath.ToLowerInvariant();
    }

    /// <summary>去掉末尾的 <c>.exe</c> 并小写（用于映像名比较）。</summary>
    public static string StripExeExtension(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var text = name!.Trim().ToLowerInvariant();
        return text.EndsWith(".exe", StringComparison.Ordinal) ? text.Substring(0, text.Length - 4) : text;
    }

    /// <summary>去掉末尾反斜杠，但保留盘符根（<c>C:\</c>）。</summary>
    public static string TrimTrailingSeparators(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path!.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            // UNC 路径：只去掉一层尾部反斜杠，避免把 \\server\share 直接削成根。
            return path.Length > 2 ? path.TrimEnd('\\') + (path.EndsWith('\\') ? string.Empty : string.Empty) : path;
        }

        var trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 0)
        {
            return path;
        }

        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }

        return trimmed;
    }

    private static string CollapseSeparators(string text)
    {
        if (text.IndexOf("\\\\", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        if (text.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var previousSeparator = false;
        foreach (var ch in text)
        {
            if (ch == '\\')
            {
                if (previousSeparator)
                {
                    continue;
                }

                previousSeparator = true;
            }
            else
            {
                previousSeparator = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
