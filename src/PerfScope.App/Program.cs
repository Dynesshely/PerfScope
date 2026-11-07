using PerfScope.Core;

namespace PerfScope.App;

/// <summary>PerfScope 入口：单实例、托盘常驻，支持 <c>--demo</c> 与 <c>--selftest &lt;秒&gt;</c>。</summary>
internal static class Program
{
    private const string MutexName = @"Local\PerfScope.SingleInstance";
    private const string MutexNameDemo = @"Local\PerfScope.SingleInstance.Demo";

    private static bool _isDemo;

    /// <summary>进程入口。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // 便携模式：--data-dir 必须在任何 Core 组件读取 AppPaths 之前生效。
        ApplyDataDirArgument(args);

        int selfTestSeconds = ParseSecondsArgument(args, "--selftest");
        _isDemo = HasFlag(args, "--demo") || HasFlag(args, "--mock");
        bool isMock = HasFlag(args, "--mock");
        Infrastructure.UiDebug.HoverDemo = HasFlag(args, "--hover-demo");
        Infrastructure.UiDebug.OpenAgentOnStart = HasFlag(args, "--open-agent");
        Infrastructure.UiDebug.OpenSettingsOnStart = HasFlag(args, "--open-settings");
        Infrastructure.UiDebug.SettingsTabIndex = ParseIntArgument(args, "--settings-tab", -1);
        Infrastructure.UiDebug.ExpandFirstGroup = HasFlag(args, "--expand-first");
        Infrastructure.UiDebug.ForceListTooltip = HasFlag(args, "--list-tip-demo");
        Infrastructure.UiDebug.ShowFirstRunTips = HasFlag(args, "--tips-demo");
        Infrastructure.UiDebug.ForceHighContrast = HasFlag(args, "--force-high-contrast");

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        string mutexName = _isDemo ? MutexNameDemo : MutexName;
        using var mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance(_isDemo);
            return 0;
        }

        AppHost host;
        try
        {
            host = AppHost.Create(_isDemo, isMock, ParseStringArgument(args, "--lang"));
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            return 1;
        }

        using (host)
        {
            using var context = new TrayAppContext(host, _isDemo, selfTestSeconds);
            Application.Run(context);
        }

        // 托盘应用必须确定性退出：即使有后台线程遗留（例如采样线程未设为后台线程）也不留在进程里
        Environment.Exit(0);
        return 0;
    }

    /// <summary>解析 <c>--data-dir &lt;目录&gt;</c>，把数据目录切到指定位置（便携模式 / 多套配置）。</summary>
    private static void ApplyDataDirArgument(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, args[i + 1], EnvironmentVariableTarget.Process);
                return;
            }

            const string prefix = "--data-dir=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, arg[prefix.Length..], EnvironmentVariableTarget.Process);
                return;
            }
        }
    }

    /// <summary>解析 <c>--name value</c> 形式的字符串参数，缺省返回 null。</summary>
    private static string? ParseStringArgument(string[] args, string name)
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

    /// <summary>解析 <c>--name 123</c> 形式的整数参数，缺省时返回 <paramref name="fallback"/>。</summary>
    private static int ParseIntArgument(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ParseSecondsArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int seconds) && seconds > 0)
                {
                    return Math.Clamp(seconds, 1, 3600);
                }
            }

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[(name.Length + 1)..], out int inlineSeconds) &&
                inlineSeconds > 0)
            {
                return Math.Clamp(inlineSeconds, 1, 3600);
            }
        }

        return 0;
    }

    private static void ActivateExistingInstance(bool demo)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(TrayAppContext.ActivateEventNameFor(demo));
            handle.Set();
        }
        catch
        {
            // 已有实例可能刚退出，忽略即可
        }
    }

    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        if (_isDemo)
        {
            return;
        }

        MessageBox.Show(
            "界面线程发生未处理异常，程序已记录日志。" + Environment.NewLine + Environment.NewLine + e.Exception.Message,
            "PerfScope",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void ReportStartupFailure(Exception exception)
    {
        WriteCrashLog(exception);
        if (_isDemo)
        {
            return;
        }

        MessageBox.Show(
            "PerfScope 启动失败：" + Environment.NewLine + Environment.NewLine + exception.Message +
            Environment.NewLine + Environment.NewLine +
            "常见原因：数据目录没有写入权限（" + AppPaths.DataDirectory + "）。" + Environment.NewLine +
            "如果是集成阶段核心实现还没就绪，可以先用 PerfScope.exe --demo 查看界面。",
            "PerfScope",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            string directory = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                " " + exception + Environment.NewLine);
        }
        catch
        {
            // 日志写入失败时静默
        }
    }
}
