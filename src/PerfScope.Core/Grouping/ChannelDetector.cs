namespace PerfScope.Core.Grouping;

/// <summary>
/// 识别「同一个产品的不同发行通道」：Edge / Edge Beta / Edge Dev、Chrome / Chrome Canary 等，
/// 它们的 ProductName 与 CompanyName 完全一样，但安装目录里带通道标记。
/// 归并时把通道并进 Key，避免把稳定版和测试版算成同一个应用。
/// </summary>
public static class ChannelDetector
{
    private static readonly (string Token, string Label)[] Tokens =
    {
        ("canary", "Canary"),
        ("sxs", "Canary"),
        ("beta", "Beta"),
        ("dev", "Dev"),
        ("preview", "Preview"),
        ("insider", "Insider"),
        ("nightly", "Nightly"),
    };

    /// <summary>
    /// 从可执行文件路径推断发行通道；识别不到返回 <c>null</c>。
    /// 只看目录段（忽略文件名），从最深的一层往上找，命中第一个词即返回。
    /// </summary>
    public static string? Detect(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(executablePath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var segments = directory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            var words = SplitWords(segments[i]);
            foreach (var (token, label) in Tokens)
            {
                foreach (var word in words)
                {
                    if (string.Equals(word, token, StringComparison.OrdinalIgnoreCase))
                    {
                        return label;
                    }
                }
            }
        }

        return null;
    }

    private static List<string> SplitWords(string segment)
    {
        var words = new List<string>(4);
        var current = new System.Text.StringBuilder(16);
        foreach (char ch in segment)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
