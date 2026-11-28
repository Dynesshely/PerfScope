using System.Reflection;

namespace PerfScope.App.Infrastructure;

/// <summary>应用版本号的唯一读取点（界面标题栏、关于页、更新检查都从这里取）。</summary>
internal static class AppVersionInfo
{
    private static readonly Lazy<string> LazyVersion = new(ReadVersion);

    /// <summary>形如 <c>0.2.0</c> 的语义化版本字符串。</summary>
    public static string Version => LazyVersion.Value;

    /// <summary>形如 <c>v0.2.0</c> 的展示文本。</summary>
    public static string Display => "v" + Version;

    private static string ReadVersion()
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // .NET 可能会在版本号后追加 +commit，这里只取主版本部分
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
