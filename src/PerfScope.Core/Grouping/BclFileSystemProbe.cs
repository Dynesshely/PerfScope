using System.Diagnostics;

namespace PerfScope.Core.Grouping;

/// <summary>
/// <see cref="IFileSystemProbe"/> 的真实实现：只使用 BCL（不引入第三方包）。
/// 所有 IO 都包在 try/catch 里 —— 归并引擎在后台线程跑，任何一次读盘失败都不能拖垮采样循环。
/// </summary>
public sealed class BclFileSystemProbe : IFileSystemProbe
{
    private static readonly string? WindowsDir = GetFolder(Environment.SpecialFolder.Windows);
    private static readonly string? ProgramFilesDir = GetFolder(Environment.SpecialFolder.ProgramFiles);
    private static readonly string? ProgramFilesX86Dir = GetFolder(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string? ProgramDataDir = GetFolder(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string? LocalAppDataDir = GetFolder(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string? RoamingAppDataDir = GetFolder(Environment.SpecialFolder.ApplicationData);

    /// <inheritdoc />
    public string? WindowsDirectory => WindowsDir;

    /// <inheritdoc />
    public string? ProgramFilesDirectory => ProgramFilesDir;

    /// <inheritdoc />
    public string? ProgramFilesX86Directory => ProgramFilesX86Dir;

    /// <inheritdoc />
    public string? ProgramDataDirectory => ProgramDataDir;

    /// <inheritdoc />
    public string? LocalAppDataDirectory => LocalAppDataDir;

    /// <inheritdoc />
    public string? RoamingAppDataDirectory => RoamingAppDataDir;

    /// <inheritdoc />
    public bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFileNames(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var entries = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            var names = new List<string>();
            foreach (var entry in entries)
            {
                names.Add(Path.GetFileName(entry));
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectoryNames(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var entries = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            var names = new List<string>();
            foreach (var entry in entries)
            {
                names.Add(Path.GetFileName(entry));
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public ExeVersionInfo? ReadVersionInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new ExeVersionInfo(
                info.ProductName,
                info.CompanyName,
                info.FileDescription,
                info.OriginalFilename,
                info.FileVersion);
        }
        catch
        {
            // 文件不存在 / 无权限 / 不是 PE 文件 —— 一律视为没有版本信息。
            return null;
        }
    }

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var value = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
