# PerfScope 项目约定（AGENTS.md）

## 用户长期要求（必须遵守）

**每一轮对话结束时，都必须让 dev 版保持运行，供用户验收效果。**

- "dev 版"指 Debug 构建：`src\PerfScope.App\bin\Debug\net10.0-windows\PerfScope.exe`。
- 每轮收尾时若没有存活的 PerfScope 实例，必须启动一个；若现有实例是改动前的旧代码，先结束进程、重新构建、再启动。
- 启动前先清理旧实例（程序有单实例互斥，否则新实例会静默退出）：

  ~~~powershell
  Get-Process PerfScope -ErrorAction SilentlyContinue | Stop-Process -Force
  ~~~

- 启动必须用便携数据目录，避免污染 `%LOCALAPPDATA%`，并用 WMI 分离启动（避免 GUI 子进程占住调用方的标准输出）：

  ~~~powershell
  $exe = "<repo>\src\PerfScope.App\bin\Debug\net10.0-windows\PerfScope.exe"
  $data = "<repo>\artifacts\dev-run"
  Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $exe + '" --data-dir "' + $data + '"') }
  ~~~

- 验收实例**不要**加 `--selftest`（那会让它到点自动退出）。
- 启动后必须确认：进程存活、`MainWindowTitle` 为 `PerfScope — 应用级 CPU / 内存监控`、数据目录下没有 `logs\crash.log`。
- 收尾前把窗口置前（`SetForegroundWindow`），并截图确认渲染正常；截图用 `scripts/capture-window.cs`。

## 构建 / 测试 / 发布

| 用途 | 命令 |
|---|---|
| 构建 | `dotnet build -c Debug`（0 警告 0 错误是底线） |
| 测试 | `.\scripts\run-tests.ps1` |
| 发布单文件 | `.\scripts\build.ps1 -Publish` → `dist\PerfScope.exe` |
| 自身开销实测 | `.\scripts\measure.ps1 -Seconds 60` |
| 无界面冒烟 | `PerfScope.Cli.exe --frames 4 --interval 1500 --top 15` |

本机 `dotnet test` 不可用：VSTest testhost 启动时需要打开父进程句柄，会被当前环境拒绝（`Win32Exception (5)`）。
必须用仓库内的反射运行器 `tests\PerfScope.TestRunner`（`.\scripts\run-tests.ps1` 已封装）。

## 环境注意事项

- 受限会话里，由工具直接启动的进程只能看到被过滤的进程表（例如 167 / 840），`NtQuerySystemInformation`
  返回的表也偏小；这是环境限制，不是程序缺陷。需要对照时可用 `dotnet run` 启动的子进程验证。
- `%LOCALAPPDATA%` 在受限会话可能不可写，所以验收实例一律用 `--data-dir` 便携目录。
- 截图脚本 `scripts/capture-window.cs` 内置 Per-Monitor V2 DPI 感知；去掉它的话，在 150% 缩放下
  `GetWindowRect` 会返回虚拟化坐标，只能截到窗口左上角。

## 代码约定

- `PerfScope.Core`（采样 / 归并 / 历史 / 设置 / 日志）不引用 WinForms；UI 只通过 `CoreRuntime` 与
  `Abstractions` 里的接口访问 Core。
- Core 与 App 保持零第三方 NuGet 依赖；测试项目用 xunit。
- 面向用户的字符串、注释、日志、提交信息一律中文。
- 界面参数改动优先做成 `AppSettings` 字段 + 设置页可调，不要在代码里写死。
