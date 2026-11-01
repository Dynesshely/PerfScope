using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 应用图标缓存。按 exe 路径提取系统图标并缩放到指定尺寸；拿不到图标时用首字母色块兜底。
/// 缓存有上限，超过后整体释放重建，避免长期运行句柄膨胀。
/// </summary>
internal sealed class IconProvider : IDisposable
{
    private const int MaxCachedIcons = 512;

    private readonly Dictionary<string, Bitmap?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _fallbackCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>取应用图标；<paramref name="size"/> 为像素边长。</summary>
    public Image? GetIcon(string? executablePath, string fallbackName, int size)
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

            Bitmap? loaded = File.Exists(path) ? LoadFromExecutable(path, size) : null;
            if (_iconCache.Count >= MaxCachedIcons)
            {
                ClearIconCache();
            }

            _iconCache[path] = loaded;
            if (loaded is not null)
            {
                return loaded;
            }
        }

        return GetFallbackIcon(fallbackName, size);
    }

    /// <summary>清空所有图标缓存并释放位图。</summary>
    public void ClearAll()
    {
        ClearIconCache();
        foreach (var bitmap in _fallbackCache.Values)
        {
            bitmap.Dispose();
        }

        _fallbackCache.Clear();
    }

    private void ClearIconCache()
    {
        foreach (var bitmap in _iconCache.Values)
        {
            bitmap?.Dispose();
        }

        _iconCache.Clear();
    }

    private Bitmap GetFallbackIcon(string name, int size)
    {
        string key = (string.IsNullOrWhiteSpace(name) ? "?" : name) + "#" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_fallbackCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = DrawFallback(name, size);
        _fallbackCache[key] = bitmap;
        return bitmap;
    }

    private static Bitmap? LoadFromExecutable(string path, int size)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var source = icon.ToBitmap();
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap DrawFallback(string name, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color[] palette =
            {
                Color.FromArgb(76, 154, 255),
                Color.FromArgb(167, 139, 250),
                Color.FromArgb(52, 199, 159),
                Color.FromArgb(255, 158, 90),
                Color.FromArgb(255, 107, 129),
                Color.FromArgb(120, 190, 90),
            };

            int hash = 17;
            foreach (char ch in name)
            {
                hash = unchecked(hash * 31 + ch);
            }

            var color = palette[Math.Abs(hash) % palette.Length];
            var bounds = new Rectangle(0, 0, Math.Max(1, size - 1), Math.Max(1, size - 1));
            GdiUtil.FillRoundedRect(graphics, bounds, Math.Max(2, size / 4), color);

            string letter = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
            using var font = new Font(Theme.FontFamilyName, Math.Max(6f, size * 0.52f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
            };
            graphics.DrawString(letter, font, textBrush, new RectangleF(0, 0, size, size), format);
        }

        return bitmap;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearAll();
    }
}
