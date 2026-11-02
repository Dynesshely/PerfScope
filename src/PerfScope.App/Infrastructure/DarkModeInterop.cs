using System.Runtime.InteropServices;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// Win32 深色模式辅助：让系统绘制的滚动条、下拉列表等跟随深色主题。
/// 原理是 uxtheme 的 <c>SetWindowTheme</c>，传 "DarkMode_Explorer" 后滚动条会用深色方案
/// （Win10 1809+ / Win11 均可用），比自绘滚动条稳定得多。
/// </summary>
internal static class DarkModeInterop
{
    /// <summary>递归给控件树套用深色滚动条（只处理可能带滚动条的控件，避免无谓探测）。</summary>
    public static void ApplyScrollbars(Control root, bool dark)
    {
        ApplyTo(root, dark);
    }

    private static void ApplyTo(Control control, bool dark)
    {
        try
        {
            switch (control)
            {
                case ListView list:
                    SetTheme(list.Handle, dark, "DarkMode_Explorer");
                    break;
                case TreeView tree:
                    SetTheme(tree.Handle, dark, "DarkMode_Explorer");
                    break;
                case TextBoxBase text:
                    SetTheme(text.Handle, dark, "DarkMode_CFD");
                    break;
                case Panel panel when panel.AutoScroll:
                case ScrollableControl scrollable when scrollable.AutoScroll:
                    SetTheme(control.Handle, dark, "DarkMode_Explorer");
                    break;
                case ComboBox combo:
                    SetTheme(combo.Handle, dark, "DarkMode_CFD");
                    break;
            }
        }
        catch
        {
            // 主题设置失败不影响功能
        }

        foreach (Control child in control.Controls)
        {
            ApplyTo(child, dark);
        }
    }

    /// <summary>给单个窗口句柄设置深色主题（<paramref name="className"/> 传 "DarkMode_Explorer" / "DarkMode_CFD"）。</summary>
    public static void SetTheme(IntPtr handle, bool dark, string className)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SetWindowTheme(handle, dark ? className : string.Empty, dark ? className : string.Empty);
        }
        catch
        {
            // 忽略
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
}
