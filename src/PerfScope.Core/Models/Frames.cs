namespace PerfScope.Core.Models;

/// <summary>应用归并键的来源类别，用于在 UI 上解释「为什么这些进程被算作同一个应用」。</summary>
public enum AppKind
{
    /// <summary>按安装根目录归并（Program Files / LOCALAPPDATA\Programs 等规则，或向上探测到的卸载器目录）。</summary>
    InstallRoot = 0,

    /// <summary>按可执行文件版本信息（ProductName + CompanyName）归并。</summary>
    ProductName = 1,

    /// <summary>按可执行文件所在目录归并。</summary>
    ExeDirectory = 2,

    /// <summary>只能按可执行文件名归并（拿不到路径，或不满足上面任何条件）。</summary>
    ExeName = 3,

    /// <summary>Windows 系统进程 / 服务的聚合组。</summary>
    SystemProcess = 4,

    /// <summary>由用户自定义规则指定。</summary>
    CustomRule = 5,

    /// <summary>其它（无法归类的兜底组）。</summary>
    Unknown = 6,

    /// <summary>按系统功能模块归并（WSL、虚拟机与容器、图形界面、安全防护、搜索、更新、内核等）。</summary>
    SystemModule = 7,
}

/// <summary>
/// 归类结果：一个进程应当归属到哪个「应用」。
/// <see cref="Key"/> 是稳定标识（同一次运行内稳定，跨重启也稳定），<see cref="DisplayName"/> 用于展示。
/// </summary>
/// <param name="Key">稳定归并键。约定格式：<c>dir:&lt;安装根&gt;</c> / <c>product:&lt;company&gt;|&lt;product&gt;</c> / <c>exe:&lt;name&gt;</c> / <c>rule:&lt;规则标识&gt;</c> / <c>sys:&lt;组名&gt;</c>。</param>
/// <param name="DisplayName">展示名，例如 <c>Google Chrome</c>。</param>
/// <param name="RootPath">应用根目录（安装目录或主程序目录），可为 <c>null</c>。</param>
/// <param name="Kind">归类来源类别。</param>
/// <param name="Reason">一句话解释归并依据，直接展示在 UI 提示里。</param>
/// <param name="Excluded">用户规则要求排除该进程（不进入应用列表与统计）。</param>
public sealed record AppIdentity(
    string Key,
    string DisplayName,
    string? RootPath,
    AppKind Kind,
    string Reason,
    bool Excluded = false);

/// <summary>应用内的单个进程明细（用于 UI 展开查看与排障）。</summary>
/// <param name="Pid">进程 ID。</param>
/// <param name="Name">映像名，例如 <c>chrome.exe</c>。</param>
/// <param name="ExecutablePath">完整路径，可能为 <c>null</c>。</param>
/// <param name="CpuPercent">该进程本区间 CPU 占用（已按设置归一化）。</param>
/// <param name="WorkingSetBytes">工作集字节数。</param>
/// <param name="PrivateBytes">私有字节数。</param>
public readonly record struct AppProcessInfo(
    int Pid,
    string Name,
    string? ExecutablePath,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes);

/// <summary>一次采样后，按「应用」聚合出的一行数据。</summary>
/// <param name="Key">归并键，与 <see cref="AppIdentity.Key"/> 相同，作为历史与趋势的稳定主键。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="RootPath">应用根目录。</param>
/// <param name="Kind">归并来源。</param>
/// <param name="IconSourcePath">用于取图标的可执行文件路径（通常取成员里占用最大的那个），可为 <c>null</c>。</param>
/// <param name="CpuPercent">应用总 CPU 占用（已按设置归一化：整机百分比 0~100；单核百分比 0~100*核心数）。</param>
/// <param name="WorkingSetBytes">应用总工作集。</param>
/// <param name="PrivateBytes">应用总私有字节。</param>
/// <param name="ProcessCount">成员进程数。</param>
/// <param name="ReadBytesPerSecond">本区间读 IO 速率（字节/秒）。</param>
/// <param name="WriteBytesPerSecond">本区间写 IO 速率（字节/秒）。</param>
/// <param name="Reason">归并依据说明。</param>
/// <param name="Processes">成员进程明细，按 CPU 降序。</param>
public sealed record AppSample(
    string Key,
    string DisplayName,
    string? RootPath,
    AppKind Kind,
    string? IconSourcePath,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    int ProcessCount,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    string Reason,
    AppProcessInfo[] Processes);

/// <summary>一帧完整的监控快照，是 UI 渲染与历史落盘的输入。</summary>
/// <param name="TimestampUtc">采样时刻（UTC）。</param>
/// <param name="SystemCpuPercent">整机 CPU 占用百分比（0~100）。</param>
/// <param name="SystemMemoryPercent">物理内存占用百分比（0~100）。</param>
/// <param name="TotalPhysicalBytes">物理内存总量。</param>
/// <param name="AvailablePhysicalBytes">可用物理内存。</param>
/// <param name="Interval">与上一帧之间的实际间隔（首帧为 <see cref="TimeSpan.Zero"/>）。</param>
/// <param name="SelfCpuPercent">监控器自身 CPU 占比（整机口径），用于自证轻量。</param>
/// <param name="SelfWorkingSetBytes">监控器自身工作集。</param>
/// <param name="ProcessCount">本帧统计到的进程总数。</param>
/// <param name="Apps">按 CPU 降序排列的应用聚合结果。</param>
public sealed record SnapshotFrame(
    DateTime TimestampUtc,
    double SystemCpuPercent,
    double SystemMemoryPercent,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    TimeSpan Interval,
    double SelfCpuPercent,
    long SelfWorkingSetBytes,
    int ProcessCount,
    AppSample[] Apps);

/// <summary>趋势曲线上的一个点。</summary>
public readonly record struct TrendPoint(DateTime TimestampUtc, double CpuPercent, long WorkingSetBytes);

/// <summary>按天聚合的应用用量摘要（用于「历史」页与长期留存）。</summary>
/// <param name="Date">本地日期。</param>
/// <param name="AppKey">应用归并键。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="CpuPercentSeconds">当日累计 CPU（百分比·秒，除以 100 即「CPU 秒」）。</param>
/// <param name="PeakCpuPercent">当日峰值 CPU 百分比。</param>
/// <param name="PeakWorkingSetBytes">当日峰值工作集。</param>
/// <param name="SampleCount">参与聚合的采样点数。</param>
public sealed record DailyAppSummary(
    DateOnly Date,
    string AppKey,
    string DisplayName,
    double CpuPercentSeconds,
    double PeakCpuPercent,
    long PeakWorkingSetBytes,
    int SampleCount);
