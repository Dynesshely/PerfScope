using System.Runtime.InteropServices;

namespace PerfScope.Core.Native;

/// <summary>
/// ntdll / kernel32 的原生互操作声明、结构布局常量。仅供本命名空间内部使用，不属于公共契约。
/// </summary>
/// <remarks>
/// 所有 <c>SYSTEM_*</c> 结构的偏移量均按 <b>64 位</b>进程（x64 / arm64）的 Windows 布局给出。
/// 32 位布局完全不同，<see cref="NativeCapture"/> 在构造函数里会拒绝 32 位进程。
/// </remarks>
internal static class NativeMethods
{
    /// <summary>SystemBasicInformation(0)：基础信息，含逻辑处理器数量。</summary>
    internal const int BasicInformationClass = 0;

    /// <summary>SystemProcessInformation(5)：一次查询返回全部进程。</summary>
    internal const int ProcessInformationClass = 5;

    /// <summary>SystemProcessorPerformanceInformation(8)：逐核 CPU 时间。</summary>
    internal const int ProcessorPerformanceInformationClass = 8;

    /// <summary>STATUS_INFO_LENGTH_MISMATCH(0xC0000004)：传入缓冲区不足，返回长度里给出所需大小。</summary>
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION：拿到映像全路径所需的最小权限。</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>ERROR_INSUFFICIENT_BUFFER：QueryFullProcessImageNameW 的缓冲区不足错误码。</summary>
    internal const int ErrorInsufficientBuffer = 122;

    /// <summary>进程映像全路径的最大字符数（含结尾 0），Windows 上限为 32767。</summary>
    internal const int MaxPathChars = 32768;

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// <c>SYSTEM_PROCESS_INFORMATION</c> 在 64 位 Windows 上的字段偏移。
    /// 结构体本身不在这里定义：解析时直接在非托管缓冲区上按偏移读取，避免整块结构体的托管拷贝。
    /// </summary>
    internal static class ProcessInfoOffset
    {
        /// <summary>ULONG NextEntryOffset，0 表示当前是最后一项。</summary>
        internal const int NextEntryOffset = 0;

        /// <summary>ULONG NumberOfThreads。</summary>
        internal const int NumberOfThreads = 4;

        /// <summary>LARGE_INTEGER WorkingSetPrivateSize（私有工作集）。</summary>
        internal const int WorkingSetPrivateSize = 8;

        /// <summary>LARGE_INTEGER CreateTime（FILETIME，1601 起的 100ns）。</summary>
        internal const int CreateTime = 32;

        /// <summary>LARGE_INTEGER UserTime（100ns）。</summary>
        internal const int UserTime = 40;

        /// <summary>LARGE_INTEGER KernelTime（100ns，含空闲）。</summary>
        internal const int KernelTime = 48;

        /// <summary>UNICODE_STRING ImageName.Length（USHORT）。</summary>
        internal const int ImageNameLength = 56;

        /// <summary>UNICODE_STRING ImageName.Buffer（指向同一块缓冲区内部的 PWSTR）。</summary>
        internal const int ImageNameBuffer = 64;

        /// <summary>HANDLE UniqueProcessId（低 32 位即 PID）。</summary>
        internal const int UniqueProcessId = 80;

        /// <summary>HANDLE InheritedFromUniqueProcessId（父 PID）。</summary>
        internal const int InheritedFromUniqueProcessId = 88;

        /// <summary>ULONG HandleCount。</summary>
        internal const int HandleCount = 96;

        /// <summary>SIZE_T WorkingSetSize（工作集）。</summary>
        internal const int WorkingSetSize = 144;

        /// <summary>SIZE_T PrivatePageCount（私有提交字节）。</summary>
        internal const int PrivatePageCount = 200;

        /// <summary>LARGE_INTEGER ReadTransferCount（累计读字节）。</summary>
        internal const int ReadTransferCount = 232;

        /// <summary>LARGE_INTEGER WriteTransferCount（累计写字节）。</summary>
        internal const int WriteTransferCount = 240;

        /// <summary>64 位下单个进程项的最小字节数（不含映像名与线程数组）。</summary>
        internal const int MinimumEntrySize = 256;
    }

    /// <summary>
    /// <c>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION</c> 的字段偏移与步长（64 位）。
    /// 结构体为 5 个 LARGE_INTEGER + 1 个 ULONG，按 8 字节对齐后实际占 48 字节。
    /// </summary>
    internal static class ProcessorPerformanceOffset
    {
        /// <summary>LARGE_INTEGER IdleTime。</summary>
        internal const int IdleTime = 0;

        /// <summary>LARGE_INTEGER KernelTime（含空闲）。</summary>
        internal const int KernelTime = 8;

        /// <summary>LARGE_INTEGER UserTime。</summary>
        internal const int UserTime = 16;

        /// <summary>64 位下每一项的步长。</summary>
        internal const int StructSize = 48;
    }

    /// <summary><c>MEMORYSTATUSEX</c>，长度 64 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        /// <summary>结构体大小，调用前必须赋值为本结构体字节数。</summary>
        public uint Length;

        /// <summary>内存负载百分比。</summary>
        public uint MemoryLoad;

        /// <summary>物理内存总量（字节）。</summary>
        public ulong TotalPhys;

        /// <summary>可用物理内存（字节）。</summary>
        public ulong AvailPhys;

        /// <summary>页面文件总量。</summary>
        public ulong TotalPageFile;

        /// <summary>可用页面文件。</summary>
        public ulong AvailPageFile;

        /// <summary>虚拟内存总量。</summary>
        public ulong TotalVirtual;

        /// <summary>可用虚拟内存。</summary>
        public ulong AvailVirtual;

        /// <summary>扩展虚拟内存。</summary>
        public ulong AvailExtendedVirtual;
    }

    /// <summary><c>SYSTEM_BASIC_INFORMATION</c>，只取逻辑处理器数量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemBasicInformation
    {
        /// <summary>保留字段。</summary>
        public uint Reserved;

        /// <summary>定时器分辨率。</summary>
        public uint TimerResolution;

        /// <summary>页大小。</summary>
        public uint PageSize;

        /// <summary>物理页数量。</summary>
        public uint NumberOfPhysicalPages;

        /// <summary>最低物理页号。</summary>
        public uint LowestPhysicalPageNumber;

        /// <summary>最高物理页号。</summary>
        public uint HighestPhysicalPageNumber;

        /// <summary>分配粒度。</summary>
        public uint AllocationGranularity;

        /// <summary>用户态地址下界。</summary>
        public IntPtr MinimumUserModeAddress;

        /// <summary>用户态地址上界。</summary>
        public IntPtr MaximumUserModeAddress;

        /// <summary>活动处理器亲和掩码。</summary>
        public IntPtr ActiveProcessorsAffinityMask;

        /// <summary>逻辑处理器数量。</summary>
        public byte NumberOfProcessors;
    }
}
