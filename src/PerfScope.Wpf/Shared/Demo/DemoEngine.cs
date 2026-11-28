using System.Diagnostics;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Demo;

/// <summary>
/// 演示用引擎：不依赖任何 Core 实现，合成 12 个应用 + 随机游走，可见时 1 秒一帧。
/// 用途是让界面在没有采样实现（或集成未完成）时也能完整自测。
/// </summary>
internal sealed class DemoEngine : IMonitorEngine
{
    private const long TotalPhysicalBytes = 32L * 1024 * 1024 * 1024;
    private const int VisibleIntervalMs = 1000;
    private const long Megabyte = 1024L * 1024L;

    private readonly object _sync = new();
    private readonly Random _random = new(20260831);
    private readonly List<DemoApp> _apps;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private AppSettings _settings;
    private System.Threading.Timer? _timer;
    private SnapshotFrame? _latest;
    private int _intervalMs = VisibleIntervalMs;
    private bool _running;
    private bool _uiVisible = true;
    private bool _disposed;
    private long _lastTickTicks;
    private double _memoryPercent = 63;

    /// <summary>创建演示引擎。</summary>
    public DemoEngine(AppSettings settings)
    {
        _settings = settings.Clone();
        _apps = BuildDemoApps();
    }

    /// <inheritdoc />
    public SnapshotFrame? Latest => Volatile.Read(ref _latest);

    /// <inheritdoc />
    public event EventHandler<SnapshotFrame>? FrameProduced;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    /// <inheritdoc />
    public int EffectiveIntervalMs => Volatile.Read(ref _intervalMs);

    /// <inheritdoc />
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _running)
            {
                return;
            }

            _running = true;
            _lastTickTicks = _clock.ElapsedTicks;
            _intervalMs = ComputeInterval();
            _timer = new System.Threading.Timer(OnTick, null, 120, Timeout.Infinite);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <inheritdoc />
    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            _settings = settings.Clone();
            _intervalMs = ComputeInterval();
        }
    }

    /// <inheritdoc />
    public bool ForceSample()
    {
        Tick();
        return true;
    }

    /// <inheritdoc />
    public void SetUiVisible(bool visible)
    {
        lock (_sync)
        {
            bool changed = _uiVisible != visible;
            _uiVisible = visible;
            _intervalMs = ComputeInterval();
            if (changed && _running && _timer is not null)
            {
                _timer.Change(0, Timeout.Infinite);
            }
        }
    }

    /// <summary>释放定时器。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private int ComputeInterval()
    {
        bool throttled = !_uiVisible && _settings.AdaptiveThrottleWhenHidden;
        return throttled ? Math.Max(VisibleIntervalMs, _settings.HiddenSampleIntervalMs) : VisibleIntervalMs;
    }

    private void OnTick(object? state)
    {
        try
        {
            Tick();
        }
        catch
        {
            // 演示引擎吞掉异常，保证定时器循环与界面都不被打断
        }
        finally
        {
            lock (_sync)
            {
                if (_running && _timer is not null)
                {
                    _timer.Change(Math.Max(200, _intervalMs), Timeout.Infinite);
                }
            }
        }
    }

    private void Tick()
    {
        SnapshotFrame frame = ComposeFrame();
        Volatile.Write(ref _latest, frame);
        FrameProduced?.Invoke(this, frame);
    }

    private SnapshotFrame ComposeFrame()
    {
        long nowTicks = _clock.ElapsedTicks;
        bool first = _lastTickTicks == 0;
        double seconds = first
            ? 0
            : Math.Max(0.05, (nowTicks - _lastTickTicks) / (double)Stopwatch.Frequency);
        _lastTickTicks = nowTicks;

        double totalCpu = 0;
        int totalProcesses = 0;
        foreach (var app in _apps)
        {
            StepApp(app);
            totalCpu += app.Cpu;
            totalProcesses += app.Processes.Length;
        }

        _memoryPercent = Math.Clamp(_memoryPercent + (_random.NextDouble() - 0.5) * 0.6, 45, 84);

        var samples = new AppSample[_apps.Count];
        for (int i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            var processes = new AppProcessInfo[app.Processes.Length];
            for (int p = 0; p < processes.Length; p++)
            {
                var process = app.Processes[p];
                processes[p] = new AppProcessInfo(
                    process.Pid,
                    process.Name,
                    process.ExecutablePath,
                    Math.Round(app.Cpu * process.CpuShare, 2),
                    (long)(app.WorkingSetBytes * process.MemoryShare),
                    (long)(app.PrivateBytes * process.MemoryShare));
            }

            samples[i] = new AppSample(
                app.Key,
                app.DisplayName,
                app.RootPath,
                app.Kind,
                app.Processes.Length > 0 ? app.Processes[0].ExecutablePath : app.RootPath,
                Math.Round(app.Cpu, 2),
                app.WorkingSetBytes,
                app.PrivateBytes,
                app.Processes.Length,
                Math.Round(app.ReadBytesPerSecond, 0),
                Math.Round(app.WriteBytesPerSecond, 0),
                app.Reason,
                processes);
        }

        Array.Sort(samples, static (a, b) => b.CpuPercent.CompareTo(a.CpuPercent));

        double systemCpu = Math.Clamp(totalCpu * 0.85 + 2.5 + (_random.NextDouble() - 0.5) * 2, 1, 98);
        long available = (long)(TotalPhysicalBytes * (1 - _memoryPercent / 100d));
        double selfCpu = Math.Round(0.05 + _random.NextDouble() * 0.55, 2);
        long selfWorkingSet = (long)((26 + _random.NextDouble() * 10) * Megabyte);

        return new SnapshotFrame(
            DateTime.UtcNow,
            Math.Round(systemCpu, 2),
            Math.Round(_memoryPercent, 2),
            TotalPhysicalBytes,
            available,
            TimeSpan.FromSeconds(seconds),
            selfCpu,
            selfWorkingSet,
            totalProcesses,
            samples);
    }

    private void StepApp(DemoApp app)
    {
        double drift = (_random.NextDouble() - 0.5) * app.CpuVolatility;
        if (_random.NextDouble() < 0.06)
        {
            drift += _random.NextDouble() * app.CpuVolatility * 3;
        }

        app.Cpu = Math.Clamp(app.Cpu + drift, 0.02, app.CpuCeiling);

        double memoryDrift = 1 + (_random.NextDouble() - 0.5) * 0.02;
        app.WorkingSetBytes = Math.Clamp(
            (long)(app.WorkingSetBytes * memoryDrift),
            AppMinimumBytes,
            app.MemoryCeilingBytes);
        app.PrivateBytes = (long)(app.WorkingSetBytes * 1.18);

        app.ReadBytesPerSecond = Math.Max(0, app.ReadBytesPerSecond * (0.75 + _random.NextDouble() * 0.5));
        app.WriteBytesPerSecond = Math.Max(0, app.WriteBytesPerSecond * (0.75 + _random.NextDouble() * 0.5));
        if (app.ReadBytesPerSecond < 1024)
        {
            app.ReadBytesPerSecond = 0;
        }

        if (app.WriteBytesPerSecond < 1024)
        {
            app.WriteBytesPerSecond = 0;
        }
    }

    private const long AppMinimumBytes = 8L * Megabyte;

    private static List<DemoApp> BuildDemoApps()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string system32 = Environment.SystemDirectory;
        string chromeExe = Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        string edgeExe = Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
        string codeExe = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
        string svchostExe = Path.Combine(system32, "svchost.exe");
        string dockerExe = Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe");
        string dockerBackend = Path.Combine(programFiles, "Docker", "Docker", "resources", "com.docker.backend.exe");
        string dockerVpnkit = Path.Combine(programFiles, "Docker", "Docker", "resources", "vpnkit.exe");
        string wechatExe = Path.Combine(programFilesX86, "Tencent", "WeChat", "WeChat.exe");
        string wechatMini = Path.Combine(programFilesX86, "Tencent", "WeChat", "WeChatAppEx.exe");
        string steamExe = Path.Combine(programFilesX86, "Steam", "steam.exe");
        string steamHelper = Path.Combine(programFilesX86, "Steam", "bin", "cef", "steamwebhelper.exe");
        string spotifyExe = Path.Combine(roaming, "Spotify", "Spotify.exe");
        string teamsExe = Path.Combine(localAppData, "Microsoft", "Teams", "ms-teams.exe");
        string notepadExe = Path.Combine(programFiles, "Notepad++", "notepad++.exe");
        string powershellExe = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        string conhostExe = Path.Combine(system32, "conhost.exe");
        string sevenZipExe = Path.Combine(programFiles, "7-Zip", "7zFM.exe");

        return new List<DemoApp>
        {
            DemoApp.Create("Google Chrome", "Chrome", AppKind.InstallRoot, Path.GetDirectoryName(chromeExe) ?? programFiles,
                "版本信息归并（ProductName=Google Chrome）", 12.5, 2.2, 38, 2600, 5.2, 0.6,
                ("chrome.exe", chromeExe), ("chrome.exe", chromeExe), ("chrome.exe", chromeExe),
                ("chrome.exe", chromeExe), ("chrome.exe", chromeExe)),

            DemoApp.Create("Microsoft Edge", "Edge", AppKind.InstallRoot, Path.GetDirectoryName(edgeExe) ?? programFilesX86,
                "版本信息归并（ProductName=Microsoft Edge）", 3.8, 1.2, 14, 900, 1.4, 0.2,
                ("msedge.exe", edgeExe), ("msedge.exe", edgeExe), ("msedge.exe", edgeExe)),

            DemoApp.Create("Visual Studio Code", "Code", AppKind.ProductName, Path.GetDirectoryName(codeExe) ?? localAppData,
                "版本信息归并（ProductName=Visual Studio Code）", 6.4, 2.0, 22, 1200, 2.0, 0.5,
                ("Code.exe", codeExe), ("Code.exe", codeExe), ("rg.exe", codeExe)),

            DemoApp.Create("Windows 系统", "Windows", AppKind.SystemProcess, system32,
                "系统进程聚合（System32 + 服务宿主）", 4.6, 1.5, 16, 2400, 0.6, 0.3,
                ("svchost.exe", svchostExe), ("svchost.exe", svchostExe), ("svchost.exe", svchostExe),
                ("lsass.exe", Path.Combine(system32, "lsass.exe")), ("dwm.exe", Path.Combine(system32, "dwm.exe")),
                ("conhost.exe", conhostExe)),

            DemoApp.Create("Microsoft Teams", "Teams", AppKind.ProductName, Path.GetDirectoryName(teamsExe) ?? localAppData,
                "版本信息归并（ProductName=Microsoft Teams）", 2.6, 1.2, 16, 800, 0.7, 0.2,
                ("ms-teams.exe", teamsExe), ("ms-teams.exe", teamsExe)),

            DemoApp.Create("Docker Desktop", "Docker", AppKind.InstallRoot, Path.GetDirectoryName(dockerExe) ?? programFiles,
                "安装根目录归并（Program Files\\Docker）", 2.1, 1.0, 18, 1500, 1.1, 0.4,
                ("Docker Desktop.exe", dockerExe), ("com.docker.backend.exe", dockerBackend), ("vpnkit.exe", dockerVpnkit)),

            DemoApp.Create("微信", "WeChat", AppKind.InstallRoot, Path.GetDirectoryName(wechatExe) ?? programFilesX86,
                "安装根目录归并（Program Files (x86)\\Tencent）", 3.2, 1.6, 20, 700, 2.4, 1.6,
                ("WeChat.exe", wechatExe), ("WeChatAppEx.exe", wechatMini), ("WeChatAppEx.exe", wechatMini)),

            DemoApp.Create("Steam", "Steam", AppKind.InstallRoot, Path.GetDirectoryName(steamExe) ?? programFilesX86,
                "安装根目录归并（Program Files (x86)\\Steam）", 5.5, 2.6, 24, 900, 6.5, 0.8,
                ("steam.exe", steamExe), ("steamwebhelper.exe", steamHelper),
                ("steamwebhelper.exe", steamHelper), ("steamwebhelper.exe", steamHelper)),

            DemoApp.Create("Spotify", "Spotify", AppKind.ProductName, Path.GetDirectoryName(spotifyExe) ?? roaming,
                "版本信息归并（ProductName=Spotify）", 1.4, 0.9, 10, 500, 0.9, 0.1,
                ("Spotify.exe", spotifyExe), ("Spotify.exe", spotifyExe)),

            DemoApp.Create("Notepad++", "Notepad", AppKind.ExeDirectory, Path.GetDirectoryName(notepadExe) ?? programFiles,
                "按 exe 所在目录归并", 0.2, 0.3, 4, 120, 0.3, 0.05,
                ("notepad++.exe", notepadExe)),

            DemoApp.Create("Windows PowerShell", "PowerShell", AppKind.ProductName,
                Path.GetDirectoryName(powershellExe) ?? system32,
                "版本信息归并（ProductName=Windows PowerShell）", 0.4, 0.5, 6, 180, 0.5, 0.05,
                ("powershell.exe", powershellExe), ("conhost.exe", conhostExe)),

            DemoApp.Create("7-Zip", "7Zip", AppKind.ExeDirectory, Path.GetDirectoryName(sevenZipExe) ?? programFiles,
                "按 exe 所在目录归并", 0.1, 0.2, 4, 100, 0.2, 0.02,
                ("7zFM.exe", sevenZipExe)),
        };
    }

    /// <summary>演示应用的可变状态（随机游走）。</summary>
    private sealed class DemoApp
    {
        private DemoApp()
        {
        }

        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required AppKind Kind { get; init; }
        public required string RootPath { get; init; }
        public required string Reason { get; init; }
        public required double CpuCeiling { get; init; }
        public required double CpuVolatility { get; init; }
        public required long MemoryCeilingBytes { get; init; }
        public required DemoProcess[] Processes { get; init; }

        public double Cpu { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        public double ReadBytesPerSecond { get; set; }
        public double WriteBytesPerSecond { get; set; }

        public static DemoApp Create(
            string displayName,
            string keySlug,
            AppKind kind,
            string rootPath,
            string reason,
            double initialCpu,
            double volatility,
            double cpuCeiling,
            long workingSetMb,
            double readMbPerSecond,
            double writeMbPerSecond,
            params (string Name, string Path)[] processes)
        {
            var list = new DemoProcess[processes.Length];
            int seed = 0x2F;
            for (int i = 0; i < processes.Length; i++)
            {
                seed = unchecked(seed * 1103515245 + 12345 + i * 7);
                int pid = 1000 + (Math.Abs(seed) % 60000);
                double cpuShare = i == 0 ? 0.58 : 0.42 / Math.Max(1, processes.Length - 1);
                double memoryShare = i == 0 ? 0.62 : 0.38 / Math.Max(1, processes.Length - 1);
                list[i] = new DemoProcess(processes[i].Name, processes[i].Path, pid, cpuShare, memoryShare);
            }

            return new DemoApp
            {
                Key = "demo:" + keySlug,
                DisplayName = displayName,
                Kind = kind,
                RootPath = rootPath,
                Reason = reason,
                CpuCeiling = cpuCeiling,
                CpuVolatility = volatility,
                MemoryCeilingBytes = workingSetMb * 3 / 2 * Megabyte,
                Processes = list,
                Cpu = initialCpu,
                WorkingSetBytes = workingSetMb * Megabyte,
                PrivateBytes = (long)(workingSetMb * 1.18 * Megabyte),
                ReadBytesPerSecond = readMbPerSecond * Megabyte,
                WriteBytesPerSecond = writeMbPerSecond * Megabyte,
            };
        }
    }

    /// <summary>演示进程条目。</summary>
    private sealed class DemoProcess
    {
        public DemoProcess(string name, string path, int pid, double cpuShare, double memoryShare)
        {
            Name = name;
            ExecutablePath = path;
            Pid = pid;
            CpuShare = cpuShare;
            MemoryShare = memoryShare;
        }

        public string Name { get; }

        public string ExecutablePath { get; }

        public int Pid { get; }

        public double CpuShare { get; }

        public double MemoryShare { get; }
    }
}
