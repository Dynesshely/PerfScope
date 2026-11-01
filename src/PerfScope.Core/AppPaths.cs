using System.Reflection;

namespace PerfScope.Core;

/// <summary>
/// 所有落盘位置的统一出口，避免各模块各写一套路径。
/// 数据目录默认是 <c>%LOCALAPPDATA%\PerfScope</c>；设置环境变量 <c>PERFSCOPE_DATA_DIR</c>
/// 可切到指定目录（便携模式 / 多套配置 / 自动化测试），不会污染用户正式数据。
/// </summary>
public static class AppPaths
{
    /// <summary>可覆盖数据目录的环境变量名。</summary>
    public const string DataDirectoryVariable = "PERFSCOPE_DATA_DIR";

    private static readonly Lazy<string> LazyDataDirectory = new(() =>
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                return Path.GetFullPath(overridden);
            }
            catch
            {
                // 路径非法时退回默认位置，不要让程序起不来
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfScope");
    });

    /// <summary>数据根目录。</summary>
    public static string DataDirectory => LazyDataDirectory.Value;

    /// <summary>设置文件。</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>历史数据目录（按天一个文件）。</summary>
    public static string HistoryDirectory => Path.Combine(DataDirectory, "history");

    /// <summary>应用 ID 名称表。</summary>
    public static string AppCatalogFile => Path.Combine(HistoryDirectory, "apps.json");

    /// <summary>日志目录。</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>日志文件。</summary>
    public static string LogFile => Path.Combine(LogDirectory, "perfscope.log");

    /// <summary>确保所有目录存在。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>当前程序版本号字符串。</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppPaths).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info is not null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                var value = info.InformationalVersion;
                var plus = value.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? value[..plus] : value;
            }

            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
