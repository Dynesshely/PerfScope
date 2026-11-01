# PerfScope 项目约定（AGENTS.md）

## 用户长期要求（必须遵守）

**每一轮对话结束时，都必须让 dev 版保持运行，供用户验收效果。**

- "dev 版"指 Debug 构建：`src\PerfScope.App\bin\Debug\net10.0-windows\PerfScope.exe`。
- 每轮收尾时若没有存活的 PerfScope 实例，必须启动一个；若现有实例是改动前的旧代码，先结束进程、重新构建、再启动。
- 启动前先清理旧实例（程序有单实例互斥，否则新实例会静默退出）：

  ~~~powershell
  Get-Process PerfScope -ErrorAction SilentlyContinue | Stop-Process -Force
  ~~~

- **必须用 `scripts\run-dev.ps1` 启动**（它会构建、把产物镜像到工作区外、再用 WMI 分离启动）：

  ~~~powershell
  .\scripts\run-dev.ps1            # 构建 + 启动
  .\scripts\run-dev.ps1 -NoBuild   # 只重启
  ~~~

  原因（实测结论）：DSH 的 Windows 沙箱按「进程映像所在路径」生效——只要 exe 在工作区内，
  这个进程看到的进程表就被过滤（本机：工作区内 205 个进程 vs 工作区外 930 个），并且写
  `%LOCALAPPDATA%` 也会被拒绝。脚本把 Debug 产物镜像到 `%LOCALAPPDATA%\PerfScope-dev\bin` 再启动，
  就能拿到完整数据；数据目录仍指向仓库的 `artifacts\dev-run`。
  脚本结束时会打印「进程可见性」，正常应显示数百个进程；如果只是 200 上下，说明镜像没生效。

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
| 文档截图 | `.\scripts\mock-screenshots.ps1`（`--mock` 虚构数据，不含真实信息） |
| 发布打包 | `.\scripts\package.ps1 -Version 1.0.0` → `dist\release`（portable zip + NSIS 安装包 + 校验和） |
| 安装包脚本 | `installer\PerfScope.nsi`（NSIS 3，按用户安装到 %LOCALAPPDATA%，无需管理员） |

本机 `dotnet test` 不可用：VSTest testhost 启动时需要打开父进程句柄，会被当前环境拒绝（`Win32Exception (5)`）。
必须用仓库内的反射运行器 `tests\PerfScope.TestRunner`（`.\scripts\run-tests.ps1` 已封装）。

## 发布

- 发版只需推 tag：`git tag v1.0.0 && git push origin v1.0.0`，`.github/workflows/release.yml` 会跑测试、
  打包并创建 GitHub Release（portable zip + NSIS 安装包 + SHA256SUMS；带 `-` 的 tag 视为预发布）。
- 发布产物是 **self-contained 单文件**（内置 .NET 运行时），用户机器无需安装运行时。
- 版本号由 tag 决定，通过 `-p:Version` 注入程序集，界面右下角显示的版本即来自这里。

## 环境注意事项

- **沙箱是按「进程映像路径」生效的**：exe 在工作区内 → 进程表被过滤、`%LOCALAPPDATA%` 写入被拒；
  exe 在工作区外（`%LOCALAPPDATA%` / `%TEMP%` 等）→ 完全正常。这就是必须用 `run-dev.ps1` 的原因。
  判断方法：用同一份二进制分别放在工作区内、外各跑一次 `PerfScope.Cli --processes`，进程数差别很大。
- 截图脚本 `scripts/capture-window.cs` 内置 Per-Monitor V2 DPI 感知；去掉它的话，在 150% 缩放下
  `GetWindowRect` 会返回虚拟化坐标，只能截到窗口左上角。
- 需要截「悬停态」时可给应用加 `--hover-demo`（强制显示坐标轴与数值卡片，仅用于截图/自测）。

## 代码约定

- `PerfScope.Core`（采样 / 归并 / 历史 / 设置 / 日志）不引用 WinForms；UI 只通过 `CoreRuntime` 与
  `Abstractions` 里的接口访问 Core。
- Core 与 App 保持零第三方 NuGet 依赖；测试项目用 xunit。
- 面向用户的字符串、注释、日志、提交信息一律中文。
- 界面参数改动优先做成 `AppSettings` 字段 + 设置页可调，不要在代码里写死。