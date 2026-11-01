using System.Drawing.Text;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 系统自带的图标字体：Windows 11 的 Segoe Fluent Icons，回退到 Windows 10 的 Segoe MDL2 Assets。
/// 用它代替手绘折线，字形在任意 DPI 下都更干净；不引入任何第三方字体文件，也不增加部署依赖。
/// </summary>
internal static class IconFont
{
    /// <summary>折叠（右向）箭头。</summary>
    public const string ChevronRight = "\uE76C";

    /// <summary>展开（下向）箭头。</summary>
    public const string ChevronDown = "\uE70D";

    private static readonly Lazy<string> LazyFamily = new(ResolveFamily);

    /// <summary>解析出的图标字体族名。</summary>
    public static string Family => LazyFamily.Value;

    /// <summary>按字号创建图标字体（调用方负责 Dispose）。</summary>
    public static Font Create(float pointSize) => new(Family, pointSize, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>字形在给定字体下是否可用（用于极端环境的兜底判断）。</summary>
    public static bool Supports(string glyph, Font font)
    {
        try
        {
            using var bitmap = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(bitmap);
            var measured = TextRenderer.MeasureText(graphics, glyph, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return measured.Width > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveFamily()
    {
        string[] candidates = { "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol" };
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
                // 候选字体不可用就继续挑下一个
            }
        }

        return "Segoe UI Symbol";
    }
}
