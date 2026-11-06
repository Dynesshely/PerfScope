using System.Text;
using System.Text.Json;
using PerfScope.Core.Abstractions;

namespace PerfScope.Core.Insights;

/// <summary>
/// 分析事件的持久化：按天一个 JSONL 文件（每行一个 JSON 对象），追加写、无需重写、崩溃只丢最后一行。
/// </summary>
public sealed class InsightStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // 中文不转义：JSONL 直接给人/AI 读，转义成 \uXXXX 既难读也更大
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly ILog? _log;
    private StreamWriter? _writer;
    private DateOnly _writerDay;
    private bool _disposed;

    /// <summary>创建存储。</summary>
    public InsightStore(string? rootDirectory = null, ILog? log = null)
    {
        _directory = Path.Combine(rootDirectory ?? AppPaths.DataDirectory, "insights");
        _log = log;
    }

    /// <summary>事件目录。</summary>
    public string DirectoryPath => _directory;

    /// <summary>追加一批事件（线程安全，内部按交易日切文件）。</summary>
    public void Append(IEnumerable<InsightEvent> events)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                foreach (var insight in events)
                {
                    var day = DateOnly.FromDateTime(insight.TimestampUtc.ToLocalTime());
                    var writer = EnsureWriter(day);
                    writer.WriteLine(JsonSerializer.Serialize(insight, JsonOptions));
                }

                _writer?.Flush();
            }
            catch (Exception ex)
            {
                _log?.Warn("写入分析事件失败", ex);
            }
        }
    }

    /// <summary>读取某天的全部事件（坏行直接跳过）。</summary>
    public IReadOnlyList<InsightEvent> LoadDay(DateOnly day)
    {
        var path = FileFor(day);
        if (!File.Exists(path))
        {
            return Array.Empty<InsightEvent>();
        }

        var list = new List<InsightEvent>();
        try
        {
            // 必须用 FileShare.ReadWrite：采样进程正在追加写这个文件，
            // 用默认的 FileShare.Read 打开会因为共享模式冲突而失败。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var insight = JsonSerializer.Deserialize<InsightEvent>(line, JsonOptions);
                    if (insight is not null)
                    {
                        list.Add(insight);
                    }
                }
                catch
                {
                    // 单行损坏（例如正在写入的最后一行）不影响其它事件
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("读取分析事件失败", ex);
        }

        return list;
    }

    /// <summary>已存在的分析日期（升序）。</summary>
    public IReadOnlyList<DateOnly> AvailableDays()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return Array.Empty<DateOnly>();
        }

        return System.IO.Directory.EnumerateFiles(_directory, "*.jsonl")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day) ? day : (DateOnly?)null)
            .Where(day => day.HasValue)
            .Select(day => day!.Value)
            .OrderBy(day => day)
            .ToArray();
    }

    /// <summary>清理超过保留天数的文件。</summary>
    public void Trim(int retentionDays)
    {
        if (retentionDays <= 0 || !System.IO.Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-retentionDays);
        foreach (var day in AvailableDays())
        {
            if (day >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(FileFor(day));
            }
            catch (Exception ex)
            {
                _log?.Warn("清理过期分析文件失败：" + FileFor(day), ex);
            }
        }
    }

    /// <summary>刷新缓冲。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _writer = null;
        }
    }

    private string FileFor(DateOnly day) => Path.Combine(_directory, day.ToString("yyyy-MM-dd") + ".jsonl");

    private StreamWriter EnsureWriter(DateOnly day)
    {
        if (_writer is not null && _writerDay == day)
        {
            return _writer;
        }

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // 忽略
        }

        System.IO.Directory.CreateDirectory(_directory);
        var stream = new FileStream(FileFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _writerDay = day;
        return _writer;
    }
}
