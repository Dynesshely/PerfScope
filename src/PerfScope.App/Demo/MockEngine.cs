using System.Diagnostics;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Demo;

/// <summary>
/// 截图 / 演示用的 mock 数据源：完全不读取本机进程与磁盘，只合成一套虚构的应用数据。
/// 用途：生成文档截图、演示界面、回归视觉，保证对外材料里不出现任何真实环境信息。
/// 数据是确定性的（固定随机种子），同一套参数每次截图画面一致。
/// </summary>
internal sealed class MockEngine : IMonitorEngine
{
    private const long TotalPhysicalBytes = 32L * 1024L * 1024L * 1024L;
    private const int VisibleIntervalMs = 1000;
    private const long Megabyte = 1024L * 1024L;

    private readonly object _sync = new();
    private readonly Random _random = new(20261101);
    private readonly List<MockApp> _apps = BuildCatalog();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _startTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private AppSettings _settings;
    private System.Threading.Timer? _timer;
    private SnapshotFrame? _latest;
    private int _intervalMs = VisibleIntervalMs;
    private bool _running;
    private bool _uiVisible = true;
    private bool _disposed;
    private long _lastTickTicks;
    private double _systemCpu;

    /// <summary>创建 mock 引擎。</summary>
    public MockEngine(AppSettings settings)
    {
        _settings = settings.Clone();
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
            _timer = new System.Threading.Timer(OnTick, null, 150, Timeout.Infinite);
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

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private int ComputeInterval() => _uiVisible ? VisibleIntervalMs : Math.Max(VisibleIntervalMs, _settings.HiddenSampleIntervalMs);

    private void OnTick(object? state)
    {
        try
        {
            Tick();
        }
        catch
        {
            // mock 引擎不应该把异常抛到线程池
        }

        lock (_sync)
        {
            if (!_running || _disposed || _timer is null)
            {
                return;
            }

            long now = _clock.ElapsedTicks;
            double seconds = (now - _lastTickTicks) / (double)Stopwatch.Frequency;
            _lastTickTicks = now;
            int delay = Math.Max(80, (int)(_intervalMs - seconds * 1000));
            _timer.Change(delay, Timeout.Infinite);
        }
    }

    private void Tick()
    {
        long nowMs = _startTimestamp + (long)(_clock.Elapsed.TotalMilliseconds);
        var apps = new List<AppSample>(_apps.Count);
        long totalMemory = 0;
        double totalCpu = 0;

        lock (_sync)
        {
            foreach (var app in _apps)
            {
                app.Step(_random);

                long workingSet = (long)(app.MemoryMb * Megabyte);
                totalMemory += workingSet;
                totalCpu += app.Cpu;

                var processes = new AppProcessInfo[app.ProcessNames.Length];
                long perProcess = Math.Max(Megabyte, workingSet / Math.Max(1, app.ProcessNames.Length));
                for (int i = 0; i < processes.Length; i++)
                {
                    processes[i] = new AppProcessInfo(
                        1000 + i,
                        app.ProcessNames[i] + ".exe",
                        null,
                        Math.Round(app.Cpu / processes.Length, 2),
                        perProcess,
                        perProcess);
                }

                apps.Add(new AppSample(
                    app.Key,
                    app.DisplayName,
                    app.RootPath,
                    app.Kind,
                    app.IconPath,
                    Math.Round(app.Cpu, 2),
                    workingSet,
                    workingSet,
                    processes.Length,
                    app.ReadBytesPerSecond,
                    app.WriteBytesPerSecond,
                    app.Reason,
                    processes));
            }

            // 系统口径：32 GB 物理内存，留出内核/缓存等不可归属部分
            long used = Math.Min(TotalPhysicalBytes, totalMemory + 5L * 1024 * Megabyte);
            long available = TotalPhysicalBytes - used;
            _systemCpu = Math.Clamp(totalCpu * 0.55 + 6 + _random.NextDouble() * 3, 0, 100);

            apps.Sort((left, right) => right.CpuPercent.CompareTo(left.CpuPercent));

            _latest = new SnapshotFrame(
                DateTimeOffset.FromUnixTimeMilliseconds(nowMs).LocalDateTime,
                Math.Round(_systemCpu, 1),
                Math.Round(used * 100.0 / TotalPhysicalBytes, 1),
                TotalPhysicalBytes,
                Math.Max(0, available),
                TimeSpan.FromMilliseconds(_intervalMs),
                0.4,
                86L * Megabyte,
                14,
                apps.ToArray());
        }

        FrameProduced?.Invoke(this, _latest!);
    }

    private static List<MockApp> BuildCatalog() => new()
    {
        MockApp.Create("Aurora Browser", "aurora", AppKind.InstallRoot, @"C:\Program Files\Aurora Browser",
            "版本信息归并（ProductName=Aurora Browser）", 12, 2.4, 3100, 6.1, 0.7, 9,
            "aurora", "aurora-renderer", "aurora-gpu", "aurora-network"),
        MockApp.Create("Nova Editor", "nova", AppKind.ProductName, @"C:\Program Files\Nova Editor",
            "版本信息归并（ProductName=Nova Editor）", 7, 1.6, 1500, 2.4, 0.6, 6,
            "nova", "nova-helper", "nova-lang"),
        MockApp.Create("Pixel Chat", "pixelchat", AppKind.InstallRoot, @"C:\Program Files\Pixel Chat",
            @"安装根目录：C:\Program Files\Pixel Chat", 4, 1.1, 980, 0.9, 0.2, 5,
            "pixelchat", "pixelchat-helper"),
        MockApp.Create("Vault Sync", "vaultsync", AppKind.InstallRoot, @"C:\Program Files\Vault Sync",
            @"安装根目录：C:\Program Files\Vault Sync", 3, 0.8, 760, 1.7, 2.9, 4,
            "vaultsync", "vaultsync-idx"),
        MockApp.Create("Studio Suite", "studio", AppKind.ProductName, @"C:\Program Files\Studio Suite",
            "版本信息归并（ProductName=Studio Suite）", 9, 2.9, 2400, 4.4, 1.2, 7,
            "studio", "studio-helper", "studio-media"),
        MockApp.Create("Echo Player", "echo", AppKind.InstallRoot, @"C:\Program Files\Echo Player",
            @"安装根目录：C:\Program Files\Echo Player", 2, 0.7, 620, 0.5, 0.1, 3,
            "echo"),
        MockApp.Create("Helio Mail", "heliomail", AppKind.ProductName, @"C:\Program Files\Helio Mail",
            "版本信息归并（ProductName=Helio Mail）", 1.6, 0.5, 540, 0.3, 0.1, 3,
            "heliomail", "heliomail-sync"),
        MockApp.Create("Terra Game", "terra", AppKind.InstallRoot, @"C:\Games\Terra",
            @"安装根目录：C:\Games\Terra", 14, 4.2, 5200, 8.5, 2.1, 4,
            "terra", "terra-launcher"),
        MockApp.Create("Lumen Terminal", "lumen", AppKind.ProductName, @"C:\Program Files\Lumen Terminal",
            "版本信息归并（ProductName=Lumen Terminal）", 2.4, 0.9, 380, 0.2, 0.1, 3,
            "lumen"),
        MockApp.Create("Orbit Meeting", "orbit", AppKind.InstallRoot, @"C:\Program Files\Orbit Meeting",
            @"安装根目录：C:\Program Files\Orbit Meeting", 3.2, 1.2, 870, 0.8, 0.3, 5,
            "orbit", "orbit-helper"),
        MockApp.Create("Beacon VPN", "beacon", AppKind.InstallRoot, @"C:\Program Files\Beacon VPN",
            @"安装根目录：C:\Program Files\Beacon VPN", 0.8, 0.3, 210, 0.2, 0.1, 2,
            "beacon"),
        MockApp.Create("Quill Notes", "quill", AppKind.ProductName, @"C:\Program Files\Quill Notes",
            "版本信息归并（ProductName=Quill Notes）", 0.6, 0.2, 180, 0.1, 0.1, 2,
            "quill"),
        MockApp.Create("WSL 子系统", "wsl", AppKind.SystemModule, @"C:\Windows\System32\lxss",
            "系统功能模块：WSL 子系统", 6, 2.0, 2400, 0.6, 0.4, 6,
            "vmmemWSL", "wslhost", "wslservice"),
        MockApp.Create("图形界面与显卡驱动", "graphics", AppKind.SystemModule, @"C:\Windows\System32",
            "系统功能模块：图形界面与显卡驱动", 2.4, 0.9, 620, 0.1, 0.1, 4,
            "dwm", "nvcontainer", "nvsphelper64"),
        MockApp.Create("内核与系统核心", "kernel", AppKind.SystemModule, @"C:\Windows\System32",
            "系统功能模块：内核与系统核心", 1.8, 0.7, 1800, 0.2, 0.1, 5,
            "System", "Registry", "Memory Compression", "csrss", "wininit"),
        MockApp.Create("安全与防护", "security", AppKind.SystemModule, @"C:\Windows\System32",
            "系统功能模块：安全与防护", 0.9, 0.4, 480, 0, 0, 3,
            "MsMpEng", "SecurityHealthService", "lsass"),
    };

    /// <summary>单个 mock 应用的随机游走状态。</summary>
    private sealed class MockApp
    {
        private double _phase;

        private MockApp()
        {
        }

        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required AppKind Kind { get; init; }
        public required string RootPath { get; init; }
        public required string Reason { get; init; }
        public required double CpuCeiling { get; init; }
        public required double CpuVolatility { get; init; }
        public required double MemoryBaseMb { get; init; }
        public required double ReadMbPerSecond { get; init; }
        public required double WriteMbPerSecond { get; init; }
        public required string[] ProcessNames { get; init; }

        public string IconPath => Path.Combine(RootPath, Key[(Key.LastIndexOf(':') + 1)..] + ".exe");

        public double Cpu { get; private set; }
        public double MemoryMb { get; private set; }
        public double ReadBytesPerSecond { get; private set; }
        public double WriteBytesPerSecond { get; private set; }

        public static MockApp Create(
            string displayName,
            string keySlug,
            AppKind kind,
            string rootPath,
            string reason,
            double cpuCeiling,
            double volatility,
            double memoryBaseMb,
            double readMbPerSecond,
            double writeMbPerSecond,
            int processCount,
            params string[] processNames)
        {
            var names = new string[Math.Max(processCount, processNames.Length)];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = processNames.Length > 0 ? processNames[i % processNames.Length] : keySlug;
            }

            return new MockApp
            {
                Key = "mock:" + keySlug,
                DisplayName = displayName,
                Kind = kind,
                RootPath = rootPath,
                Reason = reason,
                CpuCeiling = cpuCeiling,
                CpuVolatility = volatility,
                MemoryBaseMb = memoryBaseMb * 0.62,
                ReadMbPerSecond = readMbPerSecond,
                WriteMbPerSecond = writeMbPerSecond,
                ProcessNames = names,
                Cpu = cpuCeiling * 0.35,
                MemoryMb = memoryBaseMb * 0.62,
            };
        }

        public void Step(Random random)
        {
            _phase += 0.18;
            double wave = Math.Sin(_phase) * 0.5 + 0.5;
            double target = CpuCeiling * (0.25 + 0.75 * wave) + random.NextDouble() * CpuVolatility;
            Cpu = Math.Clamp(Cpu * 0.6 + target * 0.4, 0, CpuCeiling * 1.15);

            double memoryTarget = MemoryBaseMb * (1 + Math.Sin(_phase * 0.37) * 0.05);
            MemoryMb = MemoryMb * 0.85 + memoryTarget * 0.15;

            ReadBytesPerSecond = ReadMbPerSecond * Megabyte * (0.3 + random.NextDouble() * 0.9);
            WriteBytesPerSecond = WriteMbPerSecond * Megabyte * (0.2 + random.NextDouble() * 0.8);
        }
    }
}