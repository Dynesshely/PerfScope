using System.Runtime.InteropServices;

namespace PerfScope.Core.Native;

/// <summary>
/// 进程映像全路径的缓存器：键为 <c>(pid, CreateTimeUtcTicks)</c>，因此 PID 复用不会串味。
/// </summary>
/// <remarks>
/// <para>只有「尚未缓存」的进程才会走 <c>OpenProcess + QueryFullProcessImageNameW</c>，
/// 稳定状态下每帧 0 次系统调用（这是自身开销低的关键之一）。</para>
/// <para>拿不到路径的进程（受保护进程、已退出进程）做 10 秒负缓存，到期后重试，
/// 避免每帧对同一批系统进程反复 OpenProcess 失败。</para>
/// <para>条目上限 4096，超出后淘汰「本帧没再出现的 PID」（进程已消失）。</para>
/// <para>本类不做加锁，由 <see cref="NativeCapture"/> 在其采集锁内串行使用。</para>
/// </remarks>
internal sealed class ProcessPathResolver
{
    /// <summary>负缓存有效时长（毫秒）。</summary>
    internal const int NegativeCacheMilliseconds = 10_000;

    /// <summary>缓存条目上限。</summary>
    internal const int MaxEntries = 4096;

    private readonly Dictionary<(int Pid, long CreateTicks), Entry> _cache = new();
    private readonly List<(int Pid, long CreateTicks)> _pruneScratch = new();

    private struct Entry
    {
        /// <summary>拿到的全路径；<c>null</c> 表示查询失败（负缓存）。</summary>
        public string? Path;

        /// <summary>负缓存的到期时间（<see cref="Environment.TickCount64"/>，毫秒）。</summary>
        public long RetryTimestamp;

        /// <summary>最后一次在采样中出现的帧号，用于淘汰已消失的 PID。</summary>
        public int LastSeenFrame;
    }

    /// <summary>缓存中当前的条目数量（诊断用）。</summary>
    internal int Count => _cache.Count;

    /// <summary>
    /// 取进程映像全路径。缓存未命中时才做一次原生查询。
    /// </summary>
    /// <param name="pid">进程 ID。</param>
    /// <param name="createTicks">创建时间（UTC ticks），用于识别 PID 复用。</param>
    /// <param name="frame">当前采样帧号。</param>
    /// <param name="pathBuffer">复用的字符缓冲区，缓冲不足时会被替换为更大的数组。</param>
    /// <returns>全路径；拿不到时返回 <c>null</c>。</returns>
    public string? Resolve(int pid, long createTicks, int frame, ref char[] pathBuffer)
    {
        (int Pid, long CreateTicks) key = (pid, createTicks);

        if (_cache.TryGetValue(key, out Entry entry))
        {
            if (entry.Path is not null)
            {
                entry.LastSeenFrame = frame;
                _cache[key] = entry;
                return entry.Path;
            }

            if (Environment.TickCount64 < entry.RetryTimestamp)
            {
                entry.LastSeenFrame = frame;
                _cache[key] = entry;
                return null;
            }
        }

        string? path = Query(pid, ref pathBuffer);
        _cache[key] = new Entry
        {
            Path = path,
            RetryTimestamp = path is null ? Environment.TickCount64 + NegativeCacheMilliseconds : 0,
            LastSeenFrame = frame,
        };

        if (_cache.Count > MaxEntries)
        {
            TrimOverflow(frame);
        }

        return path;
    }

    /// <summary>一帧解析结束后调用：条目超上限时淘汰本帧已消失的进程。</summary>
    /// <param name="frame">当前采样帧号。</param>
    public void Trim(int frame)
    {
        if (_cache.Count > MaxEntries)
        {
            TrimOverflow(frame);
        }
    }

    /// <summary>清空缓存（测试与调试用）。</summary>
    public void Clear() => _cache.Clear();

    private void TrimOverflow(int frame)
    {
        _pruneScratch.Clear();
        foreach (KeyValuePair<(int Pid, long CreateTicks), Entry> pair in _cache)
        {
            if (pair.Value.LastSeenFrame < frame)
            {
                _pruneScratch.Add(pair.Key);
            }
        }

        foreach ((int Pid, long CreateTicks) key in _pruneScratch)
        {
            _cache.Remove(key);
        }

        _pruneScratch.Clear();
    }

    /// <summary>OpenProcess + QueryFullProcessImageNameW，永不抛出。</summary>
    private static string? Query(int pid, ref char[] pathBuffer)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            while (true)
            {
                int size = pathBuffer.Length;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, pathBuffer, ref size))
                {
                    return size <= 0 ? null : new string(pathBuffer, 0, size);
                }

                if (Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer)
                {
                    return null;
                }

                if (pathBuffer.Length >= NativeMethods.MaxPathChars)
                {
                    return null;
                }

                int nextSize = Math.Min(pathBuffer.Length * 2, NativeMethods.MaxPathChars);
                pathBuffer = new char[nextSize];
            }
        }
        catch (Exception)
        {
            // 原生查询失败不应影响整帧采集：当作「拿不到路径」处理。
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
