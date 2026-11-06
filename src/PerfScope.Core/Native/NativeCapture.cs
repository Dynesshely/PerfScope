using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Models;

namespace PerfScope.Core.Native;

/// <summary>
/// 原生批量采集实现：一次 <c>ntdll!NtQuerySystemInformation(SystemProcessInformation)</c> 拿回全部进程，
/// 再加一次逐核 CPU 查询与一次物理内存查询，全程零 WMI、零 PerformanceCounter、零逐进程轮询。
/// </summary>
/// <remarks>
/// <para><b>缓冲区复用</b>：非托管缓冲区挂在实例上，起始 512KB，遇 <c>STATUS_INFO_LENGTH_MISMATCH</c>
/// 扩容（上限 64MB）后原地重试；稳定帧里不再分配非托管内存。</para>
/// <para><b>托管分配</b>：映像名字符串按 PID 复用（同一 PID 名字未变时直接返回上次的实例），
/// exe 全路径走缓存，热路径不 new DateTime、不建闭包。</para>
/// <para><b>线程安全</b>：<see cref="Capture"/> 内部串行化，可被引擎线程与 UI 强制刷新并发调用。</para>
/// </remarks>
public sealed class NativeCapture : ICapture, IDisposable
{
    private const int InitialProcessBufferSize = 512 * 1024;
    private const int MaxProcessBufferSize = 64 * 1024 * 1024;
    private const int MaxGrowAttempts = 8;
    private const int NameCacheLimit = 8192;
    private const int SlowCaptureLogThrottleMilliseconds = 60_000;

    private static readonly Comparison<ProcessRawSample> PidComparison =
        static (left, right) => left.Pid.CompareTo(right.Pid);

    private readonly object _gate = new();
    private readonly Dictionary<int, CachedName> _nameCache = new(512);
    private readonly List<int> _pruneScratch = new(64);
    private readonly List<ProcessRawSample> _processScratch = new(512);
    private readonly ProcessPathResolver _pathResolver = new();

    private IntPtr _processBuffer;
    private int _processBufferSize;
    private IntPtr _processorBuffer;
    private int _processorBufferSize;
    private IntPtr _basicInfoBuffer;
    private int _logicalProcessorCount;
    private char[] _pathScratch = new char[1024];
    private int _frameIndex;
    private long _lastSlowLogTimestamp;
    private bool _disposed;

    /// <summary>构造采集器。构造过程不分配非托管内存（首次采集时按需分配）。</summary>
    /// <exception cref="PlatformNotSupportedException">当前进程不是 64 位，结构布局不成立。</exception>
    public NativeCapture()
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("PerfScope 的原生采样依赖 64 位进程结构布局，请在 x64 / arm64 进程下运行。");
        }
    }

    /// <summary>
    /// 诊断回调：单次采集耗时超过 <see cref="SlowCaptureThresholdMs"/> 等异常情况通过它上报。
    /// 构造函数按契约无 <c>ILog</c> 参数，因此由使用方（通常是 <c>Sampling.MonitorEngine</c>）挂接日志。
    /// </summary>
    public Action<string>? Diagnostics { get; set; }

    /// <summary>慢采集告警阈值（毫秒）。默认 50。</summary>
    public int SlowCaptureThresholdMs { get; set; } = 50;

    /// <summary>最近一次采集耗时（毫秒）。</summary>
    public double LastCaptureMilliseconds { get; private set; }

    /// <summary>逻辑处理器数量；首次采集之前为 0。</summary>
    public int LogicalProcessorCount
    {
        get
        {
            lock (_gate)
            {
                return _logicalProcessorCount;
            }
        }
    }

    /// <inheritdoc />
    public SystemRawSample Capture()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeCapture));
            }

            long startedTimestamp = Stopwatch.GetTimestamp();
            DateTime timestampUtc = DateTime.UtcNow;

            int logicalProcessorCount = EnsureProcessorCount();
            ProcessRawSample[] processes = QueryProcesses();
            ReadProcessorTimes(logicalProcessorCount, out long idleTicks, out long kernelTicks, out long userTicks);
            ReadPhysicalMemory(out long totalPhysicalBytes, out long availablePhysicalBytes);

            double elapsedMilliseconds = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;
            LastCaptureMilliseconds = elapsedMilliseconds;
            ReportSlowCapture(elapsedMilliseconds);

            return new SystemRawSample(
                timestampUtc,
                logicalProcessorCount,
                idleTicks,
                kernelTicks,
                userTicks,
                totalPhysicalBytes,
                availablePhysicalBytes,
                processes);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FreeBuffers();
            _processScratch.Clear();
            _nameCache.Clear();
            _pathResolver.Clear();
            Diagnostics = null;
        }
    }

    private void FreeBuffers()
    {
        if (_processBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_processBuffer);
            _processBuffer = IntPtr.Zero;
            _processBufferSize = 0;
        }

        if (_processorBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_processorBuffer);
            _processorBuffer = IntPtr.Zero;
            _processorBufferSize = 0;
        }

        if (_basicInfoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_basicInfoBuffer);
            _basicInfoBuffer = IntPtr.Zero;
        }
    }

    private void ReportSlowCapture(double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= SlowCaptureThresholdMs)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastSlowLogTimestamp < SlowCaptureLogThrottleMilliseconds)
        {
            return;
        }

        _lastSlowLogTimestamp = now;
        Diagnostics?.Invoke(
            $"原生采集单次耗时 {elapsedMilliseconds:F1} ms（阈值 {SlowCaptureThresholdMs} ms），可能系统繁忙或进程数激增。");
    }

    // ================= 逻辑处理器数量 =================

    private int EnsureProcessorCount()
    {
        if (_logicalProcessorCount > 0)
        {
            return _logicalProcessorCount;
        }

        int count = 0;
        int basicInfoSize = Marshal.SizeOf<NativeMethods.SystemBasicInformation>();
        if (_basicInfoBuffer == IntPtr.Zero)
        {
            _basicInfoBuffer = Marshal.AllocHGlobal(basicInfoSize);
        }

        int status = NativeMethods.NtQuerySystemInformation(
            NativeMethods.BasicInformationClass,
            _basicInfoBuffer,
            basicInfoSize,
            out _);

        if (status == 0)
        {
            NativeMethods.SystemBasicInformation info = Marshal.PtrToStructure<NativeMethods.SystemBasicInformation>(_basicInfoBuffer);
            count = info.NumberOfProcessors;
        }

        if (count <= 0)
        {
            count = Environment.ProcessorCount;
        }

        if (count <= 0)
        {
            count = 1;
        }

        _logicalProcessorCount = count;

        int requiredProcessorBuffer = count * NativeMethods.ProcessorPerformanceOffset.StructSize;
        if (_processorBufferSize < requiredProcessorBuffer)
        {
            GrowProcessorBuffer(requiredProcessorBuffer);
        }

        return count;
    }

    // ================= 进程表 =================

    private unsafe ProcessRawSample[] QueryProcesses()
    {
        if (_processBuffer == IntPtr.Zero)
        {
            GrowProcessBuffer(InitialProcessBufferSize);
        }

        int status;
        int attempt = 0;
        int requiredLength = 0;
        while (true)
        {
            status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.ProcessInformationClass,
                _processBuffer,
                _processBufferSize,
                out requiredLength);

            if (status != NativeMethods.StatusInfoLengthMismatch)
            {
                break;
            }

            if (++attempt > MaxGrowAttempts)
            {
                break;
            }

            long target = requiredLength > _processBufferSize
                ? requiredLength + (requiredLength >> 2) + 65536L
                : (long)_processBufferSize * 2;

            int newSize = (int)Math.Min(target, MaxProcessBufferSize);
            if (newSize <= _processBufferSize)
            {
                break;
            }

            GrowProcessBuffer(newSize);
        }

        if (status != 0)
        {
            throw new InvalidOperationException($"NtQuerySystemInformation(SystemProcessInformation) 调用失败：0x{status:X8}");
        }

        return ParseProcesses();
    }

    private unsafe ProcessRawSample[] ParseProcesses()
    {
        byte* basePointer = (byte*)_processBuffer;
        long bufferEnd = (long)_processBuffer + _processBufferSize;
        List<ProcessRawSample> processes = _processScratch;
        processes.Clear();
        int frame = ++_frameIndex;
        byte* entry = basePointer;

        while ((long)entry + NativeMethods.ProcessInfoOffset.MinimumEntrySize <= bufferEnd)
        {
            int nextOffset = Unsafe.ReadUnaligned<int>(entry + NativeMethods.ProcessInfoOffset.NextEntryOffset);
            int pid = Unsafe.ReadUnaligned<int>(entry + NativeMethods.ProcessInfoOffset.UniqueProcessId);
            int parentPid = Unsafe.ReadUnaligned<int>(entry + NativeMethods.ProcessInfoOffset.InheritedFromUniqueProcessId);
            long createFileTime = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.CreateTime);
            long userTime = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.UserTime);
            long kernelTime = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.KernelTime);
            long workingSetBytes = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.WorkingSetSize);
            long workingSetPrivateBytes = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.WorkingSetPrivateSize);
            long privatePageCount = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.PrivatePageCount);
            long readTransferBytes = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.ReadTransferCount);
            long writeTransferBytes = Unsafe.ReadUnaligned<long>(entry + NativeMethods.ProcessInfoOffset.WriteTransferCount);
            int threadCount = Unsafe.ReadUnaligned<int>(entry + NativeMethods.ProcessInfoOffset.NumberOfThreads);
            int handleCount = (int)Unsafe.ReadUnaligned<uint>(entry + NativeMethods.ProcessInfoOffset.HandleCount);
            ushort imageNameLength = Unsafe.ReadUnaligned<ushort>(entry + NativeMethods.ProcessInfoOffset.ImageNameLength);
            IntPtr imageNameBuffer = Unsafe.ReadUnaligned<IntPtr>(entry + NativeMethods.ProcessInfoOffset.ImageNameBuffer);

            string imageName = ResolveImageName(pid, imageNameLength, imageNameBuffer, frame, bufferEnd);
            long createTimeUtcTicks = createFileTime > 0 ? DateTime.FromFileTimeUtc(createFileTime).Ticks : 0;

            // 私有提交字节优先用 PrivatePageCount；极少数进程该值为 0（如 Idle），退回私有工作集。
            long privateBytes = privatePageCount > 0 ? privatePageCount : Math.Max(0, workingSetPrivateBytes);

            string? executablePath = _pathResolver.Resolve(pid, createTimeUtcTicks, frame, ref _pathScratch);

            processes.Add(new ProcessRawSample(
                pid,
                parentPid,
                imageName,
                executablePath,
                userTime + kernelTime,
                createTimeUtcTicks,
                workingSetBytes,
                privateBytes,
                readTransferBytes,
                writeTransferBytes,
                threadCount,
                handleCount));

            if (nextOffset <= 0)
            {
                break;
            }

            entry += nextOffset;
        }

        _pathResolver.Trim(frame);
        PruneNameCache(frame);

        ProcessRawSample[] result = processes.ToArray();
        Array.Sort(result, PidComparison);
        return result;
    }

    private unsafe string ResolveImageName(int pid, ushort length, IntPtr pointer, int frame, long bufferEnd)
    {
        long pointerValue = pointer.ToInt64();
        if (length > 0 && pointerValue != 0 && pointerValue >= (long)_processBuffer && pointerValue + length <= bufferEnd)
        {
            var span = new ReadOnlySpan<char>((void*)pointerValue, length / 2);
            if (_nameCache.TryGetValue(pid, out CachedName cached) && span.SequenceEqual(cached.Name.AsSpan()))
            {
                cached.Frame = frame;
                _nameCache[pid] = cached;
                return cached.Name;
            }

            string name = new(span);
            _nameCache[pid] = new CachedName { Name = name, Frame = frame };
            return name;
        }

        string fallback = pid == 0 ? "Idle" : "Unknown";
        _nameCache[pid] = new CachedName { Name = fallback, Frame = frame };
        return fallback;
    }

    private void PruneNameCache(int frame)
    {
        if (_nameCache.Count <= NameCacheLimit)
        {
            return;
        }

        _pruneScratch.Clear();
        foreach (KeyValuePair<int, CachedName> pair in _nameCache)
        {
            if (pair.Value.Frame < frame)
            {
                _pruneScratch.Add(pair.Key);
            }
        }

        foreach (int key in _pruneScratch)
        {
            _nameCache.Remove(key);
        }

        _pruneScratch.Clear();
    }

    private void GrowProcessBuffer(int size)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size);
        if (_processBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_processBuffer);
        }

        _processBuffer = buffer;
        _processBufferSize = size;
    }

    private void GrowProcessorBuffer(int size)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size);
        if (_processorBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_processorBuffer);
        }

        _processorBuffer = buffer;
        _processorBufferSize = size;
    }

    // ================= 系统 CPU =================

    private unsafe void ReadProcessorTimes(int logicalProcessorCount, out long idleTicks, out long kernelTicks, out long userTicks)
    {
        idleTicks = 0;
        kernelTicks = 0;
        userTicks = 0;

        int required = logicalProcessorCount * NativeMethods.ProcessorPerformanceOffset.StructSize;
        if (_processorBufferSize < required)
        {
            GrowProcessorBuffer(required);
        }

        int status = NativeMethods.NtQuerySystemInformation(
            NativeMethods.ProcessorPerformanceInformationClass,
            _processorBuffer,
            _processorBufferSize,
            out int returnedLength);

        if (status != 0)
        {
            return;
        }

        int available = returnedLength > 0 && returnedLength <= _processorBufferSize ? returnedLength : _processorBufferSize;
        int stride = available >= logicalProcessorCount * NativeMethods.ProcessorPerformanceOffset.StructSize
            ? NativeMethods.ProcessorPerformanceOffset.StructSize
            : available / Math.Max(1, logicalProcessorCount);

        if (stride < NativeMethods.ProcessorPerformanceOffset.StructSize)
        {
            // 返回长度异常（例如系统提前返回）：退回固定步长，避免踩到别人的内存。
            stride = NativeMethods.ProcessorPerformanceOffset.StructSize;
        }

        byte* buffer = (byte*)_processorBuffer;
        for (int index = 0; index < logicalProcessorCount; index++)
        {
            byte* item = buffer + ((long)index * stride);
            idleTicks += Unsafe.ReadUnaligned<long>(item + NativeMethods.ProcessorPerformanceOffset.IdleTime);
            kernelTicks += Unsafe.ReadUnaligned<long>(item + NativeMethods.ProcessorPerformanceOffset.KernelTime);
            userTicks += Unsafe.ReadUnaligned<long>(item + NativeMethods.ProcessorPerformanceOffset.UserTime);
        }
    }

    private static void ReadPhysicalMemory(out long totalPhysicalBytes, out long availablePhysicalBytes)
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            totalPhysicalBytes = (long)status.TotalPhys;
            availablePhysicalBytes = (long)status.AvailPhys;
            return;
        }

        totalPhysicalBytes = 0;
        availablePhysicalBytes = 0;
    }

    private struct CachedName
    {
        /// <summary>上一次为该 PID 构造的映像名字符串。</summary>
        public string Name;

        /// <summary>最后一次出现的帧号。</summary>
        public int Frame;
    }
}
