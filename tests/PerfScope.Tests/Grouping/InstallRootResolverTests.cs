using PerfScope.Core.Grouping;
using PerfScope.Core.Models;

namespace PerfScope.Tests.Grouping;

public sealed class InstallRootResolverTests
{
    private static InstallRootResolver Create(FakeFileSystemProbe probe) => new(probe);

    [Fact]
    public void ProgramFiles_取相对路径前两段()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Program Files\Google\Chrome\Application\chrome.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"C:\Program Files\Google\Chrome", result.RootPath);
    }

    [Fact]
    public void ProgramFiles_只有一段时直接用它()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Program Files\7-Zip\7z.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"C:\Program Files\7-Zip", result.RootPath);
    }

    [Fact]
    public void ProgramFilesX86_同样按两段规则()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Program Files (x86)\Acme\OldApp\old.exe");

        Assert.NotNull(result);
        Assert.Equal(@"C:\Program Files (x86)\Acme\OldApp", result!.RootPath);
    }

    [Fact]
    public void LocalAppData_Programs_取下一级目录()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code\Code.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"C:\Users\Test\AppData\Local\Programs\Microsoft VS Code", result.RootPath);
    }

    [Fact]
    public void LocalAppData_厂商目录_取两段()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Users\Test\AppData\Local\Acme\Tool\tool.exe");

        Assert.NotNull(result);
        Assert.Equal(@"C:\Users\Test\AppData\Local\Acme\Tool", result!.RootPath);
    }

    [Fact]
    public void LocalAppData_系统容器_降级为_exe_所在目录()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"C:\Users\Test\AppData\Local\Temp\setup\app.exe");
        var resolver = Create(probe);

        var result = resolver.Resolve(@"C:\Users\Test\AppData\Local\Temp\setup\app.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.ExeDirectory, result!.Kind);
        Assert.Equal(@"C:\Users\Test\AppData\Local\Temp\setup", result.RootPath);
    }

    [Fact]
    public void LocalAppData_Microsoft_Windows_容器_降级为_exe_所在目录()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Users\Test\AppData\Local\Microsoft\Windows\INetCache\x.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.ExeDirectory, result!.Kind);
    }

    [Fact]
    public void RoamingAppData_取厂商与产品两段()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Users\Test\AppData\Roaming\Vendor\App\app.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"C:\Users\Test\AppData\Roaming\Vendor\App", result.RootPath);
    }

    [Fact]
    public void Steam_游戏目录取_common_下一段()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"D:\SteamLibrary\steamapps\common\Half-Life 2\bin\hl2.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Half-Life 2", result.RootPath);
        Assert.Equal("Half-Life 2", result.DisplayNameHint);
    }

    [Fact]
    public void 向上探测_卸载器特征()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"D:\Games\SomeGame\unins000.exe");
        probe.AddFile(@"D:\Games\SomeGame\bin\game.exe");
        var resolver = Create(probe);

        var result = resolver.Resolve(@"D:\Games\SomeGame\bin\game.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"D:\Games\SomeGame", result.RootPath);
    }

    [Fact]
    public void 向上探测_Electron_特征()
    {
        var probe = new FakeFileSystemProbe();
        probe.AddFile(@"D:\Apps\MyElectron\resources\app.asar");
        probe.AddFile(@"D:\Apps\MyElectron\resources\app\worker.exe");
        var resolver = Create(probe);

        var result = resolver.Resolve(@"D:\Apps\MyElectron\resources\app\worker.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"D:\Apps\MyElectron", result.RootPath);
    }

    [Fact]
    public void WindowsApps_UWP_包标识取名称与发布者()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.0.0_x64__8wekyb3d8bbwe\Calculator.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.InstallRoot, result!.Kind);
        Assert.Equal(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.0.0_x64__8wekyb3d8bbwe", result.RootPath);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", result.DisplayNameHint);
    }

    [Fact]
    public void 无法识别时回退到_exe_所在目录()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var result = resolver.Resolve(@"C:\Tools\portable\tool.exe");

        Assert.NotNull(result);
        Assert.Equal(AppKind.ExeDirectory, result!.Kind);
        Assert.Equal(@"C:\Tools\portable", result.RootPath);
    }

    [Fact]
    public void 空路径不抛异常并返回_null()
    {
        var resolver = Create(new FakeFileSystemProbe());

        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve(string.Empty));
        Assert.Null(resolver.Resolve("   "));
        Assert.Null(resolver.Resolve("chrome.exe"));
    }

    [Fact]
    public void 带NT前缀与引号的路径也能解析()
    {
        var resolver = Create(new FakeFileSystemProbe());

        var weirdPath = "\"" + @"\\?\C:\Program Files\Google\Chrome\Application\chrome.exe" + "\"";
        var result = resolver.Resolve(weirdPath);

        Assert.NotNull(result);
        Assert.Equal(@"C:\Program Files\Google\Chrome", result!.RootPath);
    }
}
