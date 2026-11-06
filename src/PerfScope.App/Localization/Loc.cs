using System.Globalization;
using System.Resources;

namespace PerfScope.App.Localization;

/// <summary>
/// 运行时本地化入口。资源 key 直接用中文原文，英文翻译放在 <c>Strings.en.resx</c>：
/// 这样源码里仍是「可读的中文」，漏翻时自动回退中文，不会出现空文案。
/// 语言由设置项决定（system / zh / en），切换后提示重启生效。
/// </summary>
internal static class Loc
{
    /// <summary>支持的语言代码。</summary>
    public const string Chinese = "zh";
    public const string English = "en";
    public const string System = "system";

    private static readonly ResourceManager Resources =
        new("PerfScope.App.Localization.Strings", typeof(Loc).Assembly);

    private static string _language = Chinese;

    /// <summary>语言变化事件（用于刷新已打开窗口的副标题等）。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>当前生效的语言（<see cref="Chinese"/> 或 <see cref="English"/>）。</summary>
    public static string Language => _language;

    /// <summary>当前是否为英文界面。</summary>
    public static bool IsEnglish => string.Equals(_language, English, StringComparison.OrdinalIgnoreCase);

    /// <summary>按设置值解析并应用语言：system 跟随系统 UI 语言。</summary>
    public static void Apply(string? settingValue)
    {
        var resolved = Resolve(settingValue);
        if (string.Equals(resolved, _language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _language = resolved;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>把设置值（system / zh / en）解析成实际语言代码。</summary>
    public static string Resolve(string? settingValue)
    {
        if (string.Equals(settingValue, English, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        if (string.Equals(settingValue, Chinese, StringComparison.OrdinalIgnoreCase))
        {
            return Chinese;
        }

        // system：只有系统 UI 语言是中文时才用中文，其余一律英文
        var ui = CultureInfo.CurrentUICulture;
        return string.Equals(ui.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    /// <summary>
    /// 取本地化文案。<paramref name="chinese"/> 既是中文原文，也是资源 key；
    /// 英文资源缺失时直接返回中文原文。
    /// </summary>
    public static string T(string chinese)
    {
        if (string.IsNullOrEmpty(chinese) || !IsEnglish)
        {
            return chinese;
        }

        try
        {
            return Resources.GetString(chinese, CultureInfo.GetCultureInfo(English)) ?? chinese;
        }
        catch
        {
            return chinese;
        }
    }

    /// <summary>带参数的本地化文案（占位符用 {0} {1}）。</summary>
    public static string T(string chinese, params object?[] args)
    {
        var template = T(chinese);
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
