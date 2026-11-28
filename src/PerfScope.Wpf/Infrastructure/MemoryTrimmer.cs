using System.Runtime.InteropServices;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 工作集修剪：托盘常驻时把长期不用的页面交还系统，让任务管理器里看到的
/// 工作集（多数人判断「占多少内存」的依据）保持在一个低水位。
/// 只归还物理页，不释放任何托管对象，下次显示窗口时按需重新换入。
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

    /// <summary>先让 GC 释放可回收对象，再归还工作集（窗口隐藏 / 长时间空闲时调用）。</summary>
    public static void TrimWithGc()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }
        catch
        {
            // 忽略
        }

        Trim();
    }
}
