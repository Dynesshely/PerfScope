using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfScope.Core.Abstractions;

namespace PerfScope.Core.Settings;

/// <summary>
/// 设置读写实现：JSON（camelCase、缩进、允许注释与尾逗号、枚举按字符串），
/// 保存时先写同目录临时文件再原子替换，读取时全程容错（损坏文件改名备份后回退默认值）。
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly ILog? _log;
    private readonly string _settingsPath;

    /// <summary>
    /// 创建设置存储。
    /// </summary>
    /// <param name="log">日志接口，可为 <c>null</c>。</param>
    /// <param name="settingsPath">设置文件路径；为 <c>null</c> 时使用 <see cref="AppPaths.SettingsFile"/>。</param>
    public SettingsStore(ILog? log, string? settingsPath = null)
    {
        _log = log;
        _settingsPath = Path.GetFullPath(string.IsNullOrWhiteSpace(settingsPath) ? AppPaths.SettingsFile : settingsPath);

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            TryLogWarn("创建设置目录失败，后续写入可能失败。", ex);
        }
    }

    /// <inheritdoc />
    public string SettingsPath => _settingsPath;

    /// <inheritdoc />
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    var fresh = new AppSettings();
                    TryLog("info", $"设置文件不存在，已写入默认设置：{_settingsPath}");
                    Persist(fresh, isInitial: true);
                    return fresh;
                }

                AppSettings? loaded = null;
                string? json = null;
                try
                {
                    json = ReadAllTextWithRetry(_settingsPath);
                    loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                }
                catch (Exception ex)
                {
                    TryLogWarn("设置文件解析失败，已备份损坏文件并回退默认值。", ex);
                }

                if (loaded is null)
                {
                    var fallback = new AppSettings();
                    BackupCorruptFile();
                    Persist(fallback, isInitial: false);
                    return fallback;
                }

                var fixes = SettingsValidator.Normalize(loaded);
                if (fixes.Count > 0)
                {
                    foreach (var fix in fixes)
                    {
                        TryLog("info", $"设置已修正：{fix}");
                    }

                    // 回写修正后的配置，避免每次启动重复修正。
                    Persist(loaded, isInitial: false);
                }

                return loaded;
            }
            catch (Exception ex)
            {
                TryLogWarn("读取设置失败，返回内存默认值（未落盘）。", ex);
                return new AppSettings();
            }
        }
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            Persist(settings, isInitial: false);
        }
    }

    /// <summary>克隆 → 校验 → 原子写盘。失败只记日志，不抛异常。</summary>
    private void Persist(AppSettings settings, bool isInitial)
    {
        try
        {
            var snapshot = settings.Clone();
            var fixes = SettingsValidator.Normalize(snapshot);
            foreach (var fix in fixes)
            {
                TryLog("info", $"保存前修正设置：{fix}");
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            WriteAllTextAtomic(_settingsPath, json);
            TryLog("debug", isInitial ? "已写入初始设置文件。" : "设置已保存。");
        }
        catch (Exception ex)
        {
            TryLogWarn("保存设置失败（本次修改只在内存生效）。", ex);
        }
    }

    /// <summary>把损坏的设置文件改名成 <c>settings.json.bad-yyyyMMdd-HHmmssfff</c>，便于事后排查。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            var badPath = _settingsPath + ".bad-" + stamp;
            File.Move(_settingsPath, badPath, overwrite: true);
            TryLogWarn($"损坏的设置文件已备份为：{badPath}", null);
        }
        catch (Exception ex)
        {
            TryLogWarn("备份损坏的设置文件失败。", ex);
        }
    }

    /// <summary>读取文本，遇到共享冲突时重试一次。</summary>
    private static string ReadAllTextWithRetry(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            Thread.Sleep(80);
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }

    /// <summary>原子写入：同目录临时文件 → <see cref="File.Replace(string,string,string?)"/>（不支持时退化为覆盖移动）。</summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("设置路径没有目录部分。");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);

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
                // 清理临时文件失败无所谓。
            }
        }
    }

    /// <summary>构造 JSON 选项：camelCase、缩进、允许注释与尾逗号、枚举按字符串、属性名大小写不敏感。</summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>日志快捷包装：日志实现自身异常绝不影响设置读写。</summary>
    private void TryLog(string level, string message)
    {
        try
        {
            _log?.Write(
                level switch
                {
                    "debug" => LogLevel.Debug,
                    "info" => LogLevel.Info,
                    "warn" => LogLevel.Warn,
                    _ => LogLevel.Error,
                },
                message);
        }
        catch
        {
            // 忽略。
        }
    }

    private void TryLogWarn(string message, Exception? exception)
    {
        try
        {
            _log?.Warn(message, exception);
        }
        catch
        {
            // 忽略。
        }
    }
}
