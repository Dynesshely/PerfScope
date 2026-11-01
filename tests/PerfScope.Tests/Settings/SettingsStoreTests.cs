using System.Text;
using System.Text.Json;
using PerfScope.Core.Abstractions;
using PerfScope.Core.Settings;
using PerfScope.Tests.Support;

namespace PerfScope.Tests.Settings;

/// <summary><see cref="SettingsStore"/> 测试：默认值、往返读写、损坏回退、越界钳制、规则去重与原子写入。</summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_WhenFileMissing_WritesDefaultsToDisk()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        var store = new SettingsStore(NullLog.Instance, path);

        var settings = store.Load();

        Assert.True(File.Exists(path));
        Assert.Equal(path, store.SettingsPath);
        Assert.Equal(2000, settings.SampleIntervalMs);
        Assert.Equal(60, settings.MemoryRetentionMinutes);
        Assert.Equal(30, settings.DiskRetentionDays);
        Assert.Equal(LogLevel.Warn, settings.LogLevel);
        Assert.Equal(CloseBehavior.MinimizeToTray, settings.CloseBehavior);

        var json = File.ReadAllText(path);
        Assert.Contains("\"sampleIntervalMs\": 2000", json, StringComparison.Ordinal);
        Assert.Contains("\"cpuMode\": \"machinePercent\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        var store = new SettingsStore(NullLog.Instance, path);

        var original = new AppSettings
        {
            SampleIntervalMs = 3000,
            UiRefreshIntervalMs = 500,
            HiddenSampleIntervalMs = 15000,
            SmoothingWindow = 7,
            CpuMode = CpuNormalization.PerCorePercent,
            MemoryMode = MemoryDisplay.PrivateWorkingSet,
            UnknownGroupName = "未知应用",
            SystemGroupName = "系统",
            MemoryRetentionMinutes = 240,
            DiskRetentionDays = 90,
            TrendAppSlots = 20,
            EnableDiskHistory = false,
            TopN = 50,
            Theme = "light",
            EnableAlerts = true,
            AlertCpuPercent = 70,
            AlertMemoryPercent = 80,
            AlertCooldownSeconds = 120,
            LogLevel = LogLevel.Debug,
            LogMaxKilobytes = 2048,
        };
        original.Rules.Add(new AppRule { Match = AppRuleMatch.ProductName, Pattern = "Visual Studio", Action = AppRuleAction.MergeToName, Target = "VS" });
        original.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "perfscope.exe", Action = AppRuleAction.Exclude });

        store.Save(original);
        var loaded = new SettingsStore(NullLog.Instance, path).Load();

        Assert.Equal(3000, loaded.SampleIntervalMs);
        Assert.Equal(500, loaded.UiRefreshIntervalMs);
        Assert.Equal(15000, loaded.HiddenSampleIntervalMs);
        Assert.Equal(7, loaded.SmoothingWindow);
        Assert.Equal(CpuNormalization.PerCorePercent, loaded.CpuMode);
        Assert.Equal(MemoryDisplay.PrivateWorkingSet, loaded.MemoryMode);
        Assert.Equal("未知应用", loaded.UnknownGroupName);
        Assert.Equal("系统", loaded.SystemGroupName);
        Assert.Equal(240, loaded.MemoryRetentionMinutes);
        Assert.Equal(90, loaded.DiskRetentionDays);
        Assert.Equal(20, loaded.TrendAppSlots);
        Assert.False(loaded.EnableDiskHistory);
        Assert.Equal(50, loaded.TopN);
        Assert.Equal("light", loaded.Theme);
        Assert.True(loaded.EnableAlerts);
        Assert.Equal(70, loaded.AlertCpuPercent);
        Assert.Equal(80, loaded.AlertMemoryPercent);
        Assert.Equal(120, loaded.AlertCooldownSeconds);
        Assert.Equal(LogLevel.Debug, loaded.LogLevel);
        Assert.Equal(2048, loaded.LogMaxKilobytes);
        Assert.Equal(2, loaded.Rules.Count);
        Assert.Equal("VS", loaded.Rules[0].Target);
        Assert.Equal(AppRuleAction.Exclude, loaded.Rules[1].Action);
        Assert.Null(loaded.Rules[1].Target);
    }

    [Fact]
    public void Load_WithCorruptFile_BacksUpAndFallsBackToDefaults()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, "{ 这不是 JSON ", Encoding.UTF8);

        var store = new SettingsStore(NullLog.Instance, path);
        var settings = store.Load();

        Assert.Equal(2000, settings.SampleIntervalMs);

        var backups = Directory.GetFiles(temp.Root, "settings.json.bad-*");
        Assert.Single(backups);
        Assert.Contains("这不是 JSON", File.ReadAllText(backups[0]), StringComparison.Ordinal);

        // 坏文件已被替换成一份合法的默认配置。
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.TryGetProperty("sampleIntervalMs", out _));
    }

    [Fact]
    public void Load_WithTruncatedJson_BacksUpAndFallsBack()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, "{\"sampleIntervalMs\": 2000, \"topN\":");

        var settings = new SettingsStore(NullLog.Instance, path).Load();

        Assert.Equal(2000, settings.SampleIntervalMs);
        Assert.Single(Directory.GetFiles(temp.Root, "settings.json.bad-*"));
    }

    [Fact]
    public void Load_OutOfRangeValues_AreClamped()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, """
            {
              // 手改出来的越界配置
              "sampleIntervalMs": 10,
              "uiRefreshIntervalMs": 999999,
              "memoryRetentionMinutes": -5,
              "diskRetentionDays": 10000,
              "topN": 1,
              "theme": "neon",
              "cpuMode": "perCorePercent",
              "logMaxKilobytes": 5,
            }
            """);

        var settings = new SettingsStore(NullLog.Instance, path).Load();

        Assert.Equal(500, settings.SampleIntervalMs);
        Assert.Equal(10000, settings.UiRefreshIntervalMs);
        Assert.Equal(5, settings.MemoryRetentionMinutes);
        Assert.Equal(365, settings.DiskRetentionDays);
        Assert.Equal(5, settings.TopN);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal(CpuNormalization.PerCorePercent, settings.CpuMode);
        Assert.Equal(64, settings.LogMaxKilobytes);

        // 修正结果已回写磁盘，第二次读取不再需要修正。
        var reloaded = new SettingsStore(NullLog.Instance, path).Load();
        Assert.Equal(500, reloaded.SampleIntervalMs);
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public void Load_DuplicateRules_AreDeduplicated()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, """
            {
              "rules": [
                { "pattern": "chrome.exe", "action": "mergeToName", "target": "Chrome" },
                { "pattern": " CHROME.EXE ", "action": "mergeToName", "target": "chrome" },
                { "pattern": "msedge.exe", "action": "exclude" },
                { "pattern": "", "action": "exclude" }
              ]
            }
            """);

        var settings = new SettingsStore(NullLog.Instance, path).Load();

        Assert.Equal(2, settings.Rules.Count);
        Assert.Equal("chrome.exe", settings.Rules[0].Pattern);
        Assert.Equal("msedge.exe", settings.Rules[1].Pattern);
    }

    [Fact]
    public void Save_InvalidSettings_AreNormalizedOnDisk()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        var store = new SettingsStore(NullLog.Instance, path);
        var settings = new AppSettings { SampleIntervalMs = 1, TopN = 9999 };

        store.Save(settings);

        var reloaded = new SettingsStore(NullLog.Instance, path).Load();
        Assert.Equal(500, reloaded.SampleIntervalMs);
        Assert.Equal(200, reloaded.TopN);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        using var temp = new TempTestDirectory();
        var path = temp.Combine("settings.json");
        var store = new SettingsStore(NullLog.Instance, path);

        store.Save(new AppSettings());
        store.Save(new AppSettings { TopN = 40 });

        var leftovers = Directory.GetFiles(temp.Root, "*.tmp-*");
        Assert.Empty(leftovers);
        Assert.Single(Directory.GetFiles(temp.Root, "settings.json"));
    }

    [Fact]
    public void Save_ToMissingDirectory_CreatesIt()
    {
        using var temp = new TempTestDirectory();
        var path = Path.Combine(temp.Root, "nested", "deep", "settings.json");
        var store = new SettingsStore(NullLog.Instance, path);

        store.Save(new AppSettings());

        Assert.True(File.Exists(path));
    }
}
