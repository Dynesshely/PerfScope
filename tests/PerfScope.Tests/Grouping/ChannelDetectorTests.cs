using PerfScope.Core.Grouping;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Grouping;

/// <summary>同一产品的不同发行通道（Edge / Edge Beta）必须归并成两个应用。</summary>
public sealed class ChannelDetectorTests
{
    private static AppSettings DefaultSettings() => new();

    private static ProcessRawSample Proc(int pid, int parentPid, string imageName, string? path)
        => new(pid, parentPid, imageName, path, 0, 0, 0, 0, 0, 0, 0, 0);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", null)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge Beta\Application\msedge.exe", "Beta")]
    [InlineData(@"C:\Program Files\Microsoft\Edge Dev\Application\msedge.exe", "Dev")]
    [InlineData(@"C:\Program Files\Google\Chrome Beta\Application\chrome.exe", "Beta")]
    [InlineData(@"C:\Program Files\Google\Chrome SxS\Application\chrome.exe", "Canary")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", null)]
    public void 通道识别(string path, string? expected)
    {
        Assert.Equal(expected, ChannelDetector.Detect(path));
    }

    [Fact]
    public void Edge_稳定版与Beta版归为两个应用()
    {
        var probe = new FakeFileSystemProbe();
        const string stableDir = @"C:\Program Files (x86)\Microsoft\Edge\Application";
        const string betaDir = @"C:\Program Files (x86)\Microsoft\Edge Beta\Application";
        probe.AddExe(stableDir + @"\msedge.exe", "Microsoft Edge", "Microsoft Corporation");
        probe.AddExe(betaDir + @"\msedge.exe", "Microsoft Edge", "Microsoft Corporation");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var stable = classifier.Classify(Proc(100, 1, "msedge.exe", stableDir + @"\msedge.exe"), null);
        var beta = classifier.Classify(Proc(200, 1, "msedge.exe", betaDir + @"\msedge.exe"), null);

        Assert.NotEqual(stable.Key, beta.Key);
        Assert.Equal("Microsoft Edge", stable.DisplayName);
        Assert.Equal("Microsoft Edge Beta", beta.DisplayName);
        Assert.Contains("Beta", beta.Reason, StringComparison.Ordinal);
    }
}
