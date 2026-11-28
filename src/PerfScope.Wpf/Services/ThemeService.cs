using System.Windows;

namespace PerfScope.App.Services;

/// <summary>
/// 主题服务：在深色 / 浅色 / 高对比度三套调色板之间切换。
/// 只替换 Application.Resources 里那份 Palette.*.xaml，其余样式全部走 DynamicResource，
/// 因此换主题不需要重建窗口。
/// </summary>
public sealed class ThemeService
{
    private const string PalettePrefix = "pack://application:,,,/Themes/Palette.";

    private string _currentKey = "Dark";

    /// <summary>当前是否为深色（含高对比度下的深色系统配色）。</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>当前是否处于高对比度模式。</summary>
    public bool IsHighContrast { get; private set; }

    /// <summary>主题切换后触发（窗口壳据此调整 DWM 标题栏）。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>按设置值（dark / light）应用主题；系统开启高对比度时强制走高对比度调色板。</summary>
    public void Apply(string? themeSetting)
    {
        bool wantDark = !string.Equals(themeSetting, "light", StringComparison.OrdinalIgnoreCase);
        bool highContrast = Infrastructure.UiDebug.ForceHighContrast || SystemParameters.HighContrast;
        string key = highContrast ? "HighContrast" : wantDark ? "Dark" : "Light";

        IsHighContrast = highContrast;
        IsDark = wantDark;

        if (string.Equals(key, _currentKey, StringComparison.Ordinal) && AlreadyLoaded(key))
        {
            return;
        }

        _currentKey = key;
        SwapPalette(key);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重新检测系统高对比度状态并应用（系统设置变化时调用）。</summary>
    public void Refresh(string? themeSetting) => Apply(themeSetting);

    private static bool AlreadyLoaded(string key)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.EndsWith("Palette." + key + ".xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void SwapPalette(string key)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        int index = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source?.Contains("/Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true)
            {
                index = i;
                break;
            }
        }

        var dictionary = new ResourceDictionary { Source = new Uri(PalettePrefix + key + ".xaml") };
        if (index >= 0)
        {
            merged[index] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }
    }
}
