using System.Diagnostics;
using PerfScope.Core.Models;
using PerfScope.Core.Native;
using Xunit.Abstractions;

namespace PerfScope.Tests.Sampling;

/// <summary><see cref="NativeCapture"/> 的冒烟与开销测试（依赖真实 Windows 系统）。</summary>
public sealed class NativeCaptureTests
{
    private readonly ITestOutputHelper _output;

    public NativeCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void 连续采集稳定且单次耗时低于阈值()
    {
        using var capture = new NativeCapture();

        // 预热：首帧需要为全部进程建立 exe 路径缓存（OpenProcess 次数最多）。
        SystemRawSample warmup = capture.Capture();
        Assert.True(warmup.Processes.Length > 50, $"进程数过少：{warmup.Processes.Length}");

        const int Iterations = 10;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        double totalMilliseconds = 0;
        SystemRawSample last = warmup;

        for (int index = 0; index < Iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            last = capture.Capture();
            totalMilliseconds += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double averageMilliseconds = totalMilliseconds / Iterations;
        double bytesPerFrame = allocatedBytes / (double)Iterations;

        _output.WriteLine($"单次 NativeCapture 平均耗时：{averageMilliseconds:F3} ms（{Iterations} 次，首帧已预热）");
        _output.WriteLine($"本机进程数量：{last.Processes.Length}");
        _output.WriteLine($"逻辑处理器：{last.LogicalProcessorCount}");
        _output.WriteLine($"每帧托管分配（GC.GetAllocatedBytesForCurrentThread 差分）：{bytesPerFrame:F0} B");
        _output.WriteLine($"单帧最小/平均耗时：{capture.LastCaptureMilliseconds:F3} ms（最近一次）");

        // 单次采集预算 50 ms 是按常规桌面（约 300~400 个进程）定的；
        // 进程数上千时内核遍历本身的耗时会线性上涨（实测内核查询占 85% 以上），
        // 因此按「每进程 0.12 ms」线性放宽，保证本用例在重负载机器上仍然有意义。
        double budgetMilliseconds = Math.Max(50d, last.Processes.Length * 0.12);
        _output.WriteLine($"本次判定预算：{budgetMilliseconds:F1} ms（50 ms 基线，进程数 > 416 时按每进程 0.12 ms 放宽）");

        Assert.True(last.Processes.Length > 50, $"进程数过少：{last.Processes.Length}");
        Assert.True(averageMilliseconds < budgetMilliseconds, $"平均耗时过高：{averageMilliseconds:F3} ms（预算 {budgetMilliseconds:F1} ms）");
        Assert.True(bytesPerFrame < 256 * 1024, $"每帧托管分配过高：{bytesPerFrame:F0} B");
    }

    [Fact]
    public void 采集结果的字段合理()
    {
        using var capture = new NativeCapture();
        SystemRawSample sample = capture.Capture();

        Assert.Equal(DateTimeKind.Utc, sample.TimestampUtc.Kind);
        Assert.True(sample.LogicalProcessorCount >= 1);
        Assert.True(sample.TotalPhysicalBytes > 0);
        Assert.True(sample.AvailablePhysicalBytes >= 0);
        Assert.True(sample.AvailablePhysicalBytes <= sample.TotalPhysicalBytes);
        Assert.True(sample.SystemCpuIdleTicks >= 0);
        Assert.True(sample.SystemCpuKernelTicks >= sample.SystemCpuIdleTicks);
        Assert.NotEmpty(sample.Processes);

        // 按 PID 升序返回。
        for (int index = 1; index < sample.Processes.Length; index++)
        {
            Assert.True(sample.Processes[index - 1].Pid < sample.Processes[index].Pid);
        }

        Assert.Contains(sample.Processes, static process => process.Pid == Environment.ProcessId);
        Assert.Contains(sample.Processes, static process => !string.IsNullOrEmpty(process.ImageName));
        Assert.All(sample.Processes, static process => Assert.False(string.IsNullOrEmpty(process.ImageName)));

        // 至少有一部分进程能拿到 exe 全路径（受保护进程拿不到属于正常现象）。
        int withPath = sample.Processes.Count(static process => process.ExecutablePath is not null);
        _output.WriteLine($"可解析 exe 全路径的进程：{withPath} / {sample.Processes.Length}");
        Assert.True(withPath > 0);

        // 路径查不到的进程必须为 null，不允许空字符串。
        Assert.DoesNotContain(sample.Processes, static process => process.ExecutablePath is { Length: 0 });

        // 自身进程的创建时间必须非 0（用于 PID 复用判定），工作集应当大于 1MB。
        ProcessRawSample self = sample.Processes.First(static process => process.Pid == Environment.ProcessId);
        Assert.True(self.CreateTimeUtcTicks > 0);
        Assert.True(self.WorkingSetBytes > 1024 * 1024);
        Assert.True(self.PrivateBytes > 0);
        Assert.True(self.ThreadCount >= 1);

        int withParent = sample.Processes.Count(static process => process.ParentPid > 0);
        _output.WriteLine($"有父进程的进程：{withParent}");
        Assert.True(withParent > 0);
    }

    [Fact]
    public void 系统CPU累计时间单调递增()
    {
        using var capture = new NativeCapture();
        SystemRawSample first = capture.Capture();
        Thread.Sleep(150);
        SystemRawSample second = capture.Capture();

        Assert.True(second.SystemCpuIdleTicks >= first.SystemCpuIdleTicks);
        Assert.True(second.SystemCpuKernelTicks >= first.SystemCpuKernelTicks);
        Assert.True(second.SystemCpuUserTicks >= first.SystemCpuUserTicks);
        Assert.True(second.TimestampUtc >= first.TimestampUtc);
    }

    [Fact]
    public void 释放后再采集抛出对象已释放()
    {
        var capture = new NativeCapture();
        capture.Capture();
        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.Capture());
    }
}
