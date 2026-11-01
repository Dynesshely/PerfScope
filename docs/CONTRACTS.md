# PerfScope 核心契约（冻结版 v1）

> 由 Lead 维护。任何实现线**不得修改**`src/PerfScope.Core/Models``src/PerfScope.Core/Abstractions`
> `src/PerfScope.Core/Settings/AppSettings.cs``src/PerfScope.Core/AppPaths.cs``src/PerfScope.Core/CoreRuntime.cs`
> 以及所有 csproj。需要改契约时先向 Lead 报告，由 Lead 统一改。

## 具体实现类与构造签名（必须严格一致）

| 接口 | 实现类（命名空间） | 构造参数 |
|---|---|---|
| ICapture | Native.NativeCapture | 无参 |
| IAppClassifier | Grouping.AppClassifier | (AppSettings settings) |
| ISnapshotComposer | Sampling.SnapshotComposer | (ICapture capture, IAppClassifier classifier, AppSettings settings) |
| IMonitorEngine | Sampling.MonitorEngine | (ISnapshotComposer composer, AppSettings settings, ILog? log) |
| IHistoryStore | History.HistoryStore | (AppSettings settings, ILog? log) |
| ISettingsStore | Settings.SettingsStore | (ILog? log) |
| ILog | Diagnostics.FileLog | (string path, LogLevel minLevel, int maxKilobytes) |

命名空间根：PerfScope.Core。所有实现类 public、sealed。

## 硬性约束

1. **零第三方 NuGet 依赖**（Core 与 App 只能使用 BCL + WinForms + P/Invoke）。
2. 采样必须走 NtQuerySystemInformation(SystemProcessInformation) 批量查询；**禁止** WMI、PerformanceCounter
   或逐进程 System.Diagnostics.Process 轮询。
3. 面向用户的字符串、注释、日志一律中文。
4. 目标框架：Core 与 Tests 为 net10.0-windows，App 为 net10.0-windows + UseWindowsForms。
5. 编译必须 0 error，尽量 0 warning（用 dotnet build 自检）。
6. 路径/版本信息解析一律走 AppClassifier 内部缓存，不允许其它模块重复读盘。

## 数据目录

统一走 PerfScope.Core.AppPaths：数据根 %LOCALAPPDATA%\PerfScope，设置 settings.json，历史 history\，日志 logs\。
