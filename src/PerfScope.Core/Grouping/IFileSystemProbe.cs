namespace PerfScope.Core.Grouping;

/// <summary>
/// 可执行文件版本信息的轻量投影（只保留归并需要的字段，避免把整个 <c>FileVersionInfo</c> 传出去）。
/// </summary>
/// <param name="ProductName">产品名，例如 <c>Google Chrome</c>；可能为 <c>null</c>。</param>
/// <param name="CompanyName">公司名，例如 <c>Google LLC</c>；可能为 <c>null</c>。</param>
/// <param name="FileDescription">文件说明，例如 <c>Windows Explorer</c>；可能为 <c>null</c>。</param>
/// <param name="OriginalFileName">原始文件名，例如 <c>chrome.exe</c>；可能为 <c>null</c>。</param>
/// <param name="FileVersion">文件版本，可能为 <c>null</c>。</param>
public sealed record ExeVersionInfo(
    string? ProductName,
    string? CompanyName,
    string? FileDescription,
    string? OriginalFileName,
    string? FileVersion)
{
    /// <summary>表示「没有取到任何版本信息」。</summary>
    public static readonly ExeVersionInfo Empty = new(null, null, null, null, null);
}

/// <summary>
/// 归并引擎唯一依赖的外部环境抽象：已知目录 + 文件系统探测 + 版本信息读取。
/// 真实实现见 <see cref="BclFileSystemProbe"/>；单测用假实现即可完全脱离本机真实路径。
/// 所有方法都允许在任意线程调用；实现必须自行吞掉 IO 异常（返回 false / 空集合 / <c>null</c>）。
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>Windows 目录（例如 <c>C:\Windows</c>）；取不到时为 <c>null</c>。</summary>
    string? WindowsDirectory { get; }

    /// <summary>Program Files 目录；取不到时为 <c>null</c>。</summary>
    string? ProgramFilesDirectory { get; }

    /// <summary>Program Files (x86) 目录；32 位系统上可能为 <c>null</c>。</summary>
    string? ProgramFilesX86Directory { get; }

    /// <summary>ProgramData 目录；取不到时为 <c>null</c>。</summary>
    string? ProgramDataDirectory { get; }

    /// <summary><c>%LOCALAPPDATA%</c>；取不到时为 <c>null</c>。</summary>
    string? LocalAppDataDirectory { get; }

    /// <summary><c>%APPDATA%</c>；取不到时为 <c>null</c>。</summary>
    string? RoamingAppDataDirectory { get; }

    /// <summary>文件是否存在。</summary>
    bool FileExists(string? path);

    /// <summary>目录是否存在。</summary>
    bool DirectoryExists(string? path);

    /// <summary>枚举目录下的文件名（不含路径），顺序不保证；目录不存在时返回空集合。</summary>
    IReadOnlyList<string> EnumerateFileNames(string directory);

    /// <summary>枚举目录下的子目录名（不含路径）；目录不存在时返回空集合。</summary>
    IReadOnlyList<string> EnumerateDirectoryNames(string directory);

    /// <summary>读取可执行文件的版本信息；读不到时返回 <c>null</c>。</summary>
    ExeVersionInfo? ReadVersionInfo(string path);
}
