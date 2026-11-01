using PerfScope.Core.Models;
using PerfScope.Core.Sampling;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Sampling;

/// <summary><see cref="SnapshotComposer"/> 的差分、聚合、平滑与边界行为测试。</summary>
public sealed class SnapshotComposerTests
{
    private static SnapshotComposer CreateComposer(
        out FakeCapture capture,
        out TestClock clock,
        int smoothingWindow = 1,
        CpuNormalization cpuMode = CpuNormalization.MachinePercent,
        Func<ProcessRawSample, AppIdentity>? classify = null)
    {
        var settings = new AppSettings
        {
            SmoothingWindow = smoothingWindow,
            CpuMode = cpuMode,
            AdaptiveThrottleWhenHidden = false,
        };

        var classifier = classify is null ? new StubClassifier() : new StubClassifier(classify);
        capture = new FakeCapture();
        clock = new TestClock(1000);
        TestClock localClock = clock;

        return new SnapshotComposer(capture, classifier, settings)
        {
            TimestampFrequency = localClock.Frequency,
            TimestampProvider = () => localClock.Now,
        };
    }

    [Fact]
    public void 首帧返回null并建立基线()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        SystemRawSample first = Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe", cpuTicks: 0)]);
        Assert.Null(composer.Compose(first));

        clock.Advance(1000);
        SystemRawSample second = Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 0)]);
        SnapshotFrame? frame = composer.Compose(second);

        Assert.NotNull(frame);
        Assert.Equal(TimeSpan.FromSeconds(1), frame.Interval);
    }

    [Fact]
    public void 差分得出整机口径CPU百分比()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        // 4 个逻辑核，1 秒内消耗 0.4 秒 CPU（4,000,000 个 100ns）→ 0.4 / (1 × 4) × 100 = 10%
        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe", cpuTicks: 0)]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 4_000_000)]));

        Assert.NotNull(frame);
        AppSample app = Assert.Single(frame.Apps);
        Assert.Equal(10d, app.CpuPercent, 6);
        Assert.Equal(10d, Assert.Single(app.Processes).CpuPercent, 6);
    }

    [Fact]
    public void 差分得出单核口径CPU百分比()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, cpuMode: CpuNormalization.PerCorePercent);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe", cpuTicks: 0)]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 4_000_000)]));

        Assert.NotNull(frame);
        // 0.4 秒 CPU / 1 秒 × 100 = 40%（单核口径不除核心数）
        Assert.Equal(40d, Assert.Single(frame.Apps).CpuPercent, 6);
    }

    [Fact]
    public void PID复用视为新进程()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(20, "a.exe", cpuTicks: 0, createTicks: 111)]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(20, "a.exe", cpuTicks: 50_000_000, createTicks: 222, workingSet: 7_000_000)]));

        Assert.NotNull(frame);
        AppSample app = Assert.Single(frame.Apps);
        // 创建时间不同 → 视为新进程，本帧 CPU 记 0，但内存取本帧的真实值。
        Assert.Equal(0d, app.CpuPercent, 6);
        Assert.Equal(7_000_000, app.WorkingSetBytes);
        Assert.Equal(1, app.ProcessCount);
    }

    [Fact]
    public void 新进程CPU记0但保留内存与进程数()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [
                Samples.Process(10, "a.exe", cpuTicks: 3_000_000, workingSet: 2_000_000),
                Samples.Process(11, "b.exe", cpuTicks: 9_000_000, workingSet: 5_000_000),
            ]));

        Assert.NotNull(frame);
        Assert.Equal(2, frame.ProcessCount);
        AppSample? newApp = frame.Apps.FirstOrDefault(static app => app.Key == "exe:b.exe");
        Assert.NotNull(newApp);
        Assert.Equal(0d, newApp.CpuPercent, 6);
        Assert.Equal(5_000_000, newApp.WorkingSetBytes);
    }

    [Fact]
    public void 时间源未前进时跳过该帧且不更新基线()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe", cpuTicks: 0)]));

        // 时间未前进：必须跳过，不得返回错误的百分比。
        Assert.Null(composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 1_000_000)])));

        // 基线保持在第 1 帧，因此这一段跨越 2 秒；8,000,000 tick 在 2 秒 / 4 核上仍是 10%。
        clock.Advance(2000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(Samples.Timestamp(2), [Samples.Process(10, "a.exe", cpuTicks: 8_000_000)]));

        Assert.NotNull(frame);
        Assert.Equal(2d, frame.Interval.TotalSeconds, 6);
        Assert.Equal(10d, Assert.Single(frame.Apps).CpuPercent, 6);
    }

    [Fact]
    public void 被排除的进程不进入列表与进程总数()
    {
        SnapshotComposer composer = CreateComposer(
            out _,
            out TestClock clock,
            classify: static process => new AppIdentity(
                "exe:" + process.ImageName,
                process.ImageName,
                null,
                AppKind.ExeName,
                "测试归并",
                Excluded: process.Pid == 99));

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe"), Samples.Process(99, "secret.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "a.exe", cpuTicks: 1_000_000), Samples.Process(99, "secret.exe", cpuTicks: 8_000_000)]));

        Assert.NotNull(frame);
        Assert.Equal(1, frame.ProcessCount);
        AppSample app = Assert.Single(frame.Apps);
        Assert.Equal("a.exe", app.DisplayName);
        Assert.DoesNotContain(frame.Apps, static item => item.Key.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void 平滑窗口为3时取最近三帧平均()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, smoothingWindow: 3);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")], logicalProcessors: 1));

        clock.Advance(1000);
        SnapshotFrame? second = composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)], logicalProcessors: 1));
        Assert.Equal(30d, Assert.Single(second!.Apps).CpuPercent, 6);

        clock.Advance(1000);
        SnapshotFrame? third = composer.Compose(Samples.Frame(Samples.Timestamp(2), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)], logicalProcessors: 1));
        // 窗口内只有 2 个样本：(30 + 0) / 2 = 15
        Assert.Equal(15d, Assert.Single(third!.Apps).CpuPercent, 6);

        clock.Advance(1000);
        SnapshotFrame? fourth = composer.Compose(Samples.Frame(Samples.Timestamp(3), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)], logicalProcessors: 1));
        // 窗口满：(30 + 0 + 0) / 3 = 10
        Assert.Equal(10d, Assert.Single(fourth!.Apps).CpuPercent, 6);
    }

    [Fact]
    public void 平滑窗口为1时不平滑()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, smoothingWindow: 1);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")], logicalProcessors: 1));
        clock.Advance(1000);
        composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)], logicalProcessors: 1));

        clock.Advance(1000);
        SnapshotFrame? third = composer.Compose(Samples.Frame(Samples.Timestamp(2), [Samples.Process(10, "a.exe", cpuTicks: 3_000_000)], logicalProcessors: 1));
        Assert.Equal(0d, Assert.Single(third!.Apps).CpuPercent, 6);
    }

    [Fact]
    public void 应用消失后丢弃它的平滑缓冲()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, smoothingWindow: 3);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")], logicalProcessors: 1));

        clock.Advance(1000);
        composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 9_000_000)], logicalProcessors: 1));
        clock.Advance(1000);
        composer.Compose(Samples.Frame(Samples.Timestamp(2), [Samples.Process(10, "a.exe", cpuTicks: 18_000_000)], logicalProcessors: 1));

        // 应用整帧消失：它的环形缓冲必须被清掉。
        clock.Advance(1000);
        SnapshotFrame? absent = composer.Compose(Samples.Frame(Samples.Timestamp(3), [Samples.Process(77, "other.exe")], logicalProcessors: 1));
        Assert.DoesNotContain(absent!.Apps, static app => app.Key == "exe:a.exe");

        clock.Advance(1000);
        SnapshotFrame? reappeared = composer.Compose(Samples.Frame(
            Samples.Timestamp(4),
            [Samples.Process(10, "a.exe", cpuTicks: 21_000_000), Samples.Process(77, "other.exe")],
            logicalProcessors: 1));

        AppSample? app = reappeared!.Apps.FirstOrDefault(static item => item.Key == "exe:a.exe");
        Assert.NotNull(app);

        // 消失一帧后重新出现的 PID 按「新进程」处理，本帧 CPU 记 0。
        // 若旧平滑缓冲残留，这里会得到 (90 + 90 + 0) / 3 = 60；断言 0 才说明缓冲确实被丢弃。
        Assert.Equal(0d, app.CpuPercent, 6);
    }

    [Fact]
    public void IO速率按区间秒数换算()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(
            Samples.Timestamp(0),
            [Samples.Process(10, "a.exe", readBytes: 1_000, writeBytes: 2_000)],
            logicalProcessors: 1));

        clock.Advance(2000); // 2 秒
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "a.exe", readBytes: 1_000 + 4_096, writeBytes: 2_000 + 1_024)],
            logicalProcessors: 1));

        AppSample app = Assert.Single(frame!.Apps);
        Assert.Equal(2_048d, app.ReadBytesPerSecond, 6);
        Assert.Equal(512d, app.WriteBytesPerSecond, 6);
    }

    [Fact]
    public void 自身开销按整机口径记录()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);
        int selfPid = Environment.ProcessId;

        composer.Compose(Samples.Frame(
            Samples.Timestamp(0),
            [Samples.Process(selfPid, "perfscope.exe", cpuTicks: 0, workingSet: 30_000_000)],
            logicalProcessors: 4));

        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(selfPid, "perfscope.exe", cpuTicks: 2_000_000, workingSet: 31_000_000)],
            logicalProcessors: 4));

        Assert.NotNull(frame);
        // 0.2 秒 CPU / 1 秒 / 4 核 × 100 = 5%
        Assert.Equal(5d, frame.SelfCpuPercent, 6);
        Assert.Equal(31_000_000, frame.SelfWorkingSetBytes);
    }

    [Fact]
    public void 系统CPU与内存百分比正确()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")], idleTicks: 0, kernelTicks: 0, userTicks: 0));
        clock.Advance(1000);

        // 内核时间含空闲：总量 = 1000 + 500 = 1500，忙碌 = 1500 - 500 = 1000 → 66.67%
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "a.exe")],
            idleTicks: 500,
            kernelTicks: 1_000,
            userTicks: 500,
            totalPhysicalBytes: 1_000,
            availablePhysicalBytes: 250));

        Assert.NotNull(frame);
        Assert.Equal(66.666_666d, frame.SystemCpuPercent, 3);
        Assert.Equal(75d, frame.SystemMemoryPercent, 3);
        Assert.Equal(1_000, frame.TotalPhysicalBytes);
        Assert.Equal(250, frame.AvailablePhysicalBytes);
    }

    [Fact]
    public void 应用与成员都按CPU降序排列()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "low.exe"), Samples.Process(11, "high.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "low.exe", cpuTicks: 1_000_000), Samples.Process(11, "high.exe", cpuTicks: 9_000_000)]));

        Assert.NotNull(frame);
        Assert.Equal("high.exe", frame.Apps[0].DisplayName);
        Assert.Equal("low.exe", frame.Apps[1].DisplayName);
    }

    [Fact]
    public void 同一应用的多进程会被聚合()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "chrome.exe"), Samples.Process(11, "chrome.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "chrome.exe", cpuTicks: 2_000_000, workingSet: 1_000), Samples.Process(11, "chrome.exe", cpuTicks: 2_000_000, workingSet: 2_000)]));

        AppSample app = Assert.Single(frame!.Apps);
        Assert.Equal(2, app.ProcessCount);
        Assert.Equal(3_000, app.WorkingSetBytes);
        Assert.Equal(10d, app.CpuPercent, 6); // 每个 5%，合计 10%
        Assert.Equal(2, app.Processes.Length);
    }

    [Fact]
    public void 空转进程不参与应用统计()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        // PID 0（Idle）的 CPU 时间就是系统空闲时间，绝不能被聚合成一个应用。
        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(0, "Idle"), Samples.Process(10, "a.exe")], logicalProcessors: 1));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(0, "Idle", cpuTicks: 9_000_000), Samples.Process(10, "a.exe", cpuTicks: 1_000_000)],
            logicalProcessors: 1));

        Assert.NotNull(frame);
        Assert.Equal(1, frame.ProcessCount);
        AppSample app = Assert.Single(frame.Apps);
        Assert.Equal("a.exe", app.DisplayName);
        Assert.Equal(10d, app.CpuPercent, 6);
    }

    [Fact]
    public void 同名不同Key的应用会被改成唯一显示名()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, classify: static process => process.Pid == 10
            ? new AppIdentity("dir:C:\\Program Files\\Tencent\\Weixin", "Weixin", @"C:\Program Files\Tencent\Weixin", AppKind.InstallRoot, "安装目录归并")
            : new AppIdentity("dir:D:\\Mainland_Apps\\Weixin", "Weixin", @"D:\Mainland_Apps\Weixin", AppKind.InstallRoot, "安装目录归并"));

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "Weixin.exe"), Samples.Process(11, "Weixin.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "Weixin.exe", cpuTicks: 1_000_000), Samples.Process(11, "Weixin.exe", cpuTicks: 2_000_000)]));

        Assert.NotNull(frame);
        Assert.Equal(2, frame.Apps.Length);

        // 只改显示名，Key 保持不变（历史与趋势依赖 Key）。
        Assert.Equal(
            ["dir:C:\\Program Files\\Tencent\\Weixin", "dir:D:\\Mainland_Apps\\Weixin"],
            frame.Apps.Select(static app => app.Key).OrderBy(static key => key, StringComparer.Ordinal));

        Assert.Equal(
            ["Weixin（Mainland_Apps）", "Weixin（Tencent）"],
            frame.Apps.Select(static app => app.DisplayName).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void 同名改名结果跨帧稳定()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, classify: static process => process.Pid == 10
            ? new AppIdentity("dir:C:\\Program Files\\Tencent\\Weixin", "Weixin", @"C:\Program Files\Tencent\Weixin", AppKind.InstallRoot, "安装目录归并")
            : new AppIdentity("dir:D:\\Mainland_Apps\\Weixin", "Weixin", @"D:\Mainland_Apps\Weixin", AppKind.InstallRoot, "安装目录归并"));

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "Weixin.exe"), Samples.Process(11, "Weixin.exe")]));

        clock.Advance(1000);
        SnapshotFrame? first = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "Weixin.exe", cpuTicks: 9_000_000), Samples.Process(11, "Weixin.exe", cpuTicks: 1_000_000)]));

        // 第二帧把两者的 CPU 名次颠倒，并按相反顺序喂进程，改名结果必须完全一致。
        clock.Advance(1000);
        SnapshotFrame? second = composer.Compose(Samples.Frame(
            Samples.Timestamp(2),
            [Samples.Process(11, "Weixin.exe", cpuTicks: 9_000_000), Samples.Process(10, "Weixin.exe", cpuTicks: 10_000_000)]));

        Assert.NotNull(first);
        Assert.NotNull(second);

        Dictionary<string, string> firstNames = first.Apps.ToDictionary(static app => app.Key, static app => app.DisplayName, StringComparer.Ordinal);
        Dictionary<string, string> secondNames = second.Apps.ToDictionary(static app => app.Key, static app => app.DisplayName, StringComparer.Ordinal);

        Assert.Equal(firstNames.Count, secondNames.Count);
        foreach (KeyValuePair<string, string> pair in firstNames)
        {
            Assert.True(secondNames.TryGetValue(pair.Key, out string? name), "第二帧丢失了应用：" + pair.Key);
            Assert.Equal(pair.Value, name);
        }

        // CPU 降序没有被改名逻辑打乱：第二帧 pid 11 的增量（8,000,000 tick）大于 pid 10（1,000,000 tick）。
        Assert.Equal(11, second.Apps[0].Processes[0].Pid);
    }

    [Fact]
    public void 没有同名应用时保持原显示名()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "chrome.exe"), Samples.Process(11, "code.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "chrome.exe", cpuTicks: 2_000_000), Samples.Process(11, "code.exe", cpuTicks: 1_000_000)]));

        Assert.Equal(["chrome.exe", "code.exe"], frame!.Apps.Select(static app => app.DisplayName));
    }

    [Fact]
    public void 同名且没有RootPath时用图标文件名区分()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, classify: static process => process.Pid == 10
            ? new AppIdentity("exe:weixin.exe", "Weixin", null, AppKind.ExeName, "按映像名")
            : new AppIdentity("exe:qq.exe", "Weixin", null, AppKind.ExeName, "按映像名"));

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "weixin.exe"), Samples.Process(11, "qq.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [
                Samples.Process(10, "weixin.exe", cpuTicks: 1_000_000, executablePath: @"C:\A\weixin.exe"),
                Samples.Process(11, "qq.exe", cpuTicks: 2_000_000, executablePath: @"D:\B\qq.exe"),
            ]));

        Assert.Equal(
            ["Weixin（qq.exe）", "Weixin（weixin.exe）"],
            frame!.Apps.Select(static app => app.DisplayName).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void 同名且尾部路径相同时退化为盘符加首段()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, classify: static process => process.Pid == 10
            ? new AppIdentity("dir:C:\\Program Files\\App", "App", @"C:\Program Files\App", AppKind.InstallRoot, "安装目录归并")
            : new AppIdentity("dir:D:\\Program Files\\App", "App", @"D:\Program Files\App", AppKind.InstallRoot, "安装目录归并"));

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "App.exe"), Samples.Process(11, "App.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(
            Samples.Timestamp(1),
            [Samples.Process(10, "App.exe", cpuTicks: 1_000_000), Samples.Process(11, "App.exe", cpuTicks: 2_000_000)]));

        Assert.Equal(
            [@"App（C:\Program Files）", @"App（D:\Program Files）"],
            frame!.Apps.Select(static app => app.DisplayName).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void 设置热更新会同步给归并器()
    {
        var settings = new AppSettings { SmoothingWindow = 2 };
        var classifier = new StubClassifier();
        var capture = new FakeCapture();
        var composer = new SnapshotComposer(capture, classifier, settings);

        Assert.Equal(0, classifier.SettingsChangeCount);

        var updated = new AppSettings { SmoothingWindow = 4, GroupByProductName = false };
        composer.OnSettingsChanged(updated);

        // 归并规则也在设置里，必须转发给归并器，否则设置页改规则不生效。
        Assert.Equal(1, classifier.SettingsChangeCount);
        Assert.Same(updated, classifier.LastSettings);
    }

    [Fact]
    public void Reset后重新建立基线()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")]));
        composer.Reset();
        clock.Advance(1000);

        Assert.Null(composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 1_000_000)])));
    }

    [Fact]
    public void 无参Compose会驱动采集器()
    {
        SnapshotComposer composer = CreateComposer(out FakeCapture capture, out TestClock clock);

        capture.Enqueue(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")]));
        Assert.Null(composer.Compose());

        clock.Advance(1000);
        capture.Enqueue(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 2_000_000)]));
        SnapshotFrame? frame = composer.Compose();

        Assert.NotNull(frame);
        Assert.Equal(2, capture.CaptureCount);
        Assert.Equal(5d, Assert.Single(frame.Apps).CpuPercent, 6);
    }

    [Fact]
    public void 设置热更新改变口径与平滑窗口()
    {
        SnapshotComposer composer = CreateComposer(out _, out TestClock clock, smoothingWindow: 1);
        var settings = new AppSettings { SmoothingWindow = 3, CpuMode = CpuNormalization.PerCorePercent };

        composer.OnSettingsChanged(settings);

        composer.Compose(Samples.Frame(Samples.Timestamp(0), [Samples.Process(10, "a.exe")]));
        clock.Advance(1000);
        SnapshotFrame? frame = composer.Compose(Samples.Frame(Samples.Timestamp(1), [Samples.Process(10, "a.exe", cpuTicks: 4_000_000)]));

        // 单核口径：40%，窗口 3 但只有 1 个样本 → 40%
        Assert.Equal(40d, Assert.Single(frame!.Apps).CpuPercent, 6);
    }
}
