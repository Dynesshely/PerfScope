using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 权限提升：默认以管理员身份运行。
/// 理由：非管理员读不到受保护进程的可执行路径与 CPU 时间，统计会偏小且无法对账任务管理器。
/// 提权失败（用户点「否」、UAC 被策略禁用、或当前是子系统/自动化环境）时不得让程序不可用，
/// 而是按普通权限继续运行，并在状态栏给出「权限受限」提示。
/// </summary>
internal static class Elevation
{
    /// <summary>内部参数：表示本次是提权后的第二次启动，避免无限重试。</summary>
    public const string RelaunchArgument = "--elevated";

    /// <summary>当前进程是否已具备管理员权限。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试以管理员身份重新启动自己。返回 <c>true</c> 表示新进程已启动、当前进程应退出；
    /// 返回 <c>false</c> 表示放弃提权（原因写入 <paramref name="failureReason"/>），调用方按普通权限继续。
    /// </summary>
    public static bool TryRelaunchElevated(string[] args, out string? failureReason)
    {
        failureReason = null;
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                failureReason = "拿不到可执行文件路径";
                return false;
            }

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = BuildArguments(args),
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED，用户在 UAC 对话框上点了「否」
            failureReason = "用户取消了 UAC 提权";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    /// <summary>把原始参数原样带上，并补上内部标记，避免提权后的新进程再次尝试提权。</summary>
    private static string BuildArguments(string[] args)
    {
        var parts = new List<string>(args.Length + 1);
        foreach (var arg in args)
        {
            if (string.Equals(arg, RelaunchArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add(QuoteIfNeeded(arg));
        }

        parts.Add(RelaunchArgument);
        return string.Join(' ', parts);
    }

    private static string QuoteIfNeeded(string value)
        => value.Length > 0 && (value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal))
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}
