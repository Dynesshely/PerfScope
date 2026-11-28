using System.Diagnostics;
using PerfScope.App.Infrastructure;
using PerfScope.Core;
using PerfScope.Core.Models;
using PerfScope.Core.Settings;

namespace PerfScope.App.Services;

/// <summary>
/// 托盘通知协调者：阈值告警与更新提示。
/// 规则：整机 CPU / 内存超阈值、单个应用 CPU 超阈值时弹气泡，同一类告警按冷却时间限频；
/// 更新检查只在启动后做一次，可关闭，是应用唯一的网络访问。
/// </summary>
internal sealed class NotificationCoordinator : IDisposable
{
    private readonly AppHost _host;
    private readonly TrayService _tray;
    private readonly Dictionary<string, DateTime> _appCooldowns = new(StringComparer.Ordinal);
    private DateTime _lastSystemAlertUtc = DateTime.MinValue;
    private bool _disposed;
    private bool _updateChecked;

    /// <summary>创建协调者。</summary>
    public NotificationCoordinator(AppHost host, TrayService tray)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _host.Engine.FrameProduced += OnFrameProduced;
    }

    /// <summary>启动后的异步任务（更新检查）。</summary>
    public async Task StartAsync()
    {
        if (_updateChecked || !_host.Settings.UpdateCheckEnabled)
        {
            return;
        }

        _updateChecked = true;
        try
        {
            var service = new Infrastructure.Updates.UpdateService(AppVersionInfo.Version);
            var result = await service.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            if (result is { HasUpdate: true })
            {
                _tray.ShowBalloon(
                    Localization.Loc.T("发现新版本"),
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Localization.Loc.T("PerfScope {0} 已发布，当前版本 {1}。"), result.LatestVersion, AppVersionInfo.Version));
            }
        }
        catch
        {
            // 更新检查失败不能打扰用户（离线是常态）
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Engine.FrameProduced -= OnFrameProduced;
    }

    private void OnFrameProduced(object? sender, SnapshotFrame frame)
    {
        var settings = _host.Settings;
        if (!settings.EnableAlerts)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - _lastSystemAlertUtc).TotalSeconds >= settings.AlertCooldownSeconds)
        {
            if (frame.SystemCpuPercent >= settings.AlertCpuPercent)
            {
                _lastSystemAlertUtc = now;
                _tray.ShowBalloon(
                    Localization.Loc.T("整机 CPU 高负载"),
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Localization.Loc.T("当前 {0}%，超过设定阈值 {1}%。"),
                        frame.SystemCpuPercent.ToString("F1", System.Globalization.CultureInfo.CurrentCulture),
                        settings.AlertCpuPercent),
                    warning: true);
                return;
            }

            if (frame.SystemMemoryPercent >= settings.AlertMemoryPercent)
            {
                _lastSystemAlertUtc = now;
                _tray.ShowBalloon(
                    Localization.Loc.T("内存占用偏高"),
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Localization.Loc.T("当前 {0}%，超过设定阈值 {1}%。"),
                        frame.SystemMemoryPercent.ToString("F1", System.Globalization.CultureInfo.CurrentCulture),
                        settings.AlertMemoryPercent),
                    warning: true);
                return;
            }
        }

        if (settings.AlertAppCpuPercent <= 0)
        {
            return;
        }

        foreach (var app in frame.Apps)
        {
            if (app.CpuPercent < settings.AlertAppCpuPercent)
            {
                continue;
            }

            if (_appCooldowns.TryGetValue(app.Key, out var last) &&
                (now - last).TotalSeconds < settings.AlertCooldownSeconds)
            {
                continue;
            }

            _appCooldowns[app.Key] = now;
            _tray.ShowBalloon(
                Localization.Loc.T("应用 CPU 占用偏高"),
                string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localization.Loc.T("{0} 当前 {1}%，超过设定阈值 {2}%。"),
                    app.DisplayName,
                    app.CpuPercent.ToString("F1", System.Globalization.CultureInfo.CurrentCulture),
                    settings.AlertAppCpuPercent),
                warning: true);
            return;
        }
    }
}
