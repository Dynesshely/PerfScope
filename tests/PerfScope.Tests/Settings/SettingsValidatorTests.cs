using PerfScope.Core.Settings;

namespace PerfScope.Tests.Settings;

/// <summary>设置校验器测试：字段范围、非法枚举、字符串回退、规则去重。</summary>
public sealed class SettingsValidatorTests
{
    [Fact]
    public void NormalizeDefaults_ReturnsNoFixes()
    {
        var settings = new AppSettings();

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Empty(fixes);
        Assert.Equal(2000, settings.SampleIntervalMs);
        Assert.Equal(LogLevel.Warn, settings.LogLevel);
    }

    [Fact]
    public void Normalize_ClampsAllNumericFields()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            SampleIntervalMs = 1,
            UiRefreshIntervalMs = 999999,
            HiddenSampleIntervalMs = 1,
            SmoothingWindow = 99,
            MemoryRetentionMinutes = 1,
            DiskRetentionDays = 999,
            TrendAppSlots = 0,
            TopN = 1,
            AlertCpuPercent = 1,
            AlertMemoryPercent = 999,
            AlertCooldownSeconds = 1,
            LogMaxKilobytes = 1,
        };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(500, settings.SampleIntervalMs);
        Assert.Equal(10000, settings.UiRefreshIntervalMs);
        Assert.Equal(1000, settings.HiddenSampleIntervalMs);
        Assert.Equal(15, settings.SmoothingWindow);
        Assert.Equal(5, settings.MemoryRetentionMinutes);
        Assert.Equal(365, settings.DiskRetentionDays);
        Assert.Equal(1, settings.TrendAppSlots);
        Assert.Equal(5, settings.TopN);
        Assert.Equal(10, settings.AlertCpuPercent);
        Assert.Equal(100, settings.AlertMemoryPercent);
        Assert.Equal(30, settings.AlertCooldownSeconds);
        Assert.Equal(64, settings.LogMaxKilobytes);
        Assert.Equal(13, fixes.Count);
        Assert.All(fixes, fix => Assert.False(string.IsNullOrWhiteSpace(fix)));
    }

    [Fact]
    public void Normalize_DiskRetentionZeroMeansNoDiskHistory()
    {
        var settings = new AppSettings { DiskRetentionDays = 0 };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Empty(fixes);
        Assert.Equal(0, settings.DiskRetentionDays);
    }

    [Fact]
    public void Normalize_InvalidEnumsFallBackToDefaults()
    {
        var settings = new AppSettings
        {
            CpuMode = (CpuNormalization)42,
            MemoryMode = (MemoryDisplay)42,
            CloseBehavior = (CloseBehavior)42,
            LogLevel = (LogLevel)42,
        };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Equal(CpuNormalization.MachinePercent, settings.CpuMode);
        Assert.Equal(MemoryDisplay.WorkingSet, settings.MemoryMode);
        Assert.Equal(CloseBehavior.MinimizeToTray, settings.CloseBehavior);
        Assert.Equal(LogLevel.Warn, settings.LogLevel);
        Assert.Equal(4, fixes.Count);
    }

    [Fact]
    public void Normalize_EmptyGroupNamesFallBackToDefaults()
    {
        var settings = new AppSettings { UnknownGroupName = "   ", SystemGroupName = string.Empty };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Equal("其他", settings.UnknownGroupName);
        Assert.Equal("Windows 系统", settings.SystemGroupName);
        Assert.Equal(2, fixes.Count);
    }

    [Fact]
    public void Normalize_InvalidThemeFallsBackToDark()
    {
        var settings = new AppSettings { Theme = "  Neon  " };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Equal("dark", settings.Theme);
        Assert.Single(fixes);
    }

    [Fact]
    public void Normalize_DuplicateRulesAreRemoved()
    {
        var settings = new AppSettings();
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "chrome.exe", Action = AppRuleAction.MergeToName, Target = "Chrome" });
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "  CHROME.EXE ", Action = AppRuleAction.MergeToName, Target = "chrome" });
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.PathContains, Pattern = @"\Steam\", Action = AppRuleAction.MergeToName, Target = "Steam" });
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "   ", Action = AppRuleAction.Exclude });
        settings.Rules.Add(new AppRule { Match = (AppRuleMatch)99, Pattern = "foo.exe", Action = (AppRuleAction)99, Target = " Foo " });

        var fixes = SettingsValidator.Normalize(settings);

        Assert.Equal(3, settings.Rules.Count);
        Assert.Equal("chrome.exe", settings.Rules[0].Pattern);
        Assert.Equal("Foo", settings.Rules[2].Target);
        Assert.Equal(AppRuleMatch.ExeName, settings.Rules[2].Match);
        Assert.Equal(AppRuleAction.MergeToName, settings.Rules[2].Action);
        Assert.Equal("Steam", settings.Rules[1].Target);
        Assert.Contains(fixes, fix => fix.Contains("去重", StringComparison.Ordinal));
        Assert.Contains(fixes, fix => fix.Contains("无匹配模式", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_NullRulesListIsReset()
    {
        var settings = new AppSettings { Rules = null! };

        var fixes = SettingsValidator.Normalize(settings);

        Assert.NotNull(settings.Rules);
        Assert.Empty(settings.Rules);
        Assert.Single(fixes);
    }

    [Fact]
    public void Normalize_ExcludeRuleDropsTarget()
    {
        var settings = new AppSettings();
        settings.Rules.Add(new AppRule { Match = AppRuleMatch.ExeName, Pattern = "perfscope.exe", Action = AppRuleAction.Exclude, Target = "PerfScope" });

        SettingsValidator.Normalize(settings);

        Assert.Null(settings.Rules[0].Target);
    }
}
