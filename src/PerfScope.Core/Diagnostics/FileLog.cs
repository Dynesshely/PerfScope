using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Settings;

namespace PerfScope.Core.Diagnostics;

/// <summary>
/// 极简文件日志实现：内部无锁队列 + 后台刷盘线程，写入端绝不被磁盘 IO 阻塞。
/// <para>
/// 触发落盘的时机：后台线程每 2 秒唤醒一次，或待写条数累积到 256 条。
/// <see cref="Dispose"/> 会同步刷完队列中剩余日志。
/// </para>
/// <para>
/// 轮转策略：当前文件超过 <c>maxKilobytes</c> 时改名成 <c>.1</c>，旧 <c>.1</c> 直接删除，
/// 因此磁盘占用最多约为上限的两倍。任何异常都在内部吞掉，绝不向调用方抛出。
/// </para>
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    /// <summary>待写条数超过该值时立即唤醒后台线程落盘。</summary>
    private const int AutoFlushCount = 256;

    /// <summary>后台线程最长休眠时间（毫秒）。</summary>
    private const int AutoFlushIntervalMs = 2000;

    /// <summary>队列上限，防止磁盘长期写入失败时内存无界增长。</summary>
    private const int MaxQueuedLines = 50000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly object _ioLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread? _worker;
    private readonly string _path;
    private readonly LogLevel _minLevel;
    private readonly long _maxBytes;

    private long _currentSize = -1;
    private FileStream? _stream;
    private int _pending;
    private volatile bool _disposed;

    /// <summary>
    /// 创建文件日志。
    /// </summary>
    /// <param name="path">日志文件完整路径，所在目录会自动创建。</param>
    /// <param name="minLevel">最低记录级别（<see cref="LogLevel.Error"/> 最严重）；级别数值大于该值的日志会被丢弃。</param>
    /// <param name="maxKilobytes">单个日志文件大小上限（KB），小于 1 时按 1 处理。</param>
    public FileLog(string path, LogLevel minLevel, int maxKilobytes)
    {
        _path = ResolvePath(path);
        _minLevel = minLevel;
        _maxBytes = Math.Max(1, maxKilobytes) * 1024L;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PerfScope.FileLog",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>把传入路径解析成绝对路径；非法路径回退到临时目录（构造函数同样不抛异常）。</summary>
    private static string ResolvePath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Path.GetTempPath(), "perfscope-fallback.log")
                : Path.GetFullPath(path);
        }
        catch
        {
            try
            {
                return Path.Combine(Path.GetTempPath(), "perfscope-fallback.log");
            }
            catch
            {
                return "perfscope-fallback.log";
            }
        }
    }

    /// <summary>日志文件完整路径。</summary>
    public string FilePath => _path;

    /// <summary>当前生效的最低记录级别。</summary>
    public LogLevel MinLevel => _minLevel;

    /// <inheritdoc />
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (_disposed || level > _minLevel)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _pending) > MaxQueuedLines)
            {
                return;
            }

            _queue.Enqueue(FormatLine(level, message, exception));
            if (Interlocked.Increment(ref _pending) >= AutoFlushCount)
            {
                _wake.Set();
            }
        }
        catch
        {
            // 日志自身永不抛异常。
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        try
        {
            lock (_ioLock)
            {
                DrainLocked();
                _stream?.Flush();
            }
        }
        catch
        {
            // 落盘失败不影响调用方。
        }
    }

    /// <summary>停止后台线程并把剩余日志全部刷到磁盘。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _wake.Set();
            if (_worker is not null && _worker != Thread.CurrentThread)
            {
                _worker.Join(TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
            // 忽略。
        }

        try
        {
            lock (_ioLock)
            {
                DrainLocked();
                _stream?.Flush();
                _stream?.Dispose();
                _stream = null;
            }
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _wake.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>后台刷盘循环。</summary>
    private void WorkerLoop()
    {
        while (!_disposed)
        {
            try
            {
                _wake.Wait(AutoFlushIntervalMs);
                _wake.Reset();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // 等待异常也不终止线程。
            }

            if (_disposed)
            {
                return;
            }

            try
            {
                lock (_ioLock)
                {
                    DrainLocked();
                    _stream?.Flush();
                }
            }
            catch
            {
                // 单次落盘失败不影响后续。
            }
        }
    }

    /// <summary>把队列里的日志一次性追加到文件（调用方必须持有 <see cref="_ioLock"/>）。</summary>
    private void DrainLocked()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        var builder = new StringBuilder(4096);
        while (_queue.TryDequeue(out var line))
        {
            builder.Append(line).Append("\r\n");
        }

        Volatile.Write(ref _pending, 0);
        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            var bytes = Utf8NoBom.GetBytes(builder.ToString());
            if (!EnsureStreamLocked())
            {
                return;
            }

            if (_currentSize > 0 && _currentSize + bytes.Length > _maxBytes)
            {
                RotateLocked();
            }

            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _currentSize += bytes.Length;

            if (_currentSize >= _maxBytes)
            {
                RotateLocked();
            }
        }
        catch
        {
            // 磁盘满 / 文件被占用等：丢弃本批，下次再试，绝不抛出。
            CloseStreamLocked();
        }
    }

    /// <summary>确保文件流已打开（调用方必须持有 <see cref="_ioLock"/>）。</summary>
    private bool EnsureStreamLocked()
    {
        if (_stream is not null)
        {
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.None);
            _currentSize = _stream.Length;
            return true;
        }
        catch
        {
            _stream = null;
            _currentSize = -1;
            return false;
        }
    }

    /// <summary>轮转：当前文件改名 <c>.1</c>，删除旧 <c>.1</c> 后重新打开空文件。</summary>
    private void RotateLocked()
    {
        try
        {
            CloseStreamLocked();

            var rotated = _path + ".1";
            try
            {
                if (File.Exists(rotated))
                {
                    File.Delete(rotated);
                }
            }
            catch
            {
                // 旧轮转文件删不掉就直接覆盖。
            }

            try
            {
                if (File.Exists(_path))
                {
                    File.Move(_path, rotated, overwrite: true);
                }
            }
            catch
            {
                // 移动失败时退化为清空当前文件。
                try
                {
                    File.WriteAllBytes(_path, Array.Empty<byte>());
                }
                catch
                {
                    // 忽略。
                }
            }

            _currentSize = -1;

            // 立刻建立新的空文件：保证日志路径始终存在（UI「打开日志」不会扑空）。
            EnsureStreamLocked();
        }
        catch
        {
            CloseStreamLocked();
        }
    }

    /// <summary>关闭文件流（调用方必须持有 <see cref="_ioLock"/>）。</summary>
    private void CloseStreamLocked()
    {
        try
        {
            _stream?.Flush();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        _stream = null;
        _currentSize = -1;
    }

    /// <summary>按契约格式拼一行日志：<c>2026-01-01 12:00:00.000 [WARN] 消息</c>，异常另起行附堆栈。</summary>
    private static string FormatLine(LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder(128);
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelTag(level)).Append("] ");
        builder.Append(message ?? string.Empty);

        if (exception is not null)
        {
            builder.Append("\r\n").Append(exception);
        }

        return builder.ToString();
    }

    /// <summary>级别标签（固定英文缩写，方便用 grep 检索）。</summary>
    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        LogLevel.Debug => "DEBUG",
        _ => "INFO",
    };
}
