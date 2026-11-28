using PerfScope.Core.Text;

namespace PerfScope.Tests.Text;

/// <summary>Core 文案出口的行为约束：默认恒等（中文）、注入后翻译、注入 null 恢复、格式化兜底。</summary>
public sealed class CoreTextTests : IDisposable
{
    /// <summary>每个用例结束后恢复默认翻译器，避免静态状态污染其它测试。</summary>
    public void Dispose() => CoreText.SetTranslator(null);

    [Fact]
    public void 默认不翻译()
    {
        Assert.Equal("整机 CPU 平均", CoreText.T("整机 CPU 平均"));
    }

    [Fact]
    public void 注入翻译器后返回译文()
    {
        CoreText.SetTranslator(chinese => chinese == "内存压力" ? "Memory pressure" : chinese);

        Assert.Equal("Memory pressure", CoreText.T("内存压力"));
        Assert.Equal("整机", CoreText.T("整机"));
    }

    [Fact]
    public void 注入null恢复恒等()
    {
        CoreText.SetTranslator(chinese => "x");
        CoreText.SetTranslator(null);

        Assert.Equal("内存增长", CoreText.T("内存增长"));
    }

    [Fact]
    public void 空字符串原样返回()
    {
        CoreText.SetTranslator(chinese => "x");

        Assert.Equal(string.Empty, CoreText.T(string.Empty));
    }

    [Fact]
    public void 带参数格式化()
    {
        CoreText.SetTranslator(chinese => chinese.Replace("共 {0} 项", "total {0} items", StringComparison.Ordinal));

        Assert.Equal("total 3 items", CoreText.T("共 {0} 项", 3));
    }

    [Fact]
    public void 占位符不匹配时不抛异常()
    {
        CoreText.SetTranslator(chinese => "no placeholders");

        Assert.Equal("no placeholders", CoreText.T("共 {0} 项", 3));
    }
}
