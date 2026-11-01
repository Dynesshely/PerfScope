using System.Runtime.InteropServices;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 工作集修剪：托盘常驻时把长期不用的页面交给系统回收，让任务管理器里看到的
/// 工作集（很多人判断「占多少内存」的依据）保持在一个很低的水位。
/// 只是归还物理页，不释放任何托管对象，下次显示窗口时会按需重新换入。
/// </summary>
internal static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>修剪当前进程工作集；失败静默（部分环境可能禁用该调用）。</summary>
    public static void Trim()
    {
        try
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // 修剪失败不影响任何功能
        }
    }
}
