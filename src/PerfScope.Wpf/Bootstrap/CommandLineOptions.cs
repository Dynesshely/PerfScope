using System.Globalization;
using PerfScope.Core;

namespace PerfScope.App.Bootstrap;

/// <summary>命令行参数解析结果。两个入口（WinForms 过渡期 / WPF 正式版）共用同一份解析逻辑。</summary>
internal sealed class CommandLineOptions
{
    /// <summary>数据目录（<c>--data-dir</c>），为空表示用默认的 %LOCALAPPDATA%\PerfScope。</summary>
    public string? DataDirectory { get; private init; }

    /// <summary>演示模式：不触碰真实数据目录与注册表。</summary>
    public bool Demo { get; private init; }

    /// <summary>截图模式：使用虚构数据。</summary>
    public bool Mock { get; private init; }

    /// <summary>强制界面语言（zh / en），为空表示跟随设置。</summary>
    public string? Language { get; private init; }

    /// <summary>自检模式秒数（到点自动退出），0 表示正常常驻。</summary>
    public int SelfTestSeconds { get; private init; }

    /// <summary>本次是提权后的第二次启动。</summary>
    public bool Elevated { get; private init; }

    /// <summary>解析命令行。已经解析并应用过 data-dir 副作用之外的参数都放在这里。</summary>
    public static CommandLineOptions Parse(string[] args)
    {
        string? dataDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dataDir = args[i + 1];
                break;
            }

            const string prefix = "--data-dir=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                dataDir = args[i][prefix.Length..];
                break;
            }
        }

        return new CommandLineOptions
        {
            DataDirectory = dataDir,
            Demo = HasFlag(args, "--demo"),
            Mock = HasFlag(args, "--mock"),
            Language = ValueOf(args, "--lang"),
            SelfTestSeconds = ParseSelfTestSeconds(args),
            Elevated = HasFlag(args, Infrastructure.Elevation.RelaunchArgument),
        };
    }

    /// <summary>把 <c>--data-dir</c> 落到环境变量（Core 的 AppPaths 读它）。必须在任何 Core 调用之前执行。</summary>
    public void ApplyDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, DataDirectory, EnvironmentVariableTarget.Process);
        }
    }

    /// <summary>取 <c>--name value</c> 形式的字符串参数。</summary>
    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ParseSelfTestSeconds(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
            {
                return Math.Clamp(seconds, 1, 3600);
            }

            const string prefix = "--selftest=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i][prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int inline) && inline > 0)
            {
                return Math.Clamp(inline, 1, 3600);
            }
        }

        return 0;
    }
}
