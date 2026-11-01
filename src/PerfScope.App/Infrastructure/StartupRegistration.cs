using Microsoft.Win32;

namespace PerfScope.App.Infrastructure;

/// <summary>开机启动（HKCU\...\Run），不需要管理员权限。</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PerfScope";

    /// <summary>当前 exe 完整路径（用于写入注册表）。</summary>
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

    /// <summary>注册表里是否已有自启项。</summary>
    public static bool IsEnabled()
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

    /// <summary>新增或删除自启项。失败时返回 <c>false</c> 并给出中文原因。</summary>
    public static bool TryApply(bool enable, out string? error)
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

            if (enable)
            {
                key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "写入开机启动失败：" + ex.Message;
            return false;
        }
    }
}
