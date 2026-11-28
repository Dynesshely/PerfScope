using System.Globalization;

namespace PerfScope.Core.Text;

/// <summary>
/// Core 层的文案出口。Core 不依赖任何 UI 框架，也不能直接读 App 的 resx，
/// 所以这里留一个可注入的翻译器：App 启动时注入 <c>Loc.T</c>，Core 里所有面向用户的字符串
/// 都写成 <see cref="T(string)"/>（key 就是中文原文，与 App 的约定一致）。
/// 未注入时恒等返回中文 —— 单测、CLI、中文界面行为完全不变。
/// </summary>
public static class CoreText
{
    private static Func<string, string> _translator = static chinese => chinese;

    /// <summary>
    /// 注入翻译器（传 <c>null</c> 恢复成恒等）。App 在确定界面语言后调用一次。
    /// </summary>
    public static void SetTranslator(Func<string, string>? translator)
        => _translator = translator ?? (static chinese => chinese);

    /// <summary>翻译一段文案（key 为中文原文）。</summary>
    public static string T(string chinese)
        => string.IsNullOrEmpty(chinese) ? chinese : _translator(chinese);

    /// <summary>翻译带占位符的文案并格式化（占位符用 {0} {1}）。</summary>
    public static string T(string format, params object?[] args)
    {
        string template = T(format);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
