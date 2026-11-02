using System.Collections.Concurrent;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Grouping;

/// <summary>
/// 应用归并引擎：把单个进程「挂钩」到一个应用上，而不是按进程展示。
/// <para>归并优先级（严格按此顺序，命中即返回，并把依据写进 <see cref="AppIdentity.Reason"/>）：</para>
/// <list type="number">
///   <item>用户自定义规则（<see cref="AppSettings.Rules"/>，按顺序）：排除 / 合并到指定名称 / 只改显示名。</item>
///   <item>Windows 系统进程聚合（System32、SysWOW64、WinSxS、servicing、DriverStore 或 svchost 等系统映像名）。</item>
///   <item>版本信息：ProductName + CompanyName（过滤掉「Microsoft® Windows® Operating System」这类无意义产品名）。</item>
///   <item>安装根目录（<see cref="InstallRootResolver"/>）。</item>
///   <item>exe 所在目录。</item>
///   <item>exe 文件名。</item>
///   <item>兜底分组（<see cref="AppSettings.UnknownGroupName"/>）。</item>
/// </list>
/// <para>
/// 另外三条增强（不改变上面的层级，只解决「同一应用的不同 exe 落在不同层级 / 展示名重复」的问题）：
/// ① 安装根别名：某个 exe 用版本信息判定了身份后，把它所在安装根记下来，同根下没有版本信息的 helper 沿用这个身份；
/// ② 父进程继承：自身只能落到「目录 / 文件名 / 兜底」这类弱归属时，沿用父进程的强归属；
/// ③ 展示名去重：同名但不同安装目录的应用追加确定性后缀（见 <see cref="DisplayNameRegistry"/>）。
/// </para>
/// <para>
/// 性能：exe 路径 → 归类结果是 LRU(4096) + 10 分钟 TTL 的字典命中，热路径不读盘、不 new 大对象；
/// 线程安全，可被采样后台线程直接调用。
/// </para>
/// </summary>
public sealed class AppClassifier : IAppClassifier
{
    private const int CacheCapacity = 4096;
    private const string ImageCacheKeyPrefix = "\u0001";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>常见的系统组件映像名（去扩展名、小写）。路径拿不到的受保护进程靠它兜住。</summary>
    private static readonly HashSet<string> SystemImageBaseNames = new(StringComparer.Ordinal)
    {
        "svchost",
        "csrss",
        "lsass",
        "services",
        "wininit",
        "smss",
        "winlogon",
        "fontdrvhost",
        "dwm",
        "sihost",
        "conhost",
        "dllhost",
        "audiodg",
        "wmiprvse",
        "runtimebroker",
        "taskhostw",
        "searchindexer",
        "searchprotocolhost",
        "searchfilterhost",
        "ctfmon",
        "textinputhost",
        "system",
        "registry",
        "idle",
        "memory compression",
        "secure system",
    };

    /// <summary>内置产品名映射：少数系统组件不并入系统组，而是作为一个独立「应用」展示。</summary>
    private static readonly Dictionary<string, WellKnownApp> WellKnownApps = new(StringComparer.Ordinal)
    {
        ["explorer"] = new WellKnownApp(
            "exe:explorer.exe",
            "Windows 资源管理器",
            "系统内置产品名映射：explorer.exe 归为「Windows 资源管理器」，不并入系统分组"),
    };

    private readonly IFileSystemProbe _probe;
    private readonly InstallRootResolver _installRootResolver;
    private readonly LruCache<AppIdentity> _identityCache;
    private readonly LruCache<ExeVersionInfo> _versionCache;
    private readonly LruCache<InstallRootResult> _rootCache;
    private readonly DisplayNameRegistry _displayNames = new();
    private readonly ConcurrentDictionary<string, ProductAlias> _rootAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _windows;
    private readonly string[] _systemBranches;

    private volatile Snapshot _snapshot;

    /// <summary>用真实文件系统构造（生产路径，契约要求的构造签名）。</summary>
    /// <param name="settings">设置快照，之后可用 <see cref="OnSettingsChanged"/> 热更新。</param>
    public AppClassifier(AppSettings settings)
        : this(settings, new BclFileSystemProbe())
    {
    }

    /// <summary>用自定义探测实现构造（单测用假文件系统，可完全脱离本机真实路径）。</summary>
    /// <param name="settings">设置快照。</param>
    /// <param name="probe">文件系统 / 已知目录探测。</param>
    public AppClassifier(AppSettings settings, IFileSystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);

        _probe = probe;
        _installRootResolver = new InstallRootResolver(probe);
        _windows = PathText.Normalize(probe.WindowsDirectory);
        _systemBranches = BuildSystemBranches(_windows);
        _identityCache = new LruCache<AppIdentity>(CacheCapacity, CacheTtl);
        _versionCache = new LruCache<ExeVersionInfo>(CacheCapacity, CacheTtl);
        _rootCache = new LruCache<InstallRootResult>(CacheCapacity, CacheTtl);
        _snapshot = new Snapshot(settings);
    }

    /// <summary>当前归类缓存条目数（诊断用）。</summary>
    public int CachedIdentityCount => _identityCache.Count;

    /// <inheritdoc />
    public AppIdentity Classify(ProcessRawSample process, ProcessRawSample? parent)
    {
        var snapshot = _snapshot;
        var rawPath = process.ExecutablePath;
        var imageName = process.ImageName ?? string.Empty;
        var hasPath = !string.IsNullOrWhiteSpace(rawPath);
        var cacheKey = hasPath ? rawPath! : ImageCacheKeyPrefix + imageName;
        var nowMs = Environment.TickCount64;

        AppIdentity identity;
        if (_identityCache.TryGet(cacheKey, nowMs, out var cached) && cached is not null)
        {
            identity = cached;
        }
        else
        {
            try
            {
                identity = BuildIdentity(process, rawPath, snapshot, nowMs);
            }
            catch
            {
                // 归并引擎跑在采样后台线程上：任何意外都只能降级，绝不能让采样循环挂掉。
                identity = new AppIdentity("unknown", snapshot.UnknownGroupName, null, AppKind.Unknown, "归类过程异常，已回退到兜底分组");
            }

            _identityCache.Set(cacheKey, identity, nowMs);
        }

        var resolved = ApplyParentInheritance(identity, parent, snapshot);
        return ApplyDisplayName(resolved, process);
    }

    /// <inheritdoc />
    public void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _snapshot = new Snapshot(settings);
        _identityCache.Clear();
        _rootAliases.Clear();
        _displayNames.Clear();
    }

    /// <inheritdoc />
    public void ResetCache()
    {
        _identityCache.Clear();
        _versionCache.Clear();
        _rootCache.Clear();
        _rootAliases.Clear();
        _displayNames.Clear();
    }

    private AppIdentity BuildIdentity(in ProcessRawSample process, string? rawPath, Snapshot snapshot, long nowMs)
    {
        var imageName = process.ImageName?.Trim() ?? string.Empty;
        var imageLower = imageName.ToLowerInvariant();
        var exeBase = PathText.StripExeExtension(imageLower);
        var path = PathText.Normalize(rawPath, _windows);

        // ① 用户自定义规则
        var rules = EvaluateRules(imageLower, exeBase, path, snapshot, nowMs);
        if (rules.Excluded is { } excluded)
        {
            return excluded;
        }

        if (rules.Merged is { } merged)
        {
            return merged;
        }

        // ①.5 系统功能模块（WSL / 虚拟机 / 图形 / 安全 / 更新 / 内核 …）
        if (snapshot.MergeWindowsSystemProcesses && SystemModuleCatalog.TryMatch(exeBase, path, out var moduleIdentity))
        {
            return WithRename(moduleIdentity, rules.Rename);
        }

        // ② Windows 系统进程聚合
        if (snapshot.MergeWindowsSystemProcesses && IsSystemProcess(exeBase, path))
        {
            var systemRoot = _windows.Length > 0 ? _windows : null;
            return WithRename(
                new AppIdentity("sys:windows", snapshot.SystemGroupName, systemRoot, AppKind.SystemProcess, "Windows 系统进程（系统目录或系统组件映像名）"),
                rules.Rename);
        }

        // ②.5 内置产品名映射（explorer.exe 在 C:\Windows 下不算系统进程）
        if (exeBase.Length > 0 && WellKnownApps.TryGetValue(exeBase, out var wellKnown))
        {
            var wellKnownRoot = path.Length > 0 ? PathText.DirectoryOf(path) : null;
            return WithRename(
                new AppIdentity(wellKnown.Key, wellKnown.DisplayName, wellKnownRoot, AppKind.CustomRule, wellKnown.Reason),
                rules.Rename);
        }

        // ③ 版本信息（ProductName + CompanyName）
        if (snapshot.GroupByProductName)
        {
            var version = GetVersion(path, snapshot, nowMs);
            if (TryProductIdentity(version, imageLower, exeBase, ChannelDetector.Detect(path)) is { } productIdentity)
            {
                var productRoot = GetInstallRoot(path, nowMs);
                if (productRoot is { Kind: AppKind.InstallRoot } realRoot)
                {
                    productIdentity = productIdentity with { RootPath = realRoot.RootPath };
                    RecordProductAlias(realRoot, productIdentity, imageName);
                }

                return WithRename(productIdentity, rules.Rename);
            }
        }

        // ④ 安装根目录 / ⑤ exe 所在目录
        var installRoot = snapshot.GroupByInstallRoot ? GetInstallRoot(path, nowMs) : null;
        if (installRoot is { Kind: AppKind.InstallRoot } resolvedRoot)
        {
            var rootKey = PathText.ToKeyPath(resolvedRoot.RootPath);
            if (rootKey.Length > 0 && _rootAliases.TryGetValue(rootKey, out var alias))
            {
                return WithRename(
                    new AppIdentity(
                        alias.Key,
                        alias.DisplayName,
                        resolvedRoot.RootPath,
                        AppKind.InstallRoot,
                        $"安装根目录已归属「{alias.DisplayName}」（同类进程 {alias.SourceImage} 由版本信息判定）"),
                    rules.Rename);
            }

            var displayName = ResolveIdentityDisplayName(
                resolvedRoot.RootPath,
                resolvedRoot.DisplayNameHint,
                exeBase,
                imageLower,
                path,
                snapshot,
                nowMs);

            return WithRename(
                new AppIdentity("dir:" + rootKey, displayName, resolvedRoot.RootPath, AppKind.InstallRoot, $"安装根目录：{resolvedRoot.RootPath}"),
                rules.Rename);
        }

        var fallbackDirectory = installRoot?.RootPath is { Length: > 0 } resolvedRootPath
            ? resolvedRootPath
            : path.Length > 0 ? PathText.DirectoryOf(path) : string.Empty;

        if (fallbackDirectory.Length > 0)
        {
            var displayName = ResolveIdentityDisplayName(fallbackDirectory, null, exeBase, imageLower, path, snapshot, nowMs);
            return WithRename(
                new AppIdentity(
                    "dir:" + PathText.ToKeyPath(fallbackDirectory),
                    displayName,
                    fallbackDirectory,
                    AppKind.ExeDirectory,
                    $"可执行文件所在目录：{fallbackDirectory}"),
                rules.Rename);
        }

        // ⑥ 仅能按 exe 文件名归并
        if (exeBase.Length > 0)
        {
            var displayName = AppNameSanitizer.CleanName(exeBase) ?? exeBase;
            var exeKey = imageLower.Length > 0 ? imageLower : exeBase + ".exe";
            return WithRename(
                new AppIdentity("exe:" + exeKey, displayName, null, AppKind.ExeName, "仅能按映像名归并（未取得可执行文件路径）"),
                rules.Rename);
        }

        // ⑦ 兜底分组
        return WithRename(
            new AppIdentity("unknown", snapshot.UnknownGroupName, null, AppKind.Unknown, "无法识别可执行文件信息，已归入兜底分组"),
            rules.Rename);
    }

    private AppIdentity ApplyParentInheritance(AppIdentity identity, ProcessRawSample? parent, Snapshot snapshot)
    {
        if (!snapshot.InheritParentGroup || identity.Excluded || parent is null)
        {
            return identity;
        }

        if (!IsWeakKind(identity.Kind))
        {
            return identity;
        }

        var parentSample = parent.Value;
        AppIdentity parentIdentity;
        try
        {
            parentIdentity = Classify(parentSample, null);
        }
        catch
        {
            return identity;
        }

        if (parentIdentity.Excluded || !IsStrongKind(parentIdentity.Kind) || parentIdentity.Key.Length == 0)
        {
            return identity;
        }

        var parentName = string.IsNullOrWhiteSpace(parentSample.ImageName) ? "父进程" : parentSample.ImageName!;
        return new AppIdentity(
            parentIdentity.Key,
            parentIdentity.DisplayName,
            parentIdentity.RootPath ?? identity.RootPath,
            parentIdentity.Kind,
            $"继承自父进程 {parentName}");
    }

    /// <summary>展示名去重：同名不同 Key 的应用追加确定性后缀，保证同一帧内 DisplayName 唯一。</summary>
    private AppIdentity ApplyDisplayName(AppIdentity identity, in ProcessRawSample process)
    {
        if (identity.Excluded || identity.Key.Length == 0 || string.IsNullOrEmpty(identity.DisplayName))
        {
            return identity;
        }

        var exeBase = PathText.StripExeExtension(process.ImageName);
        var displayName = _displayNames.Register(
            identity.Key,
            identity.DisplayName,
            identity.RootPath,
            exeBase,
            Environment.TickCount64);

        return string.Equals(displayName, identity.DisplayName, StringComparison.Ordinal)
            ? identity
            : identity with { DisplayName = displayName };
    }

    private RuleOutcome EvaluateRules(string imageLower, string exeBase, string path, Snapshot snapshot, long nowMs)
    {
        AppIdentity? excluded = null;
        AppIdentity? merged = null;
        string? rename = null;

        foreach (var rule in snapshot.Rules)
        {
            if (!MatchesRule(in rule, imageLower, exeBase, path, snapshot, nowMs))
            {
                continue;
            }

            switch (rule.Action)
            {
                case AppRuleAction.Exclude:
                    excluded ??= new AppIdentity(
                        "rule:exclude:" + rule.PatternLower,
                        AppNameSanitizer.CleanName(rule.Pattern) ?? (imageLower.Length > 0 ? imageLower : "已排除进程"),
                        null,
                        AppKind.CustomRule,
                        $"用户规则排除：{DescribeMatch(rule.Match)}「{rule.Pattern}」",
                        true);
                    break;

                case AppRuleAction.MergeToName:
                    if (rule.Target is { Length: > 0 } target)
                    {
                        merged ??= new AppIdentity(
                            "rule:merge:" + target.ToLowerInvariant(),
                            target,
                            null,
                            AppKind.CustomRule,
                            $"用户规则合并：{DescribeMatch(rule.Match)}「{rule.Pattern}」→「{target}」");
                    }

                    break;

                case AppRuleAction.Rename:
                    rename ??= rule.Target;
                    break;
            }
        }

        return new RuleOutcome(excluded, merged, rename);
    }

    private bool MatchesRule(in RuleSnapshot rule, string imageLower, string exeBase, string path, Snapshot snapshot, long nowMs)
    {
        switch (rule.Match)
        {
            case AppRuleMatch.ExeName:
                return string.Equals(rule.PatternLower, imageLower, StringComparison.Ordinal)
                    || string.Equals(rule.PatternLower, exeBase, StringComparison.Ordinal)
                    || string.Equals(PathText.StripExeExtension(rule.PatternLower), exeBase, StringComparison.Ordinal);

            case AppRuleMatch.PathContains:
                return path.Length > 0 && path.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);

            case AppRuleMatch.ProductName:
            {
                var version = GetVersion(path, snapshot, nowMs);
                var rawProduct = version.ProductName;
                if (!string.IsNullOrWhiteSpace(rawProduct)
                    && rawProduct!.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var cleanedProduct = AppNameSanitizer.CleanName(rawProduct);
                return cleanedProduct is not null
                    && cleanedProduct.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            default:
                return false;
        }
    }

    private static string DescribeMatch(AppRuleMatch match) => match switch
    {
        AppRuleMatch.ExeName => "映像名",
        AppRuleMatch.PathContains => "路径包含",
        AppRuleMatch.ProductName => "产品名",
        _ => "条件",
    };

    private ExeVersionInfo GetVersion(string path, Snapshot snapshot, long nowMs)
    {
        if (path.Length == 0)
        {
            return ExeVersionInfo.Empty;
        }

        if (snapshot.UseVersionInfoCache && _versionCache.TryGet(path, nowMs, out var cached) && cached is not null)
        {
            return cached;
        }

        var info = _probe.ReadVersionInfo(path) ?? ExeVersionInfo.Empty;
        if (snapshot.UseVersionInfoCache)
        {
            _versionCache.Set(path, info, nowMs);
        }

        return info;
    }

    private InstallRootResult? GetInstallRoot(string path, long nowMs)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var directory = PathText.DirectoryOf(path);
        if (directory.Length == 0)
        {
            return null;
        }

        if (_rootCache.TryGet(directory, nowMs, out var cached) && cached is not null)
        {
            return cached;
        }

        var resolved = _installRootResolver.Resolve(path) ?? new InstallRootResult(directory, AppKind.ExeDirectory, null);
        _rootCache.Set(directory, resolved, nowMs);
        return resolved;
    }

    /// <summary>
    /// 某个 exe 用版本信息判定了身份后，把它所在安装根记下来，
    /// 让同根下拿不到版本信息的 helper 进程（Electron 的 renderer/GPU/utility、Chrome 的 naCl 进程等）也能并进同一行。
    /// </summary>
    private void RecordProductAlias(InstallRootResult root, AppIdentity productIdentity, string imageName)
    {
        if (root.RootPath.Length == 0)
        {
            return;
        }

        var rootKey = PathText.ToKeyPath(root.RootPath);
        if (rootKey.Length == 0 || _rootAliases.ContainsKey(rootKey))
        {
            return;
        }

        if (_rootAliases.TryAdd(rootKey, new ProductAlias(productIdentity.Key, productIdentity.DisplayName, imageName)))
        {
            // 之前可能已经有同根的 helper 被归到了目录级 Key，清掉这些陈旧结果，下一帧就会并进产品级 Key。
            var staleKey = "dir:" + rootKey;
            _identityCache.RemoveWhere(identity => string.Equals(identity.Key, staleKey, StringComparison.Ordinal));
        }
    }

    private bool IsSystemProcess(string exeBase, string path)
    {
        if (exeBase.Length > 0 && SystemImageBaseNames.Contains(exeBase))
        {
            return true;
        }

        if (path.Length == 0 || _systemBranches.Length == 0)
        {
            return false;
        }

        foreach (var branch in _systemBranches)
        {
            if (PathText.IsUnder(path, branch))
            {
                return true;
            }
        }

        return false;
    }

    private static AppIdentity? TryProductIdentity(ExeVersionInfo version, string imageLower, string exeBase, string? channel)
    {
        var product = AppNameSanitizer.CleanName(version.ProductName);
        if (AppNameSanitizer.IsMeaninglessProductName(product, version.ProductName, version, imageLower, exeBase))
        {
            return null;
        }

        var company = AppNameSanitizer.CleanName(version.CompanyName);
        var keyBody = company is null ? product! : company + "|" + product;
        var displayName = product!;
        var reason = company is null
            ? $"版本信息 ProductName「{product}」"
            : $"版本信息 ProductName「{product}」+ CompanyName「{company}」";

        // 同一产品的不同发行通道（Beta / Dev / Canary…）产品名完全一样，
        // 必须把通道并进 Key，否则会把稳定版和测试版算成一个应用。
        if (!string.IsNullOrEmpty(channel))
        {
            keyBody += "|" + channel;
            displayName = product + " " + channel;
            reason += $"（{channel} 通道，安装目录含通道标记）";
        }

        return new AppIdentity("product:" + keyBody.ToLowerInvariant(), displayName, null, AppKind.ProductName, reason);
    }

    /// <summary>
    /// 解析应用展示名。顺序：
    /// ① 安装根提示（UWP 包族名、Steam 游戏名）；
    /// ② 目录末段（可用时优先，因为它与 Key 一一对应、跨帧最稳定）；
    /// ③ 版本信息 FileDescription；
    /// ④ 版本信息 ProductName；
    /// ⑤ 向上找第一个「usable」的祖先目录段（把 bin\Release\net10.0-windows 还原成工程名）；
    /// ⑥ exe 基名；
    /// ⑦ 兜底组名。
    /// </summary>
    private string ResolveIdentityDisplayName(
        string directory,
        string? hint,
        string exeBase,
        string imageLower,
        string path,
        Snapshot snapshot,
        long nowMs)
    {
        var hintName = AppNameSanitizer.CleanName(hint);
        if (hintName is not null)
        {
            return hintName;
        }

        var segment = AppNameSanitizer.CleanSegment(PathText.LastSegment(directory));
        if (segment is not null && !AppNameSanitizer.IsUnusableDirectoryName(segment))
        {
            return segment;
        }

        var version = GetVersion(path, snapshot, nowMs);

        var description = AppNameSanitizer.CleanName(version.FileDescription);
        if (description is not null
            && !AppNameSanitizer.IsUnusableDirectoryName(description)
            && !AppNameSanitizer.IsMeaninglessProductName(description, version.FileDescription, version, imageLower, exeBase))
        {
            return description;
        }

        var product = AppNameSanitizer.CleanName(version.ProductName);
        if (product is not null
            && !AppNameSanitizer.IsUnusableDirectoryName(product)
            && !AppNameSanitizer.IsMeaninglessProductName(product, version.ProductName, version, imageLower, exeBase))
        {
            return product;
        }

        var ancestor = AppNameSanitizer.FindUsableAncestorSegment(PathText.Parent(directory));
        if (ancestor is not null)
        {
            return ancestor;
        }

        var imageName = AppNameSanitizer.CleanName(exeBase);
        if (imageName is not null)
        {
            return imageName;
        }

        return segment ?? snapshot.UnknownGroupName;
    }

    private static AppIdentity WithRename(AppIdentity identity, string? rename)
    {
        if (string.IsNullOrEmpty(rename) || identity.Excluded)
        {
            return identity;
        }

        return identity with
        {
            DisplayName = rename!,
            Reason = identity.Reason + $"；显示名由用户规则指定为「{rename}」",
        };
    }

    private static bool IsWeakKind(AppKind kind)
        => kind is AppKind.ExeDirectory or AppKind.ExeName or AppKind.Unknown;

    private static bool IsStrongKind(AppKind kind)
        => kind is AppKind.ProductName or AppKind.InstallRoot or AppKind.CustomRule;

    private static string[] BuildSystemBranches(string windowsDirectory)
    {
        if (windowsDirectory.Length == 0)
        {
            return Array.Empty<string>();
        }

        return new[]
        {
            PathText.Combine(windowsDirectory, "System32"),
            PathText.Combine(windowsDirectory, "SysWOW64"),
            PathText.Combine(windowsDirectory, "WinSxS"),
            PathText.Combine(windowsDirectory, "servicing"),
        };
    }

    private sealed record ProductAlias(string Key, string DisplayName, string SourceImage);

    private sealed record WellKnownApp(string Key, string DisplayName, string Reason);

    private readonly record struct RuleSnapshot(
        AppRuleMatch Match,
        AppRuleAction Action,
        string Pattern,
        string PatternLower,
        string? Target);

    private readonly record struct RuleOutcome(AppIdentity? Excluded, AppIdentity? Merged, string? Rename);

    /// <summary>设置快照：只在 <see cref="OnSettingsChanged"/> 时重建，归类热路径只读它。</summary>
    private sealed class Snapshot
    {
        public Snapshot(AppSettings settings)
        {
            GroupByProductName = settings.GroupByProductName;
            GroupByInstallRoot = settings.GroupByInstallRoot;
            InheritParentGroup = settings.InheritParentGroup;
            MergeWindowsSystemProcesses = settings.MergeWindowsSystemProcesses;
            UseVersionInfoCache = settings.UseVersionInfoCache;
            UnknownGroupName = string.IsNullOrWhiteSpace(settings.UnknownGroupName) ? "其他" : settings.UnknownGroupName.Trim();
            SystemGroupName = string.IsNullOrWhiteSpace(settings.SystemGroupName) ? "Windows 系统" : settings.SystemGroupName.Trim();

            var rules = new List<RuleSnapshot>(settings.Rules?.Count ?? 0);
            if (settings.Rules is { } configuredRules)
            {
                foreach (var rule in configuredRules)
                {
                    if (rule is null || !rule.Enabled)
                    {
                        continue;
                    }

                    var pattern = (rule.Pattern ?? string.Empty).Trim();
                    if (pattern.Length == 0)
                    {
                        continue;
                    }

                    var target = string.IsNullOrWhiteSpace(rule.Target) ? null : rule.Target!.Trim();
                    rules.Add(new RuleSnapshot(rule.Match, rule.Action, pattern, pattern.ToLowerInvariant(), target));
                }
            }

            Rules = rules.ToArray();
        }

        public bool GroupByProductName { get; }

        public bool GroupByInstallRoot { get; }

        public bool InheritParentGroup { get; }

        public bool MergeWindowsSystemProcesses { get; }

        public bool UseVersionInfoCache { get; }

        public string UnknownGroupName { get; }

        public string SystemGroupName { get; }

        public RuleSnapshot[] Rules { get; }
    }
}