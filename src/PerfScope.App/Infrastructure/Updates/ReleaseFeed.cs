using System.Text.Json;

namespace PerfScope.App.Infrastructure.Updates;

/// <summary>GitHub Releases 上的一次发布（只保留更新流程用得到的字段）。</summary>
internal sealed record ReleaseInfo(
    string Tag,
    string? Name,
    string? Notes,
    string? ReleaseUrl,
    string? PortableZipUrl,
    string? SetupExeUrl,
    string? Sha256SumsUrl,
    DateTimeOffset? PublishedAt);

/// <summary>
/// 读取 GitHub Releases 最新版本。这是 PerfScope 唯一的网络访问点：
/// 只发一次 GET、不带任何标识信息、失败静默——离线环境下功能完全不受影响。
/// </summary>
internal static class ReleaseFeed
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Dynesshely/PerfScope/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PerfScope-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>拉取最新发布；失败（离线 / 限流 / 解析错误）返回 <c>null</c>。</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 Releases API 的 JSON（抽成静态方法便于单测与容错）。</summary>
    internal static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            string? portable = null;
            string? setup = null;
            string? sums = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = GetString(asset, "name");
                    string? url = GetString(asset, "browser_download_url");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    if (name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        portable = url;
                    }
                    else if (name.StartsWith("PerfScope-Setup-", StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setup = url;
                    }
                    else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sums = url;
                    }
                }
            }

            DateTimeOffset? published = null;
            string? publishedText = GetString(root, "published_at");
            if (DateTimeOffset.TryParse(publishedText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                published = parsed;
            }

            return new ReleaseInfo(
                tag!,
                GetString(root, "name"),
                GetString(root, "body"),
                GetString(root, "html_url"),
                portable,
                setup,
                sums,
                published);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
