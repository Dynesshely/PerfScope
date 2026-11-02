using PerfScope.Core.Grouping;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Grouping;

/// <summary>系统功能模块归并（WSL / 虚拟机 / 图形 / 安全 / 内核 …）的行为约束。</summary>
public sealed class SystemModuleTests
{
    private static ProcessRawSample Proc(int pid, string imageName, string? path)
        => new(pid, 4, imageName, path, 0, 0, 0, 0, 0, 0, 0, 0);

    private static AppClassifier CreateClassifier(AppSettings? settings = null)
        => new(settings ?? new AppSettings(), new FakeFileSystemProbe());

    [Fact]
    public void WSL_相关进程归入WSL子系统()
    {
        var classifier = CreateClassifier();

        var vmmem = classifier.Classify(Proc(700, "vmmemWSL", null), null);
        var wslhost = classifier.Classify(Proc(701, "wslhost.exe", @"C:\Windows\System32\wslhost.exe"), null);

        Assert.Equal(AppKind.SystemModule, vmmem.Kind);
        Assert.Equal("sysmod:wsl", vmmem.Key);
        Assert.Equal("WSL 子系统", vmmem.DisplayName);
        Assert.Equal(vmmem.Key, wslhost.Key);
    }

    [Fact]
    public void 内核伪进程归入内核与系统核心()
    {
        var classifier = CreateClassifier();

        var system = classifier.Classify(Proc(4, "System", null), null);
        var compression = classifier.Classify(Proc(900, "Memory Compression", null), null);
        var registry = classifier.Classify(Proc(901, "Registry", null), null);

        Assert.Equal("sysmod:kernel", system.Key);
        Assert.Equal("sysmod:kernel", compression.Key);
        Assert.Equal("sysmod:kernel", registry.Key);
        Assert.Equal("内核与系统核心", system.DisplayName);
    }

    [Fact]
    public void 驱动与固件类进程归入驱动与固件()
    {
        var classifier = CreateClassifier();

        var ati = classifier.Classify(Proc(810, "atieclxx.exe", @"C:\Windows\System32\atieclxx.exe"), null);
        var asus = classifier.Classify(Proc(811, "ArmouryDevice.exe", @"C:\Program Files\ASUS\ArmouryDevice\ArmouryDevice.exe"), null);
        var store = classifier.Classify(Proc(812, "helper.exe", @"C:\Windows\System32\DriverStore\FileRepository\oem12.inf_amd64\helper.exe"), null);
        var aggregator = classifier.Classify(Proc(813, "aggregatorhost.exe", @"C:\Windows\System32\aggregatorhost.exe"), null);

        Assert.Equal("sysmod:drivers", ati.Key);
        Assert.Equal("驱动与固件", ati.DisplayName);
        Assert.Equal("sysmod:drivers", asus.Key);
        Assert.Equal("sysmod:drivers", store.Key);
        Assert.Equal("sysmod:drivers", aggregator.Key);
    }

    [Fact]
    public void 显卡驱动仍归入图形界面而不是驱动与固件()
    {
        var classifier = CreateClassifier();

        var nv = classifier.Classify(Proc(820, "nvcontainer.exe", @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe"), null);

        Assert.Equal("sysmod:graphics", nv.Key);
        Assert.Equal("图形界面与显卡驱动", nv.DisplayName);
    }

    [Fact]
    public void 桌面窗口管理器归入图形界面()
    {
        var classifier = CreateClassifier();

        var dwm = classifier.Classify(Proc(800, "dwm.exe", @"C:\Windows\System32\dwm.exe"), null);
        var nv = classifier.Classify(Proc(801, "nvcontainer.exe", @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe"), null);

        Assert.Equal("sysmod:graphics", dwm.Key);
        Assert.Equal("sysmod:graphics", nv.Key);
        Assert.Equal(AppKind.SystemModule, nv.Kind);
    }

    [Fact]
    public void 其余系统进程仍然并进Windows系统组()
    {
        var classifier = CreateClassifier();

        var svchost = classifier.Classify(Proc(900, "svchost.exe", @"C:\Windows\System32\svchost.exe"), null);

        Assert.Equal("sys:windows", svchost.Key);
        Assert.Equal(AppKind.SystemProcess, svchost.Kind);
    }

    [Fact]
    public void 关闭系统进程合并后模块规则不生效()
    {
        var settings = new AppSettings { MergeWindowsSystemProcesses = false };
        var classifier = CreateClassifier(settings);

        var dwm = classifier.Classify(Proc(800, "dwm.exe", @"C:\Windows\System32\dwm.exe"), null);

        Assert.NotEqual(AppKind.SystemModule, dwm.Kind);
        Assert.DoesNotContain("sysmod:", dwm.Key, StringComparison.Ordinal);
    }
}