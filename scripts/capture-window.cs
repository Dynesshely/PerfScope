#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// 对指定进程的主窗口做一次 PrintWindow 截图（用于文档与验收留证）。
// 用法：dotnet run scripts/capture-window.cs -- <pid> <输出.png>
if (args.Length < 2)
{
    Console.WriteLine("用法：dotnet run scripts/capture-window.cs -- <pid> <输出.png>");
    return 1;
}

// 关键：截图进程必须是 Per-Monitor-V2 DPI 感知，否则 GetWindowRect 返回的是被 DPI 虚拟化后的坐标，
// 在 150% 缩放下只能截到窗口左上角的一部分（表现为右侧列"消失"）。
NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));

var pid = int.Parse(args[0]);
var output = Path.GetFullPath(args[1]);
if (Path.GetDirectoryName(output) is { } dir)
{
    Directory.CreateDirectory(dir);
}

using var process = System.Diagnostics.Process.GetProcessById(pid);
var handle = IntPtr.Zero;
for (var i = 0; i < 40 && handle == IntPtr.Zero; i++)
{
    process.Refresh();
    handle = process.MainWindowHandle;
    if (handle == IntPtr.Zero)
    {
        Thread.Sleep(250);
    }
}

if (handle == IntPtr.Zero)
{
    Console.WriteLine("窗口句柄为 0（窗口可能未创建或已隐藏）");
    return 2;
}

// 可选：先把窗口调大，保证六列都能完整显示（参数：宽 高）
if (args.Length >= 4 && int.TryParse(args[2], out var targetWidth) && int.TryParse(args[3], out var targetHeight))
{
    NativeMethods.MoveWindow(handle, 8, 8, targetWidth, targetHeight, true);
    Thread.Sleep(1800);
}

NativeMethods.GetWindowRect(handle, out var rect);
var width = rect.Right - rect.Left;
var height = rect.Bottom - rect.Top;
if (width <= 0 || height <= 0)
{
    Console.WriteLine($"窗口尺寸非法：{width}x{height}");
    return 3;
}

var useScreenCopy = args.Any(a => string.Equals(a, "screen", StringComparison.OrdinalIgnoreCase))
                    || args.Any(a => string.Equals(a, "topmost", StringComparison.OrdinalIgnoreCase));

if (args.Any(a => string.Equals(a, "topmost", StringComparison.OrdinalIgnoreCase)))
{
    // 置顶 + 激活，确保屏幕拷贝拿到的是本窗口（而不是被别的窗口遮住的位置）
    NativeMethods.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
    NativeMethods.ShowWindow(handle, 5);
    NativeMethods.SetForegroundWindow(handle);
    Thread.Sleep(1200);
    NativeMethods.GetWindowRect(handle, out rect);
    width = rect.Right - rect.Left;
    height = rect.Bottom - rect.Top;
}
using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(bitmap))
{
    if (useScreenCopy)
    {
        // 直接从屏幕抓像素：所见即所得，能拿到自绘控件的最终画面
        NativeMethods.SetForegroundWindow(handle);
        Thread.Sleep(900);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
    }
    else
    {
        var hdc = graphics.GetHdc();
        try
        {
            // PW_RENDERFULLCONTENT(2)：能抓到 DirectComposition / 双缓冲内容
            NativeMethods.PrintWindow(handle, hdc, 2);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }
}

bitmap.Save(output, ImageFormat.Png);
Console.WriteLine($"已保存 {output}（{width}x{height}）");
return 0;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
}