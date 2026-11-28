using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfScope.App.Services;

/// <summary>
/// 应用图标服务：按可执行文件路径取系统图标（SHGetFileInfo → HICON → BitmapSource），
/// 拿不到时用首字母色块兜底。
/// <para>
/// WPF 图片使用的几条硬性纪律（都是内存泄漏/句柄泄漏的高发点）：
/// 1. 任何交给界面的 <see cref="ImageSource"/> 都必须 <c>Freeze()</c>，否则会与创建线程绑定；
/// 2. 不得持有 <c>BitmapImage</c> + 文件流：本服务只从 HICON 转换，HICON 用完立刻 <c>DestroyIcon</c>；
/// 3. <c>ImageSource</c> 不实现 IDisposable，所谓「释放」= 清掉强引用让 GC 回收像素缓冲，
///    因此缓存必须有上限，不能按应用名无限增长；
/// 4. 不要在每帧渲染路径上创建图片对象（本类只在新应用出现时命中一次）。
/// </para>
/// </summary>
public sealed class IconService : IDisposable
{
    private const int MaxCached = 512;
    private const int MaxFallbackCached = 256;
    private const int IconSize = 20;

    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _fallbackCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>取图标；<paramref name="executablePath"/> 为空或取不到时返回首字母色块。</summary>
    public ImageSource? GetIcon(string? executablePath, string displayName)
    {
        if (_disposed)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string path = executablePath!;
            if (_iconCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var loaded = File.Exists(path) ? LoadFromShell(path) : null;
            if (loaded is not null)
            {
                if (_iconCache.Count >= MaxCached)
                {
                    _iconCache.Clear();
                }

                _iconCache[path] = loaded;
                return loaded;
            }
        }

        return GetFallback(displayName);
    }

    /// <summary>清空缓存（切换主题后首字母色块需要按新配色重画）。</summary>
    public void ClearFallbacks()
    {
        _fallbackCache.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _iconCache.Clear();
        _fallbackCache.Clear();
    }

    private static ImageSource? LoadFromShell(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            hIcon = info.hIcon;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(IconSize, IconSize));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }
    }

    private ImageSource GetFallback(string displayName)
    {
        string key = string.IsNullOrWhiteSpace(displayName) ? "?" : displayName;
        if (_fallbackCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 色块是按名字缓存的：上限一到就整体丢弃（像素缓冲由 GC 回收），避免长时间运行后无限增长
        if (_fallbackCache.Count >= MaxFallbackCached)
        {
            _fallbackCache.Clear();
        }

        var image = DrawFallback(key);
        _fallbackCache[key] = image;
        return image;
    }

    private static ImageSource DrawFallback(string name)
    {
        string letter = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
        Color[] palette =
        {
            Color.FromRgb(0x4C, 0x9A, 0xFF),
            Color.FromRgb(0xA7, 0x8B, 0xFA),
            Color.FromRgb(0x34, 0xC7, 0x9F),
            Color.FromRgb(0xFF, 0x9E, 0x5A),
            Color.FromRgb(0xFF, 0x6B, 0x81),
            Color.FromRgb(0x78, 0xBE, 0x5A),
        };

        int hash = 17;
        foreach (char ch in name)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var color = palette[Math.Abs(hash) % palette.Length];
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(color), null,
                new Rect(0, 0, IconSize, IconSize), 5, 5);
            var text = new FormattedText(letter, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                IconSize * 0.56, Brushes.White, 1.0);
            context.DrawText(text, new Point((IconSize - text.Width) / 2, (IconSize - text.Height) / 2 - 1));
        }

        var bitmap = new RenderTargetBitmap(IconSize * 2, IconSize * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

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
}
