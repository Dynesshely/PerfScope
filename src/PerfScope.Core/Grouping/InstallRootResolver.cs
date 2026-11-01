using PerfScope.Core.Models;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 安装根目录解析结果。
/// </summary>
/// <param name="RootPath">安装根目录（已规范化、无末尾反斜杠）。</param>
/// <param name="Kind">解析来源：命中安装根规则为 <see cref="AppKind.InstallRoot"/>，兜底为 exe 所在目录时是 <see cref="AppKind.ExeDirectory"/>。</param>
/// <param name="DisplayNameHint">展示名提示（UWP 包族名、Steam 游戏名等），可为 <c>null</c>。</param>
public sealed record InstallRootResult(string RootPath, AppKind Kind, string? DisplayNameHint);

/// <summary>
/// 安装根目录解析器：把「可执行文件路径」推导成「这个应用装在哪」。
/// <para>规则顺序：</para>
/// <list type="number">
///   <item><c>%ProgramFiles%\WindowsApps\&lt;包目录&gt;</c>：UWP 应用，取 <c>名称_发布者ID</c> 作为应用标识。</item>
///   <item><c>%ProgramFiles%</c> / <c>%ProgramFiles(x86)%</c>：取相对路径前两段（Vendor\App），只有一段就用它。</item>
///   <item><c>%LOCALAPPDATA%\Programs\&lt;App&gt;</c> / <c>%LOCALAPPDATA%\&lt;Vendor&gt;\&lt;App&gt;</c>：取 App 或两段；Temp / Packages / Microsoft\Windows 这类系统容器降级到后面的规则。</item>
///   <item><c>%APPDATA%\&lt;Vendor&gt;\&lt;App&gt;</c>：同上。</item>
///   <item>Steam 游戏：路径含 <c>steamapps\common\&lt;Game&gt;</c> 时取 <c>&lt;Game&gt;</c>。</item>
///   <item>通用向上探测：从 exe 所在目录向上最多 4 层，遇到卸载器特征或 Electron 特征即认定为安装根。</item>
///   <item>都不满足：用 exe 所在目录，<see cref="AppKind"/> 记为 <see cref="AppKind.ExeDirectory"/>。</item>
/// </list>
/// 本类只做纯字符串 + 文件系统探测，不读写设置、不做缓存（缓存在 <see cref="AppClassifier"/> 里统一做）。
/// </summary>
public sealed class InstallRootResolver
{
    private const int MaxUpwardProbeDepth = 4;
    private const string SteamMarker = "steamapps\\common\\";

    private static readonly HashSet<string> LocalAppDataContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp",
        "packages",
        "microsoft",
        "crashdumps",
        "connecteddevicesplatform",
        "d3dscache",
        "publishercache",
        "comms",
        "placeholdertilelogofolder",
        "virtualstore",
        "application data",
        "history",
        "cache",
        "elevateddiagnostics",
        "group policy",
        "microsoftedgeupdate",
        "packages",
    };

    private static readonly HashSet<string> RoamingContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft",
        "packages",
        "temp",
        "virtualstore",
        "application data",
        "local settings",
    };

    private static readonly HashSet<string> UninstallerFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall.exe",
        "unins000.exe",
        "unins001.exe",
        "uninst.exe",
        "uninstaller.exe",
    };

    private readonly IFileSystemProbe _probe;
    private readonly string _windows;
    private readonly string[] _programFilesRoots;
    private readonly string[] _windowsAppsRoots;
    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly string _programData;

    /// <summary>用指定的探测实现构造解析器。</summary>
    /// <param name="probe">文件系统 / 已知目录探测。</param>
    public InstallRootResolver(IFileSystemProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _windows = PathText.Normalize(probe.WindowsDirectory);
        _localAppData = PathText.Normalize(probe.LocalAppDataDirectory);
        _roamingAppData = PathText.Normalize(probe.RoamingAppDataDirectory);
        _programData = PathText.Normalize(probe.ProgramDataDirectory);

        var programFiles = new List<string>(2);
        var programFilesX86 = PathText.Normalize(probe.ProgramFilesX86Directory);
        var programFiles64 = PathText.Normalize(probe.ProgramFilesDirectory);
        if (programFiles64.Length > 0)
        {
            programFiles.Add(programFiles64);
        }

        if (programFilesX86.Length > 0 && !string.Equals(programFilesX86, programFiles64, StringComparison.OrdinalIgnoreCase))
        {
            programFiles.Add(programFilesX86);
        }

        _programFilesRoots = programFiles.ToArray();
        _windowsAppsRoots = _programFilesRoots
            .Select(root => PathText.Combine(root, "WindowsApps"))
            .ToArray();
    }

    /// <summary>
    /// 解析安装根目录。
    /// </summary>
    /// <param name="executablePath">可执行文件完整路径（可以带 <c>\\?\</c> 前缀、<c>\\SystemRoot\</c> 前缀或引号，内部会规范化）。</param>
    /// <returns>解析结果；连目录部分都拿不到时返回 <c>null</c>（调用方应退到「按映像名归并」）。</returns>
    public InstallRootResult? Resolve(string? executablePath)
    {
        var exePath = PathText.Normalize(executablePath, _windows);
        if (exePath.Length == 0)
        {
            return null;
        }

        var directory = PathText.DirectoryOf(exePath);
        if (directory.Length == 0)
        {
            return null;
        }

        var resolved = ResolveWindowsApps(directory)
            ?? ResolveProgramFiles(directory)
            ?? ResolveLocalAppData(directory)
            ?? ResolveRoamingAppData(directory)
            ?? ResolveSteam(directory)
            ?? ProbeUpwards(directory);

        return resolved ?? new InstallRootResult(directory, AppKind.ExeDirectory, null);
    }

    private InstallRootResult? ResolveWindowsApps(string directory)
    {
        foreach (var windowsApps in _windowsAppsRoots)
        {
            var segments = PathText.SegmentsAfter(directory, windowsApps);
            if (segments.Length == 0)
            {
                continue;
            }

            var packageDirectory = segments[0];
            var root = PathText.Combine(windowsApps, packageDirectory);
            return new InstallRootResult(root, AppKind.InstallRoot, BuildUwpIdentity(packageDirectory));
        }

        return null;
    }

    private InstallRootResult? ResolveProgramFiles(string directory)
    {
        foreach (var programFiles in _programFilesRoots)
        {
            if (!PathText.IsUnder(directory, programFiles))
            {
                continue;
            }

            var segments = PathText.SegmentsAfter(directory, programFiles);
            if (segments.Length == 0)
            {
                // exe 直接躺在 Program Files 根下，交给后面的规则。
                continue;
            }

            if (string.Equals(segments[0], "WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                // UWP 已由 ResolveWindowsApps 处理（这里只会在 WindowsApps 根下没拿到包目录时出现）。
                continue;
            }

            var root = segments.Length == 1
                ? PathText.Combine(programFiles, segments[0])
                : PathText.Combine(PathText.Combine(programFiles, segments[0]), segments[1]);

            return new InstallRootResult(root, AppKind.InstallRoot, null);
        }

        return null;
    }

    private InstallRootResult? ResolveLocalAppData(string directory)
    {
        if (_localAppData.Length == 0)
        {
            return null;
        }

        var segments = PathText.SegmentsAfter(directory, _localAppData);
        if (segments.Length == 0)
        {
            return null;
        }

        if (string.Equals(segments[0], "Programs", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 2)
            {
                return null;
            }

            var root = PathText.Combine(PathText.Combine(_localAppData, "Programs"), segments[1]);
            return new InstallRootResult(root, AppKind.InstallRoot, null);
        }

        if (LocalAppDataContainers.Contains(segments[0]))
        {
            // Temp / Packages / Microsoft\Windows 之类的系统容器：不是一个应用的安装根。
            return null;
        }

        if (segments.Length >= 2)
        {
            var root = PathText.Combine(PathText.Combine(_localAppData, segments[0]), segments[1]);
            return new InstallRootResult(root, AppKind.InstallRoot, null);
        }

        return new InstallRootResult(PathText.Combine(_localAppData, segments[0]), AppKind.InstallRoot, null);
    }

    private InstallRootResult? ResolveRoamingAppData(string directory)
    {
        if (_roamingAppData.Length == 0)
        {
            return null;
        }

        var segments = PathText.SegmentsAfter(directory, _roamingAppData);
        if (segments.Length == 0)
        {
            return null;
        }

        if (RoamingContainers.Contains(segments[0]))
        {
            return null;
        }

        if (segments.Length >= 2)
        {
            var root = PathText.Combine(PathText.Combine(_roamingAppData, segments[0]), segments[1]);
            return new InstallRootResult(root, AppKind.InstallRoot, null);
        }

        return new InstallRootResult(PathText.Combine(_roamingAppData, segments[0]), AppKind.InstallRoot, null);
    }

    private static InstallRootResult? ResolveSteam(string directory)
    {
        var index = directory.IndexOf(SteamMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var gameStart = index + SteamMarker.Length;
        if (gameStart >= directory.Length)
        {
            return null;
        }

        var separator = directory.IndexOf('\\', gameStart);
        var game = separator < 0 ? directory.Substring(gameStart) : directory.Substring(gameStart, separator - gameStart);
        if (game.Length == 0)
        {
            return null;
        }

        var root = separator < 0 ? directory : directory.Substring(0, separator);
        return new InstallRootResult(root, AppKind.InstallRoot, game);
    }

    private InstallRootResult? ProbeUpwards(string directory)
    {
        var candidate = directory;
        for (var level = 0; level <= MaxUpwardProbeDepth; level++)
        {
            if (!IsPlausibleRoot(candidate))
            {
                return null;
            }

            // 先做便宜的 Electron 特征（两次 File.Exists），再枚举目录找卸载器：
            // Electron / Chromium 应用占多数，这样能把大量目录枚举省掉。
            if (HasElectronSignature(candidate) || HasUninstaller(candidate))
            {
                return new InstallRootResult(candidate, AppKind.InstallRoot, null);
            }

            var parent = PathText.Parent(candidate);
            if (parent is null || parent.Length == 0 || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            candidate = parent;
        }

        return null;
    }

    private bool IsPlausibleRoot(string directory)
    {
        if (string.IsNullOrEmpty(directory) || PathText.IsDriveRoot(directory))
        {
            return false;
        }

        if (_windows.Length > 0 && PathText.IsUnder(directory, _windows))
        {
            return false;
        }

        foreach (var programFiles in _programFilesRoots)
        {
            if (PathText.IsUnder(directory, programFiles))
            {
                return false;
            }
        }

        if (!PassesContainerFilter(directory, _localAppData, LocalAppDataContainers))
        {
            return false;
        }

        if (!PassesContainerFilter(directory, _roamingAppData, RoamingContainers))
        {
            return false;
        }

        if (_programData.Length > 0 && PathText.IsUnder(directory, _programData))
        {
            var segments = PathText.SegmentsAfter(directory, _programData);
            if (segments.Length == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PassesContainerFilter(string directory, string root, HashSet<string> containers)
    {
        if (root.Length == 0 || !PathText.IsUnder(directory, root))
        {
            return true;
        }

        var segments = PathText.SegmentsAfter(directory, root);
        if (segments.Length == 0)
        {
            return false;
        }

        return !containers.Contains(segments[0]);
    }

    private bool HasUninstaller(string directory)
    {
        var names = _probe.EnumerateFileNames(directory);
        foreach (var name in names)
        {
            if (UninstallerFileNames.Contains(name))
            {
                return true;
            }

            if (name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasElectronSignature(string directory)
    {
        return _probe.FileExists(PathText.Combine(PathText.Combine(directory, "resources"), "app.asar"))
            || _probe.FileExists(PathText.Combine(PathText.Combine(PathText.Combine(directory, "resources"), "app"), "package.json"));
    }

    /// <summary>
    /// UWP 包目录名（<c>名称_版本_架构__发布者ID</c>）→ 应用标识 <c>名称_发布者ID</c>
    /// （按 <c>_</c> 分段取第 0 段与最后一段）。
    /// </summary>
    private static string BuildUwpIdentity(string packageDirectory)
    {
        var parts = packageDirectory.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return packageDirectory;
        }

        return parts[0] + "_" + parts[parts.Length - 1];
    }
}
