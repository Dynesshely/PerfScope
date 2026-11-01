using PerfScope.Core.Grouping;

namespace PerfScope.Tests.Grouping;

/// <summary>
/// 完全内存的假文件系统：不依赖本机任何真实路径，用来给归并引擎写确定性单测。
/// </summary>
internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExeVersionInfo> _versions = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystemProbe(
        string windowsDirectory = @"C:\Windows",
        string programFiles = @"C:\Program Files",
        string programFilesX86 = @"C:\Program Files (x86)",
        string programData = @"C:\ProgramData",
        string localAppData = @"C:\Users\Test\AppData\Local",
        string roamingAppData = @"C:\Users\Test\AppData\Roaming")
    {
        WindowsDirectory = Normalize(windowsDirectory);
        ProgramFilesDirectory = Normalize(programFiles);
        ProgramFilesX86Directory = Normalize(programFilesX86);
        ProgramDataDirectory = Normalize(programData);
        LocalAppDataDirectory = Normalize(localAppData);
        RoamingAppDataDirectory = Normalize(roamingAppData);

        foreach (var root in new[] { WindowsDirectory, ProgramFilesDirectory, ProgramFilesX86Directory, ProgramDataDirectory, LocalAppDataDirectory, RoamingAppDataDirectory })
        {
            AddDirectory(root);
        }
    }

    public string? WindowsDirectory { get; }

    public string? ProgramFilesDirectory { get; }

    public string? ProgramFilesX86Directory { get; }

    public string? ProgramDataDirectory { get; }

    public string? LocalAppDataDirectory { get; }

    public string? RoamingAppDataDirectory { get; }

    /// <summary>版本信息被读取的次数（用于验证缓存是否生效）。</summary>
    public int VersionReadCount { get; private set; }

    /// <summary>目录被枚举的次数。</summary>
    public int DirectoryEnumerationCount { get; private set; }

    /// <summary>登记一个文件，并补齐其所有祖先目录。</summary>
    public void AddFile(string path)
    {
        var normalized = Normalize(path);
        _files.Add(normalized);
        AddDirectory(ParentOf(normalized));
    }

    /// <summary>登记一个文件并附带版本信息。</summary>
    public void AddExe(string path, string? productName = null, string? companyName = null, string? description = null)
    {
        AddFile(path);
        if (productName is null && companyName is null && description is null)
        {
            return;
        }

        _versions[Normalize(path)] = new ExeVersionInfo(
            productName,
            companyName,
            description,
            Path.GetFileName(path),
            null);
    }

    /// <summary>登记一个目录，并补齐其所有祖先目录。</summary>
    public void AddDirectory(string? path)
    {
        var current = Normalize(path);
        while (current.Length > 0)
        {
            if (!_directories.Add(current))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            current = Normalize(parent);
        }
    }

    public bool FileExists(string? path) => _files.Contains(Normalize(path));

    public bool DirectoryExists(string? path) => _directories.Contains(Normalize(path));

    public IReadOnlyList<string> EnumerateFileNames(string directory)
    {
        DirectoryEnumerationCount++;
        var normalized = Normalize(directory);
        var names = new List<string>();
        foreach (var file in _files)
        {
            if (string.Equals(ParentOf(file), normalized, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileName(file));
            }
        }

        return names;
    }

    public IReadOnlyList<string> EnumerateDirectoryNames(string directory)
    {
        DirectoryEnumerationCount++;
        var normalized = Normalize(directory);
        var names = new List<string>();
        foreach (var child in _directories)
        {
            if (string.Equals(ParentOf(child), normalized, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileName(child));
            }
        }

        return names;
    }

    public ExeVersionInfo? ReadVersionInfo(string path)
    {
        VersionReadCount++;
        return _versions.TryGetValue(Normalize(path), out var info) ? info : null;
    }

    private static string ParentOf(string normalizedPath)
    {
        var parent = Path.GetDirectoryName(normalizedPath);
        return string.IsNullOrEmpty(parent) ? string.Empty : Normalize(parent);
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var text = path!.Replace('/', '\\').Trim();
        if (text.Length > 3)
        {
            text = text.TrimEnd('\\');
        }

        return text;
    }
}
