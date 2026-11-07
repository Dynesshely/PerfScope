using PerfScope.Core.Diagnostics;

namespace PerfScope.Tests.Diagnostics;

/// <summary>诊断包脱敏：不能把用户名 / 机器名 / 用户目录带出去。</summary>
public sealed class DiagnosticBundleTests
{
    [Fact]
    public void 用户名与机器名被替换()
    {
        var redacted = DiagnosticBundle.Redact(
            @"C:\Users\alice\AppData\Local\PerfScope user=alice machine ALICE-PC",
            userName: "alice",
            machineName: "ALICE-PC");

        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALICE-PC", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERNAME%", redacted, StringComparison.Ordinal);
        Assert.Contains("%COMPUTERNAME%", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void 用户目录路径被整体替换()
    {
        var redacted = DiagnosticBundle.Redact(@"C:\Users\bob\AppData\Roaming\PerfScope\settings.json", "someone-else", "PC-1");

        Assert.DoesNotContain(@"\bob\", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void 非用户目录路径保持原样()
    {
        var redacted = DiagnosticBundle.Redact(@"C:\Program Files\PerfScope\PerfScope.exe", "bob", "PC-1");

        Assert.Contains(@"C:\Program Files\PerfScope", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void 空输入返回空字符串()
    {
        Assert.Equal(string.Empty, DiagnosticBundle.Redact(null));
    }
}
