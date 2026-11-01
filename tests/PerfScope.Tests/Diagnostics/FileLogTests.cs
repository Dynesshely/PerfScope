using PerfScope.Core.Abstractions;
using PerfScope.Core.Diagnostics;
using PerfScope.Core.Settings;
using PerfScope.Tests.Support;

namespace PerfScope.Tests.Diagnostics;

/// <summary><see cref="FileLog"/> 测试：级别过滤、轮转、并发写入、释放前刷盘、异常堆栈。</summary>
public sealed class FileLogTests
{
    [Fact]
    public void Write_FiltersByMinLevel()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");

        using (var log = new FileLog(path, LogLevel.Warn, 512))
        {
            log.Error("致命错误");
            log.Warn("一般警告");
            log.Info("普通信息");
            log.Debug("调试信息");
            log.Flush();
        }

        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("[ERROR] 致命错误", lines[0], StringComparison.Ordinal);
        Assert.Contains("[WARN] 一般警告", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("[INFO]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("[DEBUG]", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_UsesContractLayout()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");

        using (var log = new FileLog(path, LogLevel.Debug, 512))
        {
            log.Write(LogLevel.Info, "布局检查", new InvalidOperationException("示例异常"));
            log.Flush();
        }

        var content = File.ReadAllText(path);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] 布局检查", content);
        Assert.Contains("System.InvalidOperationException", content, StringComparison.Ordinal);
        Assert.Contains("示例异常", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RotatesWhenExceedingSizeLimit()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");

        using (var log = new FileLog(path, LogLevel.Debug, 1))
        {
            for (var batch = 0; batch < 40; batch++)
            {
                for (var line = 0; line < 25; line++)
                {
                    log.Debug($"轮转测试 {batch}-{line} 这是一条用于撑大日志文件的填充消息");
                }

                log.Flush();
            }
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"), "超过大小上限后应生成 .1 轮转文件");
        Assert.True(new FileInfo(path).Length <= 2 * 1024, "当前日志文件应保持在大小上限附近");
        Assert.True(new FileInfo(path + ".1").Length > 0);
    }

    [Fact]
    public void Dispose_FlushesEverythingWithoutLosingLines()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");

        var log = new FileLog(path, LogLevel.Debug, 4096);
        for (var i = 0; i < 500; i++)
        {
            log.Debug($"第 {i} 条消息");
        }

        log.Dispose();

        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
        Assert.Equal(500, lines.Length);
        Assert.EndsWith("第 499 条消息", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentWrites_DoNotLoseOrThrow()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");
        const int threads = 8;
        const int perThread = 200;

        using (var log = new FileLog(path, LogLevel.Debug, 4096))
        {
            Parallel.For(0, threads, thread =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    log.Write(LogLevel.Debug, $"并发写入 线程{thread} 序号{i}");
                }
            });
        }

        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
        Assert.Equal(threads * perThread, lines.Length);
        Assert.All(lines, line => Assert.Contains("[DEBUG] 并发写入", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Write_AfterDispose_IsIgnoredAndNeverThrows()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("logs", "perfscope.log");
        var log = new FileLog(path, LogLevel.Debug, 512);
        log.Debug("释放前");
        log.Dispose();

        var exception = Record.Exception(() => log.Error("释放后不应再写入"));
        Assert.Null(exception);
        Assert.DoesNotContain("释放后", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithBadPath_NeverThrows()
    {
        using var temp = new TempTestDirectory();
        var log = new FileLog(temp.Combine("不可写目录名", "x\0y.log"), LogLevel.Debug, 64);
        var exception = Record.Exception(() =>
        {
            log.Info("写不进去也不该抛异常");
            log.Flush();
            log.Dispose();
        });

        Assert.Null(exception);
    }
}
