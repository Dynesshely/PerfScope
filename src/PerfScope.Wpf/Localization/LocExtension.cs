using System.Windows.Markup;

namespace PerfScope.App.Localization;

/// <summary>
/// XAML 里的本地化标记扩展：<c>Text="{loc:Loc '暂停监控'}"</c>。
/// 取值在加载时求值一次（语言切换本来就要求重启生效），所以不需要动态刷新。
/// 参数就是中文原文，同时也是 <c>Strings.en.resx</c> 里的 key —— 与代码里的 <see cref="Loc.T"/> 完全同一套约定。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>创建空的标记扩展（供 XAML 以 <c>Text=</c> 形式赋值）。</summary>
    public LocExtension()
    {
    }

    /// <summary>用中文原文作为 key 创建标记扩展。</summary>
    public LocExtension(string text) => Text = text;

    /// <summary>中文原文（同时是资源 key）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
