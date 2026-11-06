using PerfScope.Core.Updates;

namespace PerfScope.Tests.Updates;

/// <summary>版本比较与「是否有新版本」的行为约束。</summary>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.1.3", 0, 1, 3, null)]
    [InlineData("v1.0.0", 1, 0, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("2.0.0+build.7", 2, 0, 0, null)]
    public void 解析(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(pre, version.PreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.0.0")]
    public void 非法输入解析失败(string text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.1.4", "0.1.3", true)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.3", "0.1.3", false)]
    [InlineData("0.1.3", "0.1.4", false)]
    [InlineData("1.0.0-beta", "0.9.0", true)]
    [InlineData("1.0.0", "1.0.0-beta", true)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("v0.2.0", "0.1.3", true)]
    public void 是否有新版本(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsNewer(candidate, current));
    }
}
