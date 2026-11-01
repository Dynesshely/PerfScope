namespace PerfScope.Core.Models;

/// <summary>
/// 一次采样中单个进程的「累计计数器」快照。时间单位是 100ns（与 Windows 的 FILETIME 一致），
/// 上层通过相邻两次快照的差分算出本区间占用。
/// </summary>
/// <param name="Pid">进程 ID。</param>
/// <param name="ParentPid">父进程 ID（可能已退出）。</param>
/// <param name="ImageName">映像文件名，例如 <c>chrome.exe</c>，永远非空。</param>
/// <param name="ExecutablePath">可执行文件完整路径；受保护/系统进程可能拿不到，此时为 <c>null</c>。</param>
/// <param name="CpuTimeTicks">内核态 + 用户态累计 CPU 时间（100ns）。</param>
/// <param name="CreateTimeUtcTicks">进程创建时间（UTC ticks），用于识别 PID 复用。</param>
/// <param name="WorkingSetBytes">工作集（物理内存驻留）字节数。</param>
/// <param name="PrivateBytes">私有提交字节数。</param>
/// <param name="ReadBytesTransferred">累计读 IO 字节数。</param>
/// <param name="WriteBytesTransferred">累计写 IO 字节数。</param>
/// <param name="ThreadCount">线程数。</param>
/// <param name="HandleCount">句柄数。</param>
public readonly record struct ProcessRawSample(
    int Pid,
    int ParentPid,
    string ImageName,
    string? ExecutablePath,
    long CpuTimeTicks,
    long CreateTimeUtcTicks,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytesTransferred,
    long WriteBytesTransferred,
    int ThreadCount,
    int HandleCount);

/// <summary>
/// 一次系统级采样的完整结果：系统总量 + 全部进程。由 <see cref="PerfScope.Core.Abstractions.ICapture"/> 产生。
/// </summary>
/// <param name="TimestampUtc">采集时刻（UTC）。</param>
/// <param name="LogicalProcessorCount">逻辑处理器数量，用于 CPU 百分比归一化。</param>
/// <param name="SystemCpuIdleTicks">全系统累计空闲时间（100ns）。</param>
/// <param name="SystemCpuKernelTicks">全系统累计内核时间（100ns，含空闲）。</param>
/// <param name="SystemCpuUserTicks">全系统累计用户时间（100ns）。</param>
/// <param name="TotalPhysicalBytes">物理内存总量。</param>
/// <param name="AvailablePhysicalBytes">可用物理内存。</param>
/// <param name="Processes">本次采集到的所有进程（建议按 PID 升序）。</param>
public sealed record SystemRawSample(
    DateTime TimestampUtc,
    int LogicalProcessorCount,
    long SystemCpuIdleTicks,
    long SystemCpuKernelTicks,
    long SystemCpuUserTicks,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    ProcessRawSample[] Processes);
