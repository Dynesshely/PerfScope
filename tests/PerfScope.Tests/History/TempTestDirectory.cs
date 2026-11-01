namespace PerfScope.Tests.Support;

/// <summary>
/// 临时的测试目录：自动创建在系统临时目录下，<see cref="Dispose"/> 时递归删除。
/// 每个测试用独立目录，避免并行执行时互相干扰。
/// </summary>
internal sealed class TempTestDirectory : IDisposable
{
    /// <summary>创建临时目录。</summary>
    /// <remarks>
    /// 不直接用 Path.GetTempPath()：在本机受限的执行环境里，子进程对用户临时目录的写入可能被拒绝
    /// （UnauthorizedAccessException）。默认落在测试程序集输出目录下的 test-temp，可用环境变量
    /// PERFSCOPE_TEST_TEMP 覆盖。
    /// </remarks>
    public TempTestDirectory()
    {
        Root = System.IO.Path.Combine(ResolveBaseDirectory(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    private static string ResolveBaseDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("PERFSCOPE_TEST_TEMP");
        var root = string.IsNullOrWhiteSpace(fromEnvironment)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "test-temp")
            : fromEnvironment;
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>临时目录绝对路径。</summary>
    public string Root { get; }

    /// <summary>拼接临时目录下的路径。</summary>
    public string Combine(params string[] parts)
    {
        if (parts.Length == 0)
        {
            return Root;
        }

        var segments = new string[parts.Length + 1];
        segments[0] = Root;
        Array.Copy(parts, 0, segments, 1, parts.Length);
        return System.IO.Path.Combine(segments);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
    }
}
