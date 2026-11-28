using PerfScope.Core;

namespace PerfScope.App.Infrastructure;

/// <summary>崩溃日志：任何未处理异常都追加到数据目录的 logs\crash.log，绝不再抛异常。</summary>
internal static class CrashLog
{
    /// <summary>追加一条崩溃记录（失败静默）。</summary>
    public static void Write(Exception exception)
    {
        try
        {
            string directory = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                + " " + exception + Environment.NewLine);
        }
        catch
        {
            // 日志写入失败时静默
        }
    }
}
