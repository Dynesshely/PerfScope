using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace PerfScope.App.Services;

/// <summary>
/// 托盘图标服务（纯 P/Invoke 的 Shell_NotifyIcon，不依赖 WinForms）。
/// 右键菜单用 WPF 的 ContextMenu，跟随主题；左键双击显示主窗口。
/// </summary>
internal sealed class TrayService : IDisposable
{
    private const int WM_TRAYICON = 0x8001;      // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;

    private readonly HwndSource _messageWindow;
    private readonly IntPtr _iconHandle;
    private ContextMenu? _menu;
    private bool _disposed;
    private bool _added;

    /// <summary>创建托盘图标。</summary>
    /// <param name="toolTip">托盘提示文本。</param>
    public TrayService(string toolTip)
    {
        _iconHandle = LoadIconHandle();

        var parameters = new HwndSourceParameters("PerfScope.TrayWindow")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE：只收消息的隐藏窗口
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
        Add(toolTip);
    }

    /// <summary>菜单被请求时触发（由外壳组装菜单项后调用 <see cref="ShowMenu"/>）。</summary>
    public event EventHandler? MenuRequested;

    /// <summary>双击托盘图标时触发。</summary>
    public event EventHandler? ActivateRequested;

    /// <summary>当前菜单（由外壳赋值）。</summary>
    public ContextMenu? Menu
    {
        get => _menu;
        set => _menu = value;
    }

    /// <summary>更新托盘提示文本（最多 127 个字符）。</summary>
    public void UpdateToolTip(string text)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NIF_TIP;
        data.szTip = Truncate(text, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>弹一个气泡通知。</summary>
    public void ShowBalloon(string title, string message, bool warning = false)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>在鼠标位置弹出菜单。</summary>
    public void ShowMenu()
    {
        MenuRequested?.Invoke(this, EventArgs.Empty);
        if (_menu is null)
        {
            return;
        }

        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private void Add(string toolTip)
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.szTip = Truncate(toolTip, 127);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void Remove()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _messageWindow.Handle,
        uID = 1,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _iconHandle,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON)
        {
            return IntPtr.Zero;
        }

        int notification = lParam.ToInt32();
        switch (notification)
        {
            case WM_LBUTTONDBLCLK:
            case WM_LBUTTONUP:
                ActivateRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>从 exe 资源里取 HICON（不依赖 System.Drawing / WinForms）。</summary>
    private static IntPtr LoadIconHandle()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return IntPtr.Zero;
            }

            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_LARGEICON);
            return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Remove();
        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
}
