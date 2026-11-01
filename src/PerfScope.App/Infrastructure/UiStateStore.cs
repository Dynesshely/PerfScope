using System.Text.Json;

namespace PerfScope.App.Infrastructure;

/// <summary>界面状态（窗口位置大小、排序列），保存在 <c>%LOCALAPPDATA%\PerfScope\ui.json</c>。</summary>
internal sealed class UiState
{
    /// <summary>窗口左上角 X（<see cref="int.MinValue"/> 表示未记录）。</summary>
    public int X { get; set; } = int.MinValue;

    /// <summary>窗口左上角 Y。</summary>
    public int Y { get; set; } = int.MinValue;

    /// <summary>窗口宽度（0 表示未记录）。</summary>
    public int Width { get; set; }

    /// <summary>窗口高度。</summary>
    public int Height { get; set; }

    /// <summary>退出时是否最大化。</summary>
    public bool Maximized { get; set; }

    /// <summary>排序列：<c>name</c> / <c>cpu</c> / <c>memory</c> / <c>process</c> / <c>io</c>。</summary>
    public string SortColumn { get; set; } = "cpu";

    /// <summary>是否降序。</summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>主窗口当前是否处于隐藏（托盘）状态。</summary>
    public bool HiddenToTray { get; set; }

    /// <summary>左侧应用列表占内容区的宽度比例（0.2~0.85），默认 0.62。</summary>
    public double SplitRatio { get; set; } = 0.62;

    /// <summary>右侧分布视图是否显示。</summary>
    public bool TreemapVisible { get; set; } = true;

    /// <summary>右侧分布视图当前标签：0 = 内存分布，1 = CPU 分布。</summary>
    public int DistributionTab { get; set; }

}

/// <summary>界面状态的读写，全部容错：任何异常都不允许影响主流程。</summary>
internal static class UiStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>状态文件完整路径。</summary>
    public static string FilePath => Path.Combine(PerfScope.Core.AppPaths.DataDirectory, "ui.json");

    /// <summary>读取状态，失败返回默认值。</summary>
    public static UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var state = JsonSerializer.Deserialize<UiState>(json, Options);
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch
        {
            // 状态文件损坏时静默回退默认值
        }

        return new UiState();
    }

    /// <summary>保存状态，失败静默。</summary>
    public static void Save(UiState state)
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? PerfScope.Core.AppPaths.DataDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // 忽略：界面状态属于锦上添花
        }
    }
}