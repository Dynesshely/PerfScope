using System.Drawing.Drawing2D;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 界面配色与字体集合。深色 / 浅色两套，所有画笔与字体都由本类统一持有，
/// 由使用方在退出或切换主题时 <see cref="Dispose"/>，避免 GDI 句柄泄漏。
/// </summary>
internal sealed class Theme : IDisposable
{
    private static readonly Lazy<string> LazyFontFamily = new(ResolveFontFamilyName);

    private readonly List<Font> _ownedFonts = new(8);

    private Theme(bool dark, float scale)
    {
        IsDark = dark;
        if (dark)
        {
            Background = Color.FromArgb(22, 24, 29);
            Surface = Color.FromArgb(30, 33, 40);
            SurfaceAlt = Color.FromArgb(34, 38, 46);
            Border = Color.FromArgb(44, 49, 59);
            HeaderBackground = Color.FromArgb(26, 29, 35);
            TextPrimary = Color.FromArgb(230, 233, 239);
            TextSecondary = Color.FromArgb(167, 176, 191);
            TextMuted = Color.FromArgb(107, 118, 134);
            Accent = Color.FromArgb(76, 154, 255);
            AccentSoft = Color.FromArgb(42, 59, 85);
            CpuLine = Color.FromArgb(76, 154, 255);
            MemoryLine = Color.FromArgb(167, 139, 250);
            Danger = Color.FromArgb(255, 107, 107);
            Warning = Color.FromArgb(255, 180, 84);
            Success = Color.FromArgb(76, 217, 100);
            RowHover = Color.FromArgb(38, 43, 52);
            RowSelected = Color.FromArgb(43, 58, 82);
            GridLine = Color.FromArgb(37, 41, 49);
        }
        else
        {
            Background = Color.FromArgb(245, 246, 248);
            Surface = Color.FromArgb(255, 255, 255);
            SurfaceAlt = Color.FromArgb(241, 244, 248);
            Border = Color.FromArgb(221, 225, 230);
            HeaderBackground = Color.FromArgb(236, 238, 241);
            TextPrimary = Color.FromArgb(27, 31, 36);
            TextSecondary = Color.FromArgb(74, 85, 96);
            TextMuted = Color.FromArgb(138, 146, 156);
            Accent = Color.FromArgb(30, 111, 217);
            AccentSoft = Color.FromArgb(220, 232, 248);
            CpuLine = Color.FromArgb(30, 111, 217);
            MemoryLine = Color.FromArgb(124, 92, 214);
            Danger = Color.FromArgb(214, 69, 69);
            Warning = Color.FromArgb(199, 119, 0);
            Success = Color.FromArgb(35, 150, 70);
            RowHover = Color.FromArgb(238, 242, 247);
            RowSelected = Color.FromArgb(220, 232, 248);
            GridLine = Color.FromArgb(233, 236, 240);
        }

        float s = scale <= 0 ? 1f : Math.Clamp(scale, 0.75f, 3f);
        UiFont = Own(FontFamilyName, 9f * s, FontStyle.Regular);
        UiBoldFont = Own(FontFamilyName, 9f * s, FontStyle.Bold);
        SmallFont = Own(FontFamilyName, 8.25f * s, FontStyle.Regular);
        MetricFont = Own(FontFamilyName, 10f * s, FontStyle.Bold);
        BigNumberFont = Own(FontFamilyName, 19f * s, FontStyle.Bold);
        NumberFont = Own(FontFamilyName, 9f * s, FontStyle.Regular);
    }

    /// <summary>是否为深色主题。</summary>
    public bool IsDark { get; }

    public Color Background { get; }
    public Color Surface { get; }
    public Color SurfaceAlt { get; }
    public Color Border { get; }
    public Color HeaderBackground { get; }
    public Color TextPrimary { get; }
    public Color TextSecondary { get; }
    public Color TextMuted { get; }
    public Color Accent { get; }
    public Color AccentSoft { get; }
    public Color CpuLine { get; }
    public Color MemoryLine { get; }
    public Color Danger { get; }
    public Color Warning { get; }
    public Color Success { get; }
    public Color RowHover { get; }
    public Color RowSelected { get; }
    public Color GridLine { get; }
    public Font UiFont { get; }
    public Font UiBoldFont { get; }
    public Font SmallFont { get; }
    public Font MetricFont { get; }
    public Font BigNumberFont { get; }
    public Font NumberFont { get; }

    /// <summary>界面统一字体族名（找不到候选时自动回落到系统字体）。</summary>
    public static string FontFamilyName => LazyFontFamily.Value;

    /// <summary>按当前系统 DPI 计算缩放比（1.0 = 96 DPI）。</summary>
    public static float DetectDpiScale()
    {
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            return Math.Clamp(graphics.DpiX / 96f, 0.75f, 3f);
        }
        catch
        {
            return 1f;
        }
    }

    /// <summary>按主题名（<c>dark</c> / <c>light</c>）与 DPI 缩放创建主题。</summary>
    public static Theme Create(string? themeName, float scale)
    {
        bool dark = !string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase);
        return new Theme(dark, scale);
    }

    /// <summary>把颜色换成带透明度的版本（用于叠加高亮）。</summary>
    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

    private Font Own(string family, float size, FontStyle style)
    {
        var font = new Font(family, size, style);
        _ownedFonts.Add(font);
        return font;
    }

    private static string ResolveFontFamilyName()
    {
        string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
        foreach (var candidate in candidates)
        {
            try
            {
                using var probe = new Font(candidate, 9f);
                if (string.Equals(probe.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch
            {
                // 探测字体失败就继续尝试下一个候选
            }
        }

        try
        {
            return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Microsoft Sans Serif";
        }
        catch
        {
            return "Microsoft Sans Serif";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var font in _ownedFonts)
        {
            font.Dispose();
        }

        _ownedFonts.Clear();
    }
}
