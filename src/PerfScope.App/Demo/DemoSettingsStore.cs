using PerfScope.Core.Abstractions;
using PerfScope.Core.Settings;

namespace PerfScope.App.Demo;

/// <summary>
/// 演示用设置存储：只在内存里保存，绝不写真实 settings.json，
/// 避免演示模式覆盖用户配置。设置页的「保存」依旧可以验证热生效链路。
/// </summary>
internal sealed class DemoSettingsStore : ISettingsStore
{
    private readonly object _sync = new();
    private AppSettings _settings;

    /// <summary>创建演示设置存储。</summary>
    public DemoSettingsStore(AppSettings settings)
    {
        _settings = settings.Clone();
    }

    /// <inheritdoc />
    public string SettingsPath => Path.Combine(
        PerfScope.Core.AppPaths.DataDirectory,
        "settings.demo.json（演示模式，不落盘）");

    /// <inheritdoc />
    public AppSettings Load()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            _settings = settings.Clone();
        }
    }
}
