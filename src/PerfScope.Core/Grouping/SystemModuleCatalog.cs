using PerfScope.Core.Models;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 系统功能模块目录：把「Windows 自己占的那部分」按功能拆成有意义的模块
/// （WSL、虚拟机与容器、图形界面与显卡驱动、安全防护、搜索与索引、系统更新、音频、打印、内核与系统核心），
/// 而不是把所有系统进程一股脑塞进「Windows 系统」。
///
/// 只依赖进程自身的身份信息（映像名 / 安装路径），**不查询服务、不需要管理员权限、不读注册表**，
/// 因此开销可忽略，且在受限账户下同样可用。
/// </summary>
internal static class SystemModuleCatalog
{
    /// <summary>模块归并键前缀。</summary>
    public const string KeyPrefix = "sysmod:";

    private sealed record ModuleDefinition(
        string Id,
        string DisplayName,
        string Reason,
        string[] ImageNames,
        string[] PathFragments);

    private static readonly ModuleDefinition[] Modules =
    {
        new(
            "wsl",
            "WSL 子系统",
            "系统模块：WSL / Linux 子系统（vmmemWSL、wslhost、wslservice 等）",
            new[] { "vmmemwsl", "wsl", "wslhost", "wslservice", "wslconfig", "wslg", "weston", "bash" },
            new[] { @"\system32\lxss\", @"\system32\wsl" }),

        new(
            "vm",
            "虚拟机与容器",
            "系统模块：Hyper-V / 容器运行时（vmwp、vmcompute、docker、containerd 等）",
            new[]
            {
                "vmmem", "vmwp", "vmcompute", "vmms", "docker", "dockerd", "com.docker.backend",
                "com.docker.build", "com.docker.proxy", "containerd", "vpnkit", "vmware-vmx",
                "vmware-hostd", "vboxheadless", "vboxsvc",
            },
            new[] { @"\docker\", @"\com.docker.", @"\vmware\", @"\virtualbox\" }),

        new(
            "graphics",
            "图形界面与显卡驱动",
            "系统模块：桌面窗口管理器与显卡驱动（dwm、nvcontainer、AMD / Intel 显卡助手等）",
            new[]
            {
                "dwm", "nvcontainer", "nvsphelper64", "nvshare", "nvtray", "nvtelemetry",
                "amdrsserv", "radeonsoftware", "amdfendrsr", "cncmd", "igfxem", "igfxhk", "igfxtray",
            },
            new[] { @"\nvidia corporation\", @"\amd\cnext\", @"\radeon\" }),

        // 驱动服务 / OEM 固件助手：名字通常是 atieclxx、amdpkgsvc、ArmouryDevice 这类，用户看不出是什么。
        // 放在图形之后，保证 nvcontainer / RadeonSoftware 之类仍归到「图形界面与显卡驱动」。
        new(
            "drivers",
            "驱动与固件",
            "系统模块：驱动服务与 OEM 硬件助手（Armoury Crate、AMD / ATI 驱动服务、Realtek、DriverStore 等）",
            new[]
            {
                "aggregatorhost", "armoury", "armourycrate", "armourydevice", "armouryhandleguard",
                "armouryliveservice", "lightingservice", "aacambientlighting",
                "aac3572drambal", "aac3572dramhal_x86", "aac3572mbhal_x86", "aackingstondramhal_x86",
                "atkexcomsvc", "atkexcom", "atkwmiacpi", "asuscertservice", "asusupdateservice",
                "asusupdatecheck", "asusoptimizationservice", "asusmainservice", "asuscertmanager",
                "atieclxx", "atiesrxx", "amdpkgsvc", "amdappcompatsvc", "amdow", "amduw23g",
                "rtkaudioservice", "rtkauduservice", "rtkbtman4", "rtkng", "ravbg64", "rtkvgm",
                "etdservice", "etdcctrl", "etdctrl", "hidfilter", "ipfsvc",
            },
            new[]
            {
                @"\driverstore\",
                @"\program files\asus\", @"\program files (x86)\asus\",
                @"\program files\amd\", @"\program files\ati technologies\",
                @"\program files\realtek\", @"\program files (x86)\realtek\",
                @"\program data\asus\",
            }),

        new(
            "security",
            "安全与防护",
            "系统模块：安全核心与 Defender（lsass、MsMpEng、SecurityHealth、SmartScreen）",
            new[]
            {
                "lsass", "lsaiso", "msmpeng", "nissrv", "mpcmdrun", "securityhealthservice",
                "securityhealthsystray", "smartscreen",
            },
            new[] { @"\windows defender\" }),

        new(
            "search",
            "搜索与索引",
            "系统模块：Windows 搜索与文件索引（SearchIndexer 等）",
            new[] { "searchindexer", "searchprotocolhost", "searchfilterhost", "searchui" },
            Array.Empty<string>()),

        new(
            "update",
            "系统更新",
            "系统模块：Windows 更新与组件维护（TiWorker、TrustedInstaller 等）",
            new[]
            {
                "tiworker", "trustedinstaller", "musnotification", "usoclient", "waasmedicagent",
                "mousocoreworker", "usocoreworker", "compattelrunner",
            },
            Array.Empty<string>()),

        new(
            "audio",
            "音频",
            "系统模块：音频引擎（audiodg）",
            new[] { "audiodg" },
            Array.Empty<string>()),

        new(
            "print",
            "打印",
            "系统模块：打印后台处理（spoolsv）",
            new[] { "spoolsv" },
            Array.Empty<string>()),

        new(
            "kernel",
            "内核与系统核心",
            "系统模块：内核 / 会话 / 内存压缩（System、Registry、Memory Compression、csrss 等）",
            new[]
            {
                "system", "registry", "memory compression", "secure system", "smss", "csrss",
                "wininit", "winlogon", "services", "fontdrvhost",
            },
            Array.Empty<string>()),
    };

    private static readonly Dictionary<string, ModuleDefinition> ByImageName = BuildImageIndex();
    private static readonly List<(string Fragment, ModuleDefinition Module)> ByPath = BuildPathIndex();

    private static Dictionary<string, ModuleDefinition> BuildImageIndex()
    {
        var map = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in Modules)
        {
            foreach (var name in module.ImageNames)
            {
                map.TryAdd(name, module);
            }
        }

        return map;
    }

    private static List<(string, ModuleDefinition)> BuildPathIndex()
    {
        var list = new List<(string, ModuleDefinition)>();
        foreach (var module in Modules)
        {
            foreach (var fragment in module.PathFragments)
            {
                list.Add((fragment, module));
            }
        }

        return list;
    }

    /// <summary>
    /// 尝试把一个进程归入系统功能模块。镜像名优先，其次看安装路径特征。
    /// </summary>
    /// <param name="exeBase">去掉 .exe 的映像名（小写）。</param>
    /// <param name="path">规范化后的完整路径（小写，可能为空）。</param>
    /// <param name="identity">命中时的归类结果。</param>
    public static bool TryMatch(string exeBase, string path, out AppIdentity identity)
    {
        identity = null!;
        if (exeBase.Length == 0)
        {
            return false;
        }

        if (ByImageName.TryGetValue(exeBase, out var module))
        {
            identity = new AppIdentity(
                KeyPrefix + module.Id,
                module.DisplayName,
                string.IsNullOrEmpty(path) ? null : PathText.DirectoryOf(path),
                AppKind.SystemModule,
                module.Reason);
            return true;
        }

        if (path.Length > 0)
        {
            foreach (var (fragment, candidate) in ByPath)
            {
                // 路径没有统一转小写，必须忽略大小写比较，否则 DriverStore 之类永远匹配不到
                if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    identity = new AppIdentity(
                        KeyPrefix + candidate.Id,
                        candidate.DisplayName,
                        PathText.DirectoryOf(path),
                        AppKind.SystemModule,
                        candidate.Reason);
                    return true;
                }
            }
        }

        return false;
    }
}
