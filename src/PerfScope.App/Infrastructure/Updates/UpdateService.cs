using PerfScope.Core;
using PerfScope.Core.Updates;

namespace PerfScope.App.Infrastructure.Updates;

/// <summary>一次更新检查的结果。</summary>
internal sealed record UpdateCheckResult(
    bool Succeeded,
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    ReleaseInfo? Release);

/// <summary>
/// 更新流程：检查 GitHub Releases → 提示 → 下载 portable zip（可选校验 SHA256）→ 让用户替换。
/// 刻意不做「静默就地替换」：PerfScope 是托盘常驻程序，静默替换正在运行的程序风险高于收益。
/// </summary>
internal sealed class UpdateService
{
    private readonly string _currentVersion;

    public UpdateService(string currentVersion) => _currentVersion = currentVersion;

    /// <summary>检查是否有新版本。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var release = await ReleaseFeed.FetchLatestAsync(cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return new UpdateCheckResult(false, false, _currentVersion, null, null);
        }

        bool newer = AppVersion.IsNewer(release.Tag, _currentVersion);
        return new UpdateCheckResult(true, newer, _currentVersion, release.Tag.TrimStart('v', 'V'), release);
    }

    /// <summary>下载便携版 zip 到本地更新目录，返回保存路径。</summary>
    public async Task<string?> DownloadPortableAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(release.PortableZipUrl))
        {
            return null;
        }

        try
        {
            string directory = Path.Combine(AppPaths.DataDirectory, "updates");
            Directory.CreateDirectory(directory);
            string fileName = "PerfScope-" + release.Tag.TrimStart('v', 'V') + "-win-x64-portable.zip";
            string target = Path.Combine(directory, fileName);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PerfScope-Updater");
            using var response = await client.GetAsync(release.PortableZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(received * 100.0 / total);
                    }
                }
            }

            await VerifyChecksumAsync(client, release, target, cancellationToken).ConfigureAwait(false);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>如果发布里带 SHA256SUMS.txt，则下载并校验；校验失败直接删除文件。</summary>
    private static async Task VerifyChecksumAsync(HttpClient client, ReleaseInfo release, string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(release.Sha256SumsUrl))
        {
            return;
        }

        try
        {
            string sums = await client.GetStringAsync(release.Sha256SumsUrl, cancellationToken).ConfigureAwait(false);
            string fileName = Path.GetFileName(filePath);
            foreach (var line in sums.Split('\n'))
            {
                var trimmed = line.Trim();
                int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
                if (space <= 0)
                {
                    continue;
                }

                string expected = trimmed[..space].Trim().ToLowerInvariant();
                string name = trimmed[(space + 1)..].Trim();
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string actual;
                await using (var stream = File.OpenRead(filePath))
                {
                    actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                }

                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    File.Delete(filePath);
                    throw new InvalidOperationException("下载文件校验失败");
                }

                return;
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // 校验资源不可用时保持文件，交给用户人工核对
        }
    }
}
