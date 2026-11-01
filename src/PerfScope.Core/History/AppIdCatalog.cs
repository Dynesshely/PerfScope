using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfScope.Core.Abstractions;

namespace PerfScope.Core.History;

/// <summary>
/// <c>history\apps.json</c> 目录表：把应用的稳定归并键（<see cref="Models.AppIdentity.Key"/>）
/// 映射成磁盘记录里的 32 位整数 ID，并记住展示名。
/// <para>新应用追加到内存后由落盘线程统一原子写回，调用方（采样线程）不会被磁盘 IO 阻塞。</para>
/// </summary>
internal sealed class AppIdCatalog
{
    /// <summary>目录表条目上限，防止多年运行后文件无限膨胀。</summary>
    private const int MaxEntries = 20000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILog? _log;
    private readonly Dictionary<string, CatalogEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, CatalogEntry> _byId = new();

    private int _nextId = 1;
    private bool _dirty;
    private bool _overflowLogged;

    /// <summary>加载（或初始化）目录表。</summary>
    public AppIdCatalog(string path, ILog? log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>目录表文件完整路径。</summary>
    public string Path => _path;

    /// <summary>目录表条目数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>取应用 ID；不存在则分配新 ID（只改内存，标脏等待落盘）。</summary>
    /// <returns>分配到的 ID；返回 0 表示目录表已满，本次不记录。</returns>
    public int GetOrAdd(string key, string displayName)
    {
        if (string.IsNullOrEmpty(key))
        {
            return 0;
        }

        var name = displayName ?? string.Empty;

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                if (name.Length > 0 && !string.Equals(existing.DisplayName, name, StringComparison.Ordinal))
                {
                    existing.DisplayName = name;
                    _dirty = true;
                }

                return existing.Id;
            }

            if (_byKey.Count >= MaxEntries)
            {
                if (!_overflowLogged)
                {
                    _overflowLogged = true;
                    _log?.Warn($"应用目录表已达 {MaxEntries} 条上限，新应用将不再写入磁盘历史。");
                }

                return 0;
            }

            var entry = new CatalogEntry(_nextId++, key, name, DateTime.UtcNow.Ticks);
            _byKey[key] = entry;
            _byId[entry.Id] = entry;
            _dirty = true;
            return entry.Id;
        }
    }

    /// <summary>按 ID 反查归并键与展示名。</summary>
    public bool TryGet(int id, out string key, out string displayName)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var entry))
            {
                key = entry.Key;
                displayName = entry.DisplayName;
                return true;
            }
        }

        key = string.Empty;
        displayName = string.Empty;
        return false;
    }

    /// <summary>若目录表有变化则原子写回文件；失败只记日志并保持脏标记。</summary>
    public void SaveIfDirty()
    {
        List<CatalogEntry> snapshot;
        int nextId;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            snapshot = new List<CatalogEntry>(_byKey.Count);
            snapshot.AddRange(_byId.Values);
            nextId = _nextId;
            _dirty = false;
        }

        try
        {
            var file = new CatalogFile { Version = 1, NextId = nextId, Apps = new List<CatalogEntryFile>(snapshot.Count) };
            foreach (var entry in snapshot)
            {
                file.Apps.Add(new CatalogEntryFile
                {
                    Id = entry.Id,
                    Key = entry.Key,
                    DisplayName = entry.DisplayName,
                    FirstSeenUtcTicks = entry.FirstSeenUtcTicks,
                });
            }

            file.Apps.Sort(static (a, b) => a.Id.CompareTo(b.Id));

            var json = JsonSerializer.Serialize(file, JsonOptions);
            WriteAtomic(_path, json);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _dirty = true;
            }

            _log?.Warn($"写入应用目录表失败：{_path}", ex);
        }
    }

    /// <summary>读取目录表；文件损坏时备份后从空表开始。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions);
            if (file?.Apps is null)
            {
                return;
            }

            foreach (var item in file.Apps)
            {
                if (item is null || item.Id <= 0 || string.IsNullOrEmpty(item.Key))
                {
                    continue;
                }

                var entry = new CatalogEntry(item.Id, item.Key, item.DisplayName ?? string.Empty, item.FirstSeenUtcTicks);
                _byId[entry.Id] = entry;
                _byKey[entry.Key] = entry;
            }

            _nextId = Math.Max(file.NextId, _byId.Count == 0 ? 1 : _byId.Keys.Max() + 1);
        }
        catch (Exception ex)
        {
            _log?.Warn("读取应用目录表失败，将从空表开始（原文件已备份）。", ex);
            BackupCorruptFile();
            _byKey.Clear();
            _byId.Clear();
            _nextId = 1;
        }
    }

    /// <summary>把损坏的目录表改名备份，避免每次启动反复失败。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            File.Move(_path, _path + ".bad-" + stamp, overwrite: true);
        }
        catch
        {
            // 备份失败不影响主流程。
        }
    }

    /// <summary>原子写入（临时文件 + 替换）。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var directory = System.IO.Path.GetDirectoryName(path) ?? throw new IOException("应用目录表路径没有目录部分。");
        Directory.CreateDirectory(directory);

        var tempPath = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8NoBom, 4096, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 忽略。
            }
        }
    }

    /// <summary>内存中的目录表条目。</summary>
    private sealed class CatalogEntry
    {
        public CatalogEntry(int id, string key, string displayName, long firstSeenUtcTicks)
        {
            Id = id;
            Key = key;
            DisplayName = displayName;
            FirstSeenUtcTicks = firstSeenUtcTicks;
        }

        public int Id { get; }

        public string Key { get; }

        public string DisplayName { get; set; }

        public long FirstSeenUtcTicks { get; }
    }

    /// <summary>apps.json 根对象。</summary>
    private sealed class CatalogFile
    {
        public int Version { get; set; } = 1;

        public int NextId { get; set; } = 1;

        public List<CatalogEntryFile> Apps { get; set; } = new();
    }

    /// <summary>apps.json 中单条应用的序列化形状。</summary>
    private sealed class CatalogEntryFile
    {
        public int Id { get; set; }

        public string Key { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public long FirstSeenUtcTicks { get; set; }
    }
}
