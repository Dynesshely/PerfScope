using System.Diagnostics;
using PerfScope.Core.Grouping;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Tests.Grouping;

public sealed class AppClassifierTests
{
    private static AppSettings DefaultSettings() => new();

    private static ProcessRawSample Proc(int pid, int parentPid, string imageName, string? path)
        => new(pid, parentPid, imageName, path, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Chrome_主体与代理进程归为同一应用()
    {
        var probe = new FakeFileSystemProbe();
        const string appDir = @"C:\Program Files\Google\Chrome\Application";
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe", "Google Chrome", "Google LLC");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var chrome = classifier.Classify(Proc(100, 1, "chrome.exe", appDir + @"\chrome.exe"), null);
        var proxy = classifier.Classify(Proc(101, 100, "chrome_proxy.exe", appDir + @"\chrome_proxy.exe"), null);

        Assert.Equal(chrome.Key, proxy.Key);
        Assert.Equal(AppKind.ProductName, chrome.Kind);
        Assert.Equal("Google Chrome", chrome.DisplayName);
        Assert.StartsWith("product:", chrome.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Chrome_拿不到版本信息的_helper_通过安装根并进同一应用()
    {
        var probe = new FakeFileSystemProbe();
        const string appDir = @"C:\Program Files\Google\Chrome\Application";
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddFile(@"C:\Program Files\Google\Chrome\Application\nacl64.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var chrome = classifier.Classify(Proc(100, 1, "chrome.exe", appDir + @"\chrome.exe"), null);
        var helper = classifier.Classify(Proc(102, 100, "nacl64.exe", appDir + @"\nacl64.exe"), null);

        Assert.Equal(chrome.Key, helper.Key);
        Assert.Equal(AppKind.InstallRoot, helper.Kind);
        Assert.Contains("安装根目录已归属", helper.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Chrome_helper_先被归类也能在主体归类后并回来()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddFile(@"C:\Program Files\Google\Chrome\Application\nacl64.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        // helper 先出现：此时还没有「安装根 → 产品」的别名，只能落到目录级。
        var helperFirst = classifier.Classify(Proc(102, 100, "nacl64.exe", @"C:\Program Files\Google\Chrome\Application\nacl64.exe"), null);
        var chrome = classifier.Classify(Proc(100, 1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), null);
        var helperAfter = classifier.Classify(Proc(102, 100, "nacl64.exe", @"C:\Program Files\Google\Chrome\Application\nacl64.exe"), null);

        Assert.NotEqual(chrome.Key, helperFirst.Key);
        Assert.Equal(chrome.Key, helperAfter.Key);
    }

    [Fact]
    public void VSCode_主程序与同目录_helper_归并()
    {
        var probe = new FakeFileSystemProbe();
        const string root = @"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code";
        probe.AddExe(@"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Visual Studio Code", "Microsoft Corporation");
        probe.AddFile(
            @"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code\resources\app\extensions\cpptools\helper.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var code = classifier.Classify(Proc(200, 1, "Code.exe", root + @"\Code.exe"), null);
        var helper = classifier.Classify(
            Proc(201, 200, "helper.exe", root + @"\resources\app\extensions\cpptools\helper.exe"),
            null);

        Assert.Equal(code.Key, helper.Key);
        Assert.Equal("Visual Studio Code", code.DisplayName);
    }

    [Fact]
    public void Electron_应用与子进程归并到探测出的安装根()
    {
        var probe = new FakeFileSystemProbe();
        const string root = @"D:\Apps\MyElectron";
        probe.AddFile(@"D:\Apps\MyElectron\unins000.exe");
        probe.AddFile(@"D:\Apps\MyElectron\resources\app.asar");
        probe.AddExe(@"D:\Apps\MyElectron\MyElectron.exe", "My Electron App", "Acme Inc.");
        probe.AddFile(@"D:\Apps\MyElectron\resources\app\worker.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var main = classifier.Classify(Proc(300, 1, "MyElectron.exe", root + @"\MyElectron.exe"), null);
        var worker = classifier.Classify(Proc(301, 300, "worker.exe", root + @"\resources\app\worker.exe"), null);

        Assert.Equal(main.Key, worker.Key);
        Assert.Equal(AppKind.InstallRoot, worker.Kind);
        Assert.Equal(root, worker.RootPath);
    }

    [Fact]
    public void UWP_包名解析为应用标识()
    {
        var probe = new FakeFileSystemProbe();
        const string package = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.0.0_x64__8wekyb3d8bbwe";
        probe.AddFile(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.0.0_x64__8wekyb3d8bbwe\Calculator.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var identity = classifier.Classify(Proc(400, 1, "Calculator.exe", package + @"\Calculator.exe"), null);

        Assert.Equal(AppKind.InstallRoot, identity.Kind);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", identity.DisplayName);
        Assert.Contains("windowsapps", identity.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 系统进程合并为系统组而_explorer_不并入()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"C:\Windows\System32\svchost.exe");
        probe.AddFile(@"C:\Windows\explorer.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var svchostA = classifier.Classify(Proc(10, 4, "svchost.exe", @"C:\Windows\System32\svchost.exe"), null);
        var svchostB = classifier.Classify(Proc(11, 4, "svchost.exe", null), null);
        var svchostC = classifier.Classify(Proc(12, 4, "svchost.exe", @"C:\Windows\System32\svchost.exe"), null);
        var explorer = classifier.Classify(Proc(13, 1, "explorer.exe", @"C:\Windows\explorer.exe"), null);

        Assert.Equal("sys:windows", svchostA.Key);
        Assert.Equal("sys:windows", svchostB.Key);
        Assert.Equal("sys:windows", svchostC.Key);
        Assert.Equal(AppKind.SystemProcess, svchostA.Kind);
        Assert.Equal("Windows 系统", svchostA.DisplayName);

        Assert.NotEqual("sys:windows", explorer.Key);
        Assert.Equal("Windows 资源管理器", explorer.DisplayName);
        Assert.Equal(@"C:\Windows", explorer.RootPath);
    }

    [Fact]
    public void 用户可以关闭系统进程聚合()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"C:\Windows\System32\svchost.exe");
        var settings = new AppSettings { MergeWindowsSystemProcesses = false };
        var classifier = new AppClassifier(settings, probe);

        var identity = classifier.Classify(Proc(10, 4, "svchost.exe", @"C:\Windows\System32\svchost.exe"), null);

        Assert.NotEqual("sys:windows", identity.Key);
        Assert.Equal(AppKind.ExeDirectory, identity.Kind);
    }

    [Fact]
    public void 用户规则三种动作与优先级()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"C:\Windows\System32\notepad.exe");
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddFile(@"C:\Tools\code.exe");
        var settings = new AppSettings();
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "notepad.exe", Action = AppRuleAction.Exclude });
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "code.exe", Action = AppRuleAction.MergeToName, Target = "编辑器" });
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "chrome.exe", Action = AppRuleAction.Rename, Target = "浏览器" });
        var classifier = new AppClassifier(settings, probe);

        // 排除优先于系统进程聚合（规则在最前）
        var notepad = classifier.Classify(Proc(1, 0, "notepad.exe", @"C:\Windows\System32\notepad.exe"), null);
        Assert.True(notepad.Excluded);
        Assert.Equal(AppKind.CustomRule, notepad.Kind);

        var code = classifier.Classify(Proc(2, 0, "code.exe", @"C:\Tools\code.exe"), null);
        Assert.Equal("rule:merge:编辑器", code.Key);
        Assert.Equal("编辑器", code.DisplayName);
        Assert.False(code.Excluded);

        var chrome = classifier.Classify(Proc(3, 0, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), null);
        Assert.StartsWith("product:", chrome.Key, StringComparison.Ordinal);
        Assert.Equal("浏览器", chrome.DisplayName);
        Assert.Contains("显示名由用户规则指定为「浏览器」", chrome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 用户规则支持路径包含匹配()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"D:\PortableApp\bin\run.exe", "Run", "Portable");
        var settings = new AppSettings();
        settings.Rules.Add(new AppRule
        {
            Match = AppRuleMatch.PathContains,
            Pattern = @"D:\PortableApp",
            Action = AppRuleAction.MergeToName,
            Target = "便携工具",
        });
        var classifier = new AppClassifier(settings, probe);

        var identity = classifier.Classify(Proc(1, 0, "run.exe", @"D:\PortableApp\bin\run.exe"), null);

        Assert.Equal("rule:merge:便携工具", identity.Key);
        Assert.Equal("便携工具", identity.DisplayName);
    }

    [Fact]
    public void 父进程继承只在弱归属时生效()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddFile(@"C:\Temp\child.exe");
        probe.AddExe(@"C:\Other\sibling.exe", "Other Sibling App", "Other Inc.");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var parent = Proc(100, 1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        var parentIdentity = classifier.Classify(parent, null);

        var weakChild = classifier.Classify(Proc(500, 100, "child.exe", @"C:\Temp\child.exe"), parent);
        Assert.Equal(parentIdentity.Key, weakChild.Key);
        Assert.Contains("继承自父进程", weakChild.Reason, StringComparison.Ordinal);

        // 自身有强归属（版本信息）时不继承
        var strongChild = classifier.Classify(Proc(501, 100, "sibling.exe", @"C:\Other\sibling.exe"), parent);
        Assert.NotEqual(parentIdentity.Key, strongChild.Key);
        Assert.Equal(AppKind.ProductName, strongChild.Kind);
    }

    [Fact]
    public void 关闭父进程继承后弱归属保持原样()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        probe.AddFile(@"C:\Temp\child.exe");
        var settings = new AppSettings { InheritParentGroup = false };
        var classifier = new AppClassifier(settings, probe);

        var parent = Proc(100, 1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        var child = classifier.Classify(Proc(500, 100, "child.exe", @"C:\Temp\child.exe"), parent);

        Assert.Equal(@"dir:c:\temp", child.Key);
        Assert.Equal(AppKind.ExeDirectory, child.Kind);
    }

    [Fact]
    public void 空路径与不存在的文件走兜底且不抛异常()
    {
        var probe = new FakeFileSystemProbe();
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var noPath = classifier.Classify(Proc(1, 0, "ghost.exe", null), null);
        var emptyName = classifier.Classify(Proc(2, 0, string.Empty, null), null);
        var missingFile = classifier.Classify(Proc(3, 0, "x.exe", @"\\?\C:\nope\x.exe"), null);
        var defaultSample = classifier.Classify(default, null);
        var weirdParent = classifier.Classify(Proc(4, 0, "y.exe", "   "), Proc(5, 0, string.Empty, null));

        Assert.Equal("exe:ghost.exe", noPath.Key);
        Assert.Equal(AppKind.ExeName, noPath.Kind);
        Assert.Equal("unknown", emptyName.Key);
        Assert.Equal(AppKind.Unknown, emptyName.Kind);
        Assert.Equal(@"dir:c:\nope", missingFile.Key);
        Assert.Equal("unknown", defaultSample.Key);
        Assert.Equal("exe:y.exe", weirdParent.Key);
    }

    [Fact]
    public void 关闭产品名归并后走安装根()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        var settings = new AppSettings { GroupByProductName = false };
        var classifier = new AppClassifier(settings, probe);

        var identity = classifier.Classify(Proc(1, 0, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), null);

        Assert.Equal(AppKind.InstallRoot, identity.Kind);
        Assert.Equal(@"dir:c:\program files\google\chrome", identity.Key);
        Assert.Equal("Chrome", identity.DisplayName);
    }

    [Fact]
    public void 无意义的产品名会被过滤()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(
            @"C:\Program Files\Windows NT\Accessories\wordpad.exe",
            "Microsoft® Windows® Operating System",
            "Microsoft Corporation");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var identity = classifier.Classify(Proc(1, 0, "wordpad.exe", @"C:\Program Files\Windows NT\Accessories\wordpad.exe"), null);

        Assert.NotEqual(AppKind.ProductName, identity.Kind);
        Assert.Equal(AppKind.InstallRoot, identity.Kind);
        Assert.DoesNotContain("Windows®", identity.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void 展示名清洗去掉商标与位宽后缀()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Vendor\App\app.exe", "Acme Studio™ (x64)", "Acme");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var identity = classifier.Classify(Proc(1, 0, "app.exe", @"C:\Program Files\Vendor\App\app.exe"), null);

        Assert.Equal("Acme Studio", identity.DisplayName);
    }

    [Fact]
    public void 设置热更新立即生效并清空归类缓存()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        var classifier = new AppClassifier(DefaultSettings(), probe);
        const string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        var before = classifier.Classify(Proc(1, 0, "chrome.exe", path), null);
        Assert.StartsWith("product:", before.Key, StringComparison.Ordinal);

        var updated = new AppSettings();
        updated.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "chrome.exe", Action = AppRuleAction.Exclude });
        classifier.OnSettingsChanged(updated);

        var after = classifier.Classify(Proc(1, 0, "chrome.exe", path), null);
        Assert.True(after.Excluded);
    }

    [Fact]
    public void 版本信息缓存开关生效()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        const string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var classifier = new AppClassifier(new AppSettings { UseVersionInfoCache = true }, probe);

        classifier.Classify(Proc(1, 0, "chrome.exe", path), null);
        var readsAfterFirst = probe.VersionReadCount;
        Assert.Equal(1, readsAfterFirst);

        // 归类缓存命中：不再读盘
        classifier.Classify(Proc(2, 0, "chrome.exe", path), null);
        Assert.Equal(readsAfterFirst, probe.VersionReadCount);

        // 设置热更新只清归类缓存，版本信息缓存保留
        classifier.OnSettingsChanged(new AppSettings { UseVersionInfoCache = true });
        classifier.Classify(Proc(3, 0, "chrome.exe", path), null);
        Assert.Equal(readsAfterFirst, probe.VersionReadCount);

        // 关掉版本信息缓存后，归类缓存未命中就会重新读取
        classifier.OnSettingsChanged(new AppSettings { UseVersionInfoCache = false });
        classifier.Classify(Proc(4, 0, "chrome.exe", path), null);
        Assert.True(probe.VersionReadCount > readsAfterFirst);
    }

    [Fact]
    public void 重置缓存后重新归类仍能得到一致结果()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "Google LLC");
        const string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var before = classifier.Classify(Proc(1, 0, "chrome.exe", path), null);
        Assert.True(classifier.CachedIdentityCount > 0);
        classifier.ResetCache();
        Assert.Equal(0, classifier.CachedIdentityCount);

        var after = classifier.Classify(Proc(1, 0, "chrome.exe", path), null);
        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.DisplayName, after.DisplayName);
    }

    [Fact]
    public void 预热后_500_次归类在_100ms_内完成()
    {
        var probe = new FakeFileSystemProbe();
        var settings = DefaultSettings();
        var samples = new List<(ProcessRawSample Sample, ProcessRawSample? Parent)>(32);

        for (var i = 0; i < 16; i++)
        {
            var path = $@"C:\Program Files\Vendor{i}\App{i}\app{i}.exe";
            probe.AddExe(path, $"Vendor{i} App{i}", $"Vendor{i}");
            samples.Add((Proc(1000 + i, 1, $"app{i}.exe", path), null));
        }

        for (var i = 0; i < 16; i++)
        {
            var path = $@"C:\Windows\System32\svc{i}.exe";
            probe.AddFile(path);
            samples.Add((Proc(2000 + i, 4, $"svc{i}.exe", path), null));
        }

        var classifier = new AppClassifier(settings, probe);

        // 预热：让所有路径进入归类缓存
        foreach (var item in samples)
        {
            classifier.Classify(item.Sample, item.Parent);
        }

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            var item = samples[i % samples.Count];
            classifier.Classify(item.Sample, item.Parent);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"500 次归类耗时 {stopwatch.ElapsedMilliseconds}ms，超出预算");
    }

    [Fact]
    public void 构建产物目录不会被当作应用名()
    {
        var probe = new FakeFileSystemProbe();
        const string path = @"C:\Projects\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe";
        probe.AddFile(@"C:\Projects\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var identity = classifier.Classify(Proc(1, 0, "PerfScope.Cli.exe", path), null);

        Assert.Equal(AppKind.ExeDirectory, identity.Kind);
        Assert.Equal("PerfScope.Cli", identity.DisplayName);
        Assert.DoesNotContain("net10.0-windows", identity.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 构建产物目录下优先用文件说明做展示名()
    {
        var probe = new FakeFileSystemProbe();
        const string path = @"C:\Projects\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe";
        probe.AddExe(
            @"C:\Projects\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe",
            "PerfScope.Cli",
            "PerfScope",
            "PerfScope 命令行工具");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var identity = classifier.Classify(Proc(1, 0, "PerfScope.Cli.exe", path), null);

        Assert.Equal("PerfScope 命令行工具", identity.DisplayName);
    }

    [Fact]
    public void 同名不同安装目录的应用展示名不同且跨帧稳定()
    {
        var probe = new FakeFileSystemProbe();
        const string first = @"C:\Apps\Alpha\Weixin\Weixin.exe";
        const string second = @"D:\Apps\Beta\Weixin\Weixin.exe";
        probe.AddFile(first);
        probe.AddFile(second);
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var firstIdentity = classifier.Classify(Proc(1, 0, "Weixin.exe", first), null);
        var secondIdentity = classifier.Classify(Proc(2, 0, "Weixin.exe", second), null);

        Assert.NotEqual(firstIdentity.Key, secondIdentity.Key);

        // 同一帧内展示名必须唯一（第一帧可能是「Weixin」+「Weixin（Beta）」，之后收敛成两个带后缀的名字）
        Assert.NotEqual(firstIdentity.DisplayName, secondIdentity.DisplayName);

        // 第二帧起稳定：两个都带确定性后缀，且再来一次结果不变
        var firstSteady = classifier.Classify(Proc(1, 0, "Weixin.exe", first), null);
        var secondSteady = classifier.Classify(Proc(2, 0, "Weixin.exe", second), null);
        Assert.Equal("Weixin（Alpha）", firstSteady.DisplayName);
        Assert.Equal("Weixin（Beta）", secondSteady.DisplayName);
        Assert.Equal(firstSteady.DisplayName, classifier.Classify(Proc(1, 0, "Weixin.exe", first), null).DisplayName);
        Assert.Equal(secondSteady.DisplayName, classifier.Classify(Proc(2, 0, "Weixin.exe", second), null).DisplayName);
    }

    [Fact]
    public void 沙箱多账号根目录用最短尾部区分段()
    {
        var probe = new FakeFileSystemProbe();
        const string first = @"D:\Sandbox\Mainland_Apps\drive\c\Program Files\Tencent\QQNT\QQ.exe";
        const string second = @"D:\Sandbox\Mainland_Apps_Multi_1\drive\c\Program Files\Tencent\QQNT\QQ.exe";
        probe.AddFile(first);
        probe.AddFile(second);
        var classifier = new AppClassifier(DefaultSettings(), probe);

        classifier.Classify(Proc(1, 0, "QQ.exe", first), null);
        classifier.Classify(Proc(2, 0, "QQ.exe", second), null);

        var firstSteady = classifier.Classify(Proc(1, 0, "QQ.exe", first), null);
        var secondSteady = classifier.Classify(Proc(2, 0, "QQ.exe", second), null);

        Assert.Equal("QQNT（Mainland_Apps）", firstSteady.DisplayName);
        Assert.Equal("QQNT（Mainland_Apps_Multi_1）", secondSteady.DisplayName);
    }

    [Fact]
    public void Explorer_与系统进程的边界()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"C:\Windows\explorer.exe");
        probe.AddFile(@"C:\Windows\System32\svchost.exe");
        var classifier = new AppClassifier(DefaultSettings(), probe);

        var explorer = classifier.Classify(Proc(1, 0, "explorer.exe", @"C:\Windows\explorer.exe"), null);
        var svchost = classifier.Classify(Proc(2, 0, "svchost.exe", @"C:\Windows\System32\svchost.exe"), null);

        Assert.Equal("Windows 资源管理器", explorer.DisplayName);
        Assert.NotEqual("sys:windows", explorer.Key);
        Assert.Equal("sys:windows", svchost.Key);
    }
}

