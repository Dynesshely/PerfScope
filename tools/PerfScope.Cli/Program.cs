using System.Diagnostics;
using System.Text;
using PerfScope.Core;
using PerfScope.Core.Abstractions;
using PerfScope.Core.History;
using PerfScope.Core.Models;
using PerfScope.Core.Native;
using PerfScope.Core.Settings;

namespace PerfScope.Cli;

/// <summary>
/// 命令行工具：不开界面直接跑一遍真实采样，用来做冒烟验证、性能实测与排障。
/// 用法：
///   PerfScope.Cli                      采 3 帧（默认 2s 间隔）并打印最后两帧的应用排行
///   PerfScope.Cli --frames 5 --interval 1000 --top 15
///   PerfScope.Cli --bench              只测 NativeCapture 的采集耗时
///   PerfScope.Cli --json               以 JSON 输出
///   PerfScope.Cli --data-dir &lt;目录&gt;   使用指定数据目录（不影响正式配置）
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = CliOptions.Parse(args);

        if (options.ShowHelp)
        {
            CliOptions.PrintUsage();
            return 0;
        }

        if (options.Benchmark)
        {
            return RunBenchmark(options);
        }

        if (options.History)
        {
            return RunHistory(options);
        }

        return RunFrames(options);
    }

    private static int RunFrames(CliOptions options)
    {
        AppSettings settings;
        ISettingsStore settingsStore;
        try
        {
            if (options.DataDirectory is not null)
            {
                Directory.CreateDirectory(options.DataDirectory);
                settingsStore = new SettingsStore(NullLog.Instance, Path.Combine(options.DataDirectory, "settings.json"));
            }
            else
            {
                settingsStore = CoreRuntime.CreateSettingsStore(NullLog.Instance);
            }

            settings = settingsStore.Load();
        }
        catch (Exception ex)
        {
            Console.WriteLine("读取设置失败：" + ex.Message);
            return 1;
        }

        settings.SampleIntervalMs = options.IntervalMs;
        settings.UiRefreshIntervalMs = Math.Min(options.IntervalMs, 1000);
        settings.LogLevel = LogLevel.Error;

        ILog log = NullLog.Instance;
        using var engine = CoreRuntime.CreateEngine(settings, log);
        // --data-dir 时把历史也落到指定目录，方便在不污染正式数据目录的情况下验证落盘。
        using var history = options.DataDirectory is null
            ? CoreRuntime.CreateHistory(settings, log)
            : new HistoryStore(settings, log, Path.Combine(options.DataDirectory, "history"));
        engine.FrameProduced += (_, frame) =>
        {
            try
            {
                history.Append(frame);
            }
            catch
            {
                // 命令行场景忽略历史写入异常
            }
        };

        var frames = new List<SnapshotFrame>();
        var gate = new ManualResetEventSlim(false);
        var wanted = Math.Max(2, options.Frames);
        engine.FrameProduced += (_, frame) =>
        {
            lock (frames)
            {
                frames.Add(frame);
                if (frames.Count >= wanted)
                {
                    gate.Set();
                }
            }
        };

        Console.WriteLine($"PerfScope 采样冒烟：间隔 {options.IntervalMs} ms，目标 {wanted} 帧");
        var startup = Stopwatch.StartNew();
        engine.Start();
        var completed = gate.Wait(TimeSpan.FromMilliseconds(options.IntervalMs * (wanted + 6) + 10000));
        startup.Stop();
        engine.Stop();

        SnapshotFrame[] captured;
        lock (frames)
        {
            captured = frames.ToArray();
        }

        if (captured.Length == 0)
        {
            Console.WriteLine("没有采到任何帧（engine 未产出）。");
            return 2;
        }

        Console.WriteLine($"实收 {captured.Length} 帧，总耗时 {startup.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine();

        if (options.Json)
        {
            PrintJson(captured[^1], options.Top);
        }
        else
        {
            PrintFrame(captured[^1], options.Top);
            if (captured.Length >= 2)
            {
                Console.WriteLine();
                Console.WriteLine("=== 相邻两帧一致性检查 ===");
                var previous = captured[^2];
                Console.WriteLine($"上一帧应用数 {previous.Apps.Length}，本帧 {captured[^1].Apps.Length}，进程数 {previous.ProcessCount} -> {captured[^1].ProcessCount}");
                Console.WriteLine($"系统 CPU 上一帧 {previous.SystemCpuPercent:F2}% -> 本帧 {captured[^1].SystemCpuPercent:F2}%");
            }
        }

        // 历史写盘验证
        if (options.DataDirectory is not null)
        {
            history.Flush();
            var days = history.GetAvailableDays();
            Console.WriteLine();
            Console.WriteLine($"历史落盘：{days.Count} 天，目录 {options.DataDirectory}");
        }

        return completed ? 0 : 3;
    }

    /// <summary>读取磁盘上的历史（分钟聚合），打印某天的应用用量排行。</summary>
    private static int RunHistory(CliOptions options)
    {
        var dataDir = options.DataDirectory ?? AppPaths.DataDirectory;
        if (!Directory.Exists(dataDir))
        {
            Console.WriteLine("数据目录不存在：" + dataDir);
            return 1;
        }

        var settings = new SettingsStore(NullLog.Instance, Path.Combine(dataDir, "settings.json")).Load();
        using var history = new HistoryStore(settings, NullLog.Instance, Path.Combine(dataDir, "history"));
        var days = history.GetAvailableDays();
        if (days.Count == 0)
        {
            Console.WriteLine("没有历史数据：" + dataDir);
            return 2;
        }

        var day = options.HistoryDay ?? days[^1];
        var summaries = history.QueryDay(day);
        Console.WriteLine($"历史目录：{dataDir}");
        Console.WriteLine($"可用日期：{string.Join("、", days.Select(d => d.ToString("yyyy-MM-dd")))}");
        Console.WriteLine();
        Console.WriteLine($"{day:yyyy-MM-dd} 应用用量（共 {summaries.Count} 个应用，按消耗的能量排序）");
        Console.WriteLine();
        // CpuPercentSeconds 的口径是「整机 CPU 百分比 × 秒」。除以该应用有数据的分钟对应的秒数，
        // 就得到它在这些时间里的平均整机 CPU 占用（与界面上的 CPU% 同口径）。
        Console.WriteLine($"{"#",3}  {"应用",-30} {"平均CPU%",11} {"峰值CPU%",10} {"峰值内存MB",12} {"有数据分钟",10}");
        Console.WriteLine(new string('-', 100));
        var index = 0;
        foreach (var item in summaries.Take(options.Top))
        {
            index++;
            var minutes = Math.Max(1, item.SampleCount);
            var averagePercent = item.CpuPercentSeconds / (minutes * 60.0);
            Console.WriteLine($"{index,3}  {Trim(item.DisplayName, 28),-30} {averagePercent,11:F3} " +
                              $"{item.PeakCpuPercent,10:F2} {ToMb(item.PeakWorkingSetBytes),12:F1} {minutes,10}");
        }

        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"统计口径：整机 CPU（0~100，与任务管理器一致）；合计平均占用 " +
                          $"{summaries.Sum(s => s.CpuPercentSeconds) / (summaries.Max(s => Math.Max(1, s.SampleCount)) * 60.0):F1}%");
        return 0;
    }

    private static void PrintFrame(SnapshotFrame frame, int top)
    {
        var local = frame.TimestampUtc.ToLocalTime();
        Console.WriteLine($"时间 {local:yyyy-MM-dd HH:mm:ss}  帧间隔 {frame.Interval.TotalMilliseconds:F0} ms  " +
                          $"进程 {frame.ProcessCount}  应用 {frame.Apps.Length}");
        Console.WriteLine($"系统 CPU {frame.SystemCpuPercent:F1}%   内存 {frame.SystemMemoryPercent:F1}% " +
                          $"({ToGb(frame.TotalPhysicalBytes - frame.AvailablePhysicalBytes):F1}/{ToGb(frame.TotalPhysicalBytes):F1} GB)   " +
                          $"自身 CPU {frame.SelfCpuPercent:F2}% / 内存 {ToMb(frame.SelfWorkingSetBytes):F0} MB");
        Console.WriteLine();
        Console.WriteLine($"{"#",3}  {"应用",-28} {"CPU%",7} {"内存MB",9} {"进程",5} {"读MB/s",8} {"写MB/s",8}  归并依据");
        Console.WriteLine(new string('-', 132));
        var index = 0;
        foreach (var app in frame.Apps.Take(top))
        {
            index++;
            Console.WriteLine($"{index,3}  {Trim(app.DisplayName, 26),-28} {app.CpuPercent,7:F2} {ToMb(app.WorkingSetBytes),9:F1} " +
                              $"{app.ProcessCount,5} {app.ReadBytesPerSecond / 1048576.0,8:F2} {app.WriteBytesPerSecond / 1048576.0,8:F2}  {Trim(app.Reason, 40)}");
        }
    }

    private static void PrintJson(SnapshotFrame frame, int top)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine($"  \"timestampUtc\": \"{frame.TimestampUtc:O}\",");
        builder.AppendLine($"  \"intervalMs\": {frame.Interval.TotalMilliseconds:F0},");
        builder.AppendLine($"  \"systemCpuPercent\": {frame.SystemCpuPercent:F2},");
        builder.AppendLine($"  \"systemMemoryPercent\": {frame.SystemMemoryPercent:F2},");
        builder.AppendLine($"  \"processCount\": {frame.ProcessCount},");
        builder.AppendLine("  \"apps\": [");
        var apps = frame.Apps.Take(top).ToArray();
        for (var i = 0; i < apps.Length; i++)
        {
            var app = apps[i];
            builder.Append($"    {{ \"name\": \"{Escape(app.DisplayName)}\", \"cpuPercent\": {app.CpuPercent:F2}, " +
                           $"\"memoryMb\": {ToMb(app.WorkingSetBytes):F1}, \"processCount\": {app.ProcessCount}, " +
                           $"\"kind\": \"{app.Kind}\", \"reason\": \"{Escape(app.Reason)}\" }}");
            builder.AppendLine(i == apps.Length - 1 ? string.Empty : ",");
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        Console.WriteLine(builder.ToString());
    }

    private static int RunBenchmark(CliOptions options)
    {
        Console.WriteLine("NativeCapture 采集耗时实测（不含 UI 与归并）");
        using var capture = new NativeCapture();
        var warmup = capture.Capture();
        Console.WriteLine($"进程数 {warmup.Processes.Length}，逻辑核心 {warmup.LogicalProcessorCount}");
        var samples = new List<double>();
        for (var i = 0; i < options.BenchmarkRounds; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            var sample = capture.Capture();
            stopwatch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            Console.WriteLine($"第 {i + 1,2} 次：{stopwatch.Elapsed.TotalMilliseconds,7:F2} ms  进程 {sample.Processes.Length,5}  托管分配 {allocated / 1024.0,6:F1} KB");
        }

        samples.Sort();
        var average = samples.Average();
        Console.WriteLine();
        Console.WriteLine($"平均 {average:F2} ms，最小 {samples[0]:F2} ms，中位 {samples[samples.Count / 2]:F2} ms，最大 {samples[^1]:F2} ms");

        // 折算：默认 2 秒采样，单核占用 = 采集耗时 / 间隔
        var defaultRatio = average / 2000.0 * 100.0;
        var hiddenRatio = average / 5000.0 * 100.0;
        var cores = Environment.ProcessorCount;
        Console.WriteLine($"折算自身 CPU（默认 2s 间隔）：{defaultRatio / cores:F3}% 整机 / {defaultRatio:F2}% 单核");
        Console.WriteLine($"折算自身 CPU（隐藏 5s 间隔）：{hiddenRatio / cores:F3}% 整机 / {hiddenRatio:F2}% 单核");
        return 0;
    }

    private static double ToMb(long bytes) => bytes / 1048576.0;

    private static double ToGb(long bytes) => bytes / 1073741824.0;

    private static string Trim(string? text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>命令行参数。</summary>
internal sealed class CliOptions
{
    public int Frames { get; private set; } = 3;

    public int IntervalMs { get; private set; } = 2000;

    public int Top { get; private set; } = 12;

    public bool Json { get; private set; }

    public bool Benchmark { get; private set; }

    public bool History { get; private set; }

    public DateOnly? HistoryDay { get; private set; }

    public int BenchmarkRounds { get; private set; } = 10;

    public bool ShowHelp { get; private set; }

    public string? DataDirectory { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--frames":
                    options.Frames = ReadInt(args, ref i, 3);
                    break;
                case "--interval":
                    options.IntervalMs = Math.Clamp(ReadInt(args, ref i, 2000), 500, 60000);
                    break;
                case "--top":
                    options.Top = Math.Clamp(ReadInt(args, ref i, 12), 1, 200);
                    break;
                case "--history":
                    options.History = true;
                    break;
                case "--day":
                    if (i + 1 < args.Length && DateOnly.TryParse(args[i + 1], out var day))
                    {
                        options.HistoryDay = day;
                        i++;
                    }

                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--bench":
                case "--benchmark":
                    options.Benchmark = true;
                    break;
                case "--rounds":
                    options.BenchmarkRounds = Math.Clamp(ReadInt(args, ref i, 10), 1, 200);
                    break;
                case "--data-dir":
                    options.DataDirectory = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("PerfScope.Cli —— 不开界面跑真实采样，用于冒烟、性能实测与排障");
        Console.WriteLine("  --frames <N>        采集帧数（默认 3，首帧为基线）");
        Console.WriteLine("  --interval <MS>     采样间隔（默认 2000）");
        Console.WriteLine("  --top <N>           打印前 N 个应用（默认 12）");
        Console.WriteLine("  --json              以 JSON 输出");
        Console.WriteLine("  --bench             只测 NativeCapture 采集耗时");
        Console.WriteLine("  --rounds <N>        性能测试轮数（默认 10）");
        Console.WriteLine("  --data-dir <目录>   使用独立数据目录（不影响正式配置）");
        Console.WriteLine("  --history           读取 --data-dir 下的历史，打印当日应用用量排行");
        Console.WriteLine("  --day <yyyy-MM-dd>  指定历史日期（默认最近一天）");
    }

    private static int ReadInt(string[] args, ref int index, int fallback)
    {
        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var value))
        {
            index++;
            return value;
        }

        return fallback;
    }
}
