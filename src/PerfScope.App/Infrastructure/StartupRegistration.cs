using System.Diagnostics;
using Microsoft.Win32;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 开机启动。默认使用「计划任务 + 最高权限」，这样开机后依然以管理员身份运行
/// （HKCU\...\Run 无法提权，UAC 会直接以普通权限拉起进程）。
/// 计划任务创建失败时退回注册表 Run 项，保证功能至少可用（只是不带提权）。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PerfScope";
    private const string TaskName = "PerfScope";

    /// <summary>当前 exe 完整路径（用于写入注册表 / 计划任务）。</summary>
    public static string ExecutablePath
    {
        get
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath!;
            }

            return Application.ExecutablePath;
        }
    }

    /// <summary>是否已启用（计划任务或注册表任一存在即视为启用）。</summary>
    public static bool IsEnabled() => TaskExists() || RunValueExists();

    /// <summary>新增或删除自启项。失败时返回 <c>false</c> 并给出中文原因。</summary>
    public static bool TryApply(bool enable, out string? error)
    {
        error = null;
        if (enable)
        {
            if (TryCreateTask(out string? taskError))
            {
                RemoveRunValue();
                return true;
            }

            // 计划任务不可用（例如策略禁用）时退回 Run 项：不报错，只是不带提权
            if (TryWriteRunValue(out string? runError))
            {
                error = null;
                return true;
            }

            error = taskError ?? runError ?? "写入自启项失败。";
            return false;
        }

        DeleteTask();
        RemoveRunValue();
        return true;
    }

    private static bool TaskExists()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN " + TaskName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(4000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunValueExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建「登录时运行 + 最高权限」的计划任务；需要管理员权限。</summary>
    private static bool TryCreateTask(out string? error)
    {
        error = null;
        try
        {
            // 用 XML 定义才能指定 HighestAvailable 与「登录时」触发器
            string xml = "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                         "<RegistrationInfo><Description>PerfScope 应用级资源监控</Description></RegistrationInfo>" +
                         "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>" +
                         "<Principals><Principal id=\"Author\"><GroupId>S-1-5-32-544</GroupId>" +
                         "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                         "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                         "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                         "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                         "<AllowHardTerminate>true</AllowHardTerminate>" +
                         "<StartWhenAvailable>true</StartWhenAvailable>" +
                         "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>" +
                         "<Actions Context=\"Author\"><Exec><Command>" + EscapeXml(ExecutablePath) + "</Command></Exec></Actions>" +
                         "</Task>";

            string temp = Path.Combine(Path.GetTempPath(), "perfscope-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(temp, xml, System.Text.Encoding.Unicode);
            try
            {
                int exitCode = RunSchtasks("/Create /TN " + TaskName + " /XML \"" + temp + "\" /F");
                if (exitCode == 0)
                {
                    return true;
                }

                error = "创建计划任务失败（exit " + exitCode + "），已退回注册表自启。";
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 临时文件删不掉不影响功能
                }
            }
        }
        catch (Exception ex)
        {
            error = "创建计划任务异常：" + ex.Message;
            return false;
        }
    }

    private static void DeleteTask()
    {
        try
        {
            RunSchtasks("/Delete /TN " + TaskName + " /F");
        }
        catch
        {
            // 删除失败忽略：没有任务时 schtasks 也会返回非 0
        }
    }

    private static int RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return -1;
        }

        if (!process.WaitForExit(8000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略
            }

            return -1;
        }

        return process.ExitCode;
    }

    private static bool TryWriteRunValue(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开注册表自启项（HKCU Run）。";
                return false;
            }

            key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = "写入注册表自启项失败：" + ex.Message;
            return false;
        }
    }

    private static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 忽略
        }
    }

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
}
