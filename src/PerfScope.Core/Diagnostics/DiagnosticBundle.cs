using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfScope.Core.Diagnostics;

/// <summary>
/// 一键导出诊断包：把日志、设置快照、最近事件与环境信息打包成一个 zip，方便用户反馈问题。
/// 所有内容都会经过脱敏（用户名、机器名、用户目录路径），不含任何可执行文件与原始采样。
/// </summary>
public static class DiagnosticBundle
{
    private static readonly Regex UserProfilePattern = new(@"[A-Za-z]:\\Users\\[^\\/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LegacyProfilePattern = new(@"[A-Za-z]:\\Documents and Settings\\[^\\/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>生成诊断包，返回 zip 路径；失败返回 <c>null</c>。</summary>
    /// <param name="rootDirectory">数据目录，默认 <see cref="AppPaths.DataDirectory"/>。</param>
    /// <param name="machineSummary">可选的采样概要文本（例如进程数、无路径进程数），由调用方提供。</param>
    public static string? Create(string? rootDirectory = null, string? machineSummary = null)
    {
        try
        {
            string root = rootDirectory ?? AppPaths.DataDirectory;
            string outputDirectory = Path.Combine(root, "diagnostics");
            Directory.CreateDirectory(outputDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string zipPath = Path.Combine(outputDirectory, "perfscope-diag-" + stamp + ".zip");

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            WriteText(archive, "environment.txt", BuildEnvironmentText(machineSummary));
            WriteText(archive, "settings-redacted.json", BuildSettingsSnapshot(root));
            WriteText(archive, "insights-recent.jsonl", BuildRecentInsights(root));

            foreach (var logFile in EnumerateLogs(root))
            {
                WriteText(archive, "logs/" + Path.GetFileName(logFile), ReadTail(logFile, 512 * 1024));
            }

            return zipPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>脱敏：把用户名、机器名与用户目录替换成占位符。</summary>
    public static string Redact(string? text, string? userName = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        var user = string.IsNullOrWhiteSpace(userName) ? SafeUserName() : userName;
        var machine = string.IsNullOrWhiteSpace(machineName) ? SafeMachineName() : machineName;
        try
        {
            // 先替换机器名：机器名常常以用户名开头（ALICE-PC / alice），先替换用户名会把机器名切碎
            if (!string.IsNullOrEmpty(machine))
            {
                result = result.Replace(machine!, "%COMPUTERNAME%", StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrEmpty(user))
            {
                result = result.Replace(user!, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 替换失败就继续用正则兜底
        }

        result = UserProfilePattern.Replace(result, "%USERPROFILE%");
        result = LegacyProfilePattern.Replace(result, "%USERPROFILE%");
        return result;
    }

    private static string BuildEnvironmentText(string? machineSummary)
    {
        var builder = new StringBuilder(1024);
        builder.AppendLine("PerfScope diagnostic bundle");
        builder.AppendLine("generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("version: " + AppPaths.Version);
        builder.AppendLine("os: " + Environment.OSVersion.VersionString);
        builder.AppendLine("os-arch: " + RuntimeInformation.OSArchitecture);
        builder.AppendLine("process-arch: " + RuntimeInformation.ProcessArchitecture);
        builder.AppendLine("clr: " + Environment.Version);
        builder.AppendLine("framework: " + RuntimeInformation.FrameworkDescription);
        builder.AppendLine("culture: " + System.Globalization.CultureInfo.CurrentUICulture.Name);
        builder.AppendLine("logical-processors: " + Environment.ProcessorCount);
        builder.AppendLine("is-64bit-os: " + Environment.Is64BitOperatingSystem);
        builder.AppendLine("data-directory: " + Redact(AppPaths.DataDirectory));
        builder.AppendLine();
        builder.AppendLine("--- elevated ---");
        builder.AppendLine(IsElevated() ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(machineSummary))
        {
            builder.AppendLine();
            builder.AppendLine("--- sampling summary ---");
            builder.AppendLine(Redact(machineSummary));
        }

        return builder.ToString();
    }

    private static string BuildSettingsSnapshot(string root)
    {
        try
        {
            string path = Path.Combine(root, "settings.json");
            if (!File.Exists(path))
            {
                return "settings.json not found";
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            // 自定义规则里可能有用户自己的路径，整体替换成占位，避免带出无关信息
            json = Regex.Replace(json, "\"rules\"\\s*:\\s*\\[.*?\\]", "\"rules\": \"[redacted]\"", RegexOptions.Singleline);
            return Redact(json);
        }
        catch (Exception ex)
        {
            return "settings snapshot failed: " + ex.GetType().Name;
        }
    }

    private static string BuildRecentInsights(string root)
    {
        try
        {
            string directory = Path.Combine(root, "insights");
            if (!Directory.Exists(directory))
            {
                return "no insights";
            }

            var file = Directory.EnumerateFiles(directory, "*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return file is null ? "no insights" : ReadTail(file, 256 * 1024);
        }
        catch
        {
            return "insights unreadable";
        }
    }

    private static IEnumerable<string> EnumerateLogs(string root)
    {
        string logs = Path.Combine(root, "logs");
        if (!Directory.Exists(logs))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(logs, "*.log"))
        {
            yield return file;
        }

        string crash = Path.Combine(logs, "crash.log");
        if (File.Exists(crash))
        {
            yield return crash;
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Redact(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            return "unreadable: " + ex.GetType().Name;
        }
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(Redact(content));
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Environment.IsPrivilegedProcess;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return string.Empty;
        }
    }
}