# PerfScope 交接文档

> 最后更新：2026-11-28 · 版本 0.2.0（GUI 层已重写为 WPF + MVVM）
> 本文档面向「接手这个项目的下一个人 / 下一个 AI 会话」，目标是让你在 30 分钟内能改代码、能验证、能发版。

---

## 1. 这是什么

PerfScope 是一个 Windows 桌面应用级资源监控器：它把几百个进程**归并成"应用"**，让你看到"哪个应用在吃 CPU"，而不是"哪个 PID 在吃 CPU"。浏览器渲染进程、Electron 辅助进程会合并成一行；WSL、显卡驱动栈、内核与安全服务等 Windows 子系统也会作为独立"应用"出现。

| 项目属性 | 值 |
|---|---|
| 仓库 | https://github.com/Dynesshely/PerfScope |
| 技术栈 | .NET 10（`net10.0-windows`）+ WPF（MVVM），**零第三方 NuGet**（Core / Wpf） |
| 当前版本 | 0.2.0（WPF 重写，尚未打 tag） |
| 最近发布 | 0.1.4（2026-11-07）；0.2.0 待发 |
| 代码规模 | 约 17k 行 C# + 3k 行 XAML，17 个测试文件，168 个单测 |
| 界面语言 | 中文 / English（resx 资源化，运行时切换） |
| 许可 | MIT |

**产品定位的一句话**：轻量、常驻、离线、无需安装运行时的应用级资源监控器，且能直接产出可以喂给 AI 的分析数据。

---

## 2. 5 分钟上手

```powershell
# 构建（0 警告 0 错误是底线）
dotnet build -c Debug

# 跑测试（本机 dotnet test 不可用，见 §7，必须用仓库内的反射运行器）
.\scripts\run-tests.ps1

# 启动开发版（会构建 → 镜像到工作区外 → 分离启动）
.\scripts\run-dev.ps1            # 构建 + 启动
.\scripts\run-dev.ps1 -NoBuild   # 只重启

# 自身开销门禁（CPU ≤ 2%，RSS ≤ 200 MB）
.\scripts\check-perf.ps1

# 格式门禁（CI 会跑，必须通过）
dotnet format --verify-no-changes

# 打包发布产物（portable zip + NSIS 安装包 + SHA256SUMS）
.\scripts\package.ps1 -Version 0.1.4
```

**为什么必须用 `run-dev.ps1` 而不是直接双击 exe**：见 §7.1 的沙箱陷阱。

---

## 3. 仓库地图

```
src/
  PerfScope.Core/            不含 UI 框架的全部核心逻辑
    Native/                  NtQuerySystemInformation 批采样、进程路径解析
    Sampling/                SnapshotComposer（归并 + CPU 归一化）、MonitorEngine（采样循环）、SmoothingBuffer
    Grouping/                归并算法：AppClassifier、InstallRootResolver、SystemModuleCatalog、ChannelDetector…
    History/                 分钟级聚合落盘、趋势环形缓冲
    Insights/                事件、日报、周报、Agent Prompt
    Settings/                AppSettings + 校验 + 持久化
    Diagnostics/             脱敏诊断包、文件日志
    Updates/                 AppVersion（语义化版本解析/比较）
  PerfScope.Wpf/             WPF 外壳（MVVM）
    Views/                   纯声明式 XAML（主窗口 / 设置 / 历史 / 关于 / 接入 Agent / 引导）
    ViewModels/              Main、Settings、TreemapPanel、History、AppRow …（业务装配都在这里）
    Services/                ShellController、DialogService、ThemeService、IconService、TrayService
    Controls/                TrendChart（折线 + 悬停坐标轴）、TreemapView（Canvas 绑定矩形）
    Chrome/                  窗口壳（自绘标题栏、Snap Layouts、原生窗口动画）
    Themes/                  三套调色板 + 控件样式（DynamicResource）
    Wpf/WpfBootstrap.cs      入口：提权、单实例、参数解析、服务装配
    Shared/AppHost.cs        组装 Core 与 UI 的门面（UI 只通过它访问 Core）
    Bootstrap/               命令行解析、单实例互斥 + 唤醒通道
    Infrastructure/          提权、开机自启、更新检查、图标、内存工作集修剪
      Elevation.cs           默认提权（runas 重新拉起自己，--elevated 防死循环）
      StartupRegistration.cs 开机自启（计划任务 HighestAvailable，失败退回 HKCU Run）
    Localization/            Loc.cs + Strings.resx / Strings.en.resx
    Mvvm/                    ObservableObject / RelayCommand / AsyncRelayCommand（自建，零第三方）
  PerfScope.Cli/            无界面命令行（冒烟测试 / CI 性能门禁 / 报告生成）
tests/
  PerfScope.Tests/           xunit 测试（纯逻辑，不依赖桌面）
  PerfScope.TestRunner/      反射测试运行器（绕开本机 VSTest 问题）
tools/PerfScope.Cli/         命令行工具工程
scripts/                     构建、运行、测试、打包、截图、性能门禁
installer/PerfScope.nsi      NSIS 安装包脚本（UTF-8 **带 BOM**，否则 makensis 报错）
docs/                        架构、归并规则、契约、验收、winget 清单
.github/workflows/           ci.yml / release.yml / codeql.yml
```

---

## 4. 数据流（改代码前必须理解）

```
NativeCapture.Query()           一次 NtQuerySystemInformation 拿到全部进程 + 系统 CPU/内存计数器
        │                       （外加 DPC / 中断时间，用于计算"未归属 CPU"）
        ▼
SnapshotComposer.Compose()      差分算 CPU% → AppClassifier 归并 → 按应用聚合 → 平滑
        │                       → 追加「未归属 CPU」行（整机忙碌 − 各进程之和）
        ▼
SnapshotFrame                   不可变快照：整机指标 + AppSample[]
        ├── MainForm / 控件     UI 渲染（全自绘）
        ├── HistoryStore        分钟级聚合落盘（%LOCALAPPDATA%\PerfScope\history）
        └── Insights            事件、日报、周报、Agent Prompt
```

**CPU 百分比有两种口径**，由 `AppSettings.CpuMode` 决定：

- `MachinePercent`（默认）：整机口径，0–100%，与任务管理器一致
- `PerCorePercent`：单核口径，可以超过 100%（32 核最多 3200%）

换算点只有一处：`SnapshotComposer` 里的 `displayPercent = perCore ? machinePercent * logicalProcessors : machinePercent`。**新增任何 CPU 统计都必须同时处理这两种口径**，否则数字会对不上。

---

## 5. 归并算法优先级（产品的核心价值）

按顺序命中，命中即停：

1. 用户自定义规则（设置页可编辑）
2. Windows 系统功能模块（`SystemModuleCatalog`：WSL、显卡栈、安全、更新、音频、打印、内核、驱动…）
3. 显式排除（系统空闲 PID 0 等）
4. 版本信息：`ProductName` + `CompanyName` + 发行通道（beta/canary/dev…）
5. 安装根目录（从 exe 路径向上找 `Program Files` 一级）
6. exe 所在目录
7. exe 文件名

每一步都会记录**归并依据**（`AppIdentity.Reason`），UI 里能看到，这是本项目的可解释性卖点——改归并逻辑时不要把 Reason 弄丢。

细节见 `docs/GROUPING.md`。

---

## 6. 约定与红线

这些是长期约定，改代码前请先读：

| 约定 | 说明 |
|---|---|
| **零第三方 NuGet** | Core 与 App 不引第三方包（测试项目可以用 xunit）。想加 GPU 指标这类依赖，必须先讨论取舍。 |
| **Core 不引用 WinForms** | UI 通过 `AppHost` 与 `Core.Abstractions` 里的接口访问 Core。 |
| **注释 / 日志 / 提交信息用中文** | 面向用户的字符串走 resx，**key 就是中文原文**（`Loc.T("暂停监控")`），英文在 `Strings.en.resx`。新增文案必须同时补英文条目。 |
| **行尾统一 LF** | `.editorconfig` 要求 LF，`.gitattributes` 强制 `* text=auto eol=lf`。改完代码跑一次 `dotnet format`。 |
| **默认以管理员运行** | 非管理员读不到受保护进程的路径与 CPU 时间，数字会偏小。提权失败要能优雅降级（状态栏提示）。 |
| **隐私** | 唯一网络请求是可关闭的更新检查；零遥测；诊断包必须脱敏（主机名先于用户名替换，顺序错了会漏）。 |
| **性能预算** | 自身 CPU ≤ 2%、RSS ≤ 200 MB（实测 0.05% / 45 MB）。`check-perf.ps1` 是硬门禁，CI 也跑。 |
| **UI 参数一律可调** | 界面参数改动优先做成 `AppSettings` 字段 + 设置页可调，不要在代码里写死。 |

---

## 7. 已知陷阱（都是踩过的坑，别重走）

### 7.1 沙箱按「进程映像路径」生效（本机特有）

exe 放在工作区内时，DSH 沙箱会过滤进程表（约 200 个进程）并拒绝写 `%LOCALAPPDATA%`；放到工作区外一切正常（约 1100 个进程）。

**这不是代码 bug，不要试图"修复"**。`scripts/run-dev.ps1` 就是把产物镜像到 `%LOCALAPPDATA%\PerfScope-dev\bin` 再启动。看到只有 200 个进程就是镜像没生效。

### 7.2 `dotnet test` 在本机不可用

VSTest testhost 启动时打开父进程句柄会被拒绝（`Win32Exception (5)`）。必须用 `tests/PerfScope.TestRunner`（反射运行器，`run-tests.ps1` 已封装）。

### 7.3 本地 format 绿、CI 必红 = 行尾问题

现象：本地 `dotnet format --verify-no-changes` 通过，CI 报 `ENDOFLINE: Replace 2 characters with '\n'`。

原因：`.editorconfig` 要求 LF，但本机 `core.autocrlf=true` 把文件检出成 CRLF，仓库当初又没有 `.gitattributes`。

修法：`.gitattributes` 已有（`* text=auto eol=lf`），若换机器后复发，跑 `git add --renormalize .`。

### 7.4 CI 的 imports 门禁

`dotnet format` 会重排 using（`System.*` 在前）。写完代码顺手跑 `dotnet format`，否则 CI 报 `IMPORTS`。

### 7.5 NSIS 脚本编码

`installer/PerfScope.nsi` 必须是 **UTF-8 with BOM**，否则 `makensis` 报 `Bad text encoding`。

### 7.6 winget 清单三层校验（被打了三次回来）

1. locale 文件 `ManifestType` 必须是 `defaultLocale`（不是 `locale`）
2. 安装信息必须在 `Installers:` 数组里（1.10 schema），不能是顶层 `InstallerUrl`
3. 每个 yaml **首行必须有** `# yaml-language-server: $schema=<对应 schema URL>`，且 URL 要与 `ManifestType` 匹配

本地可先用官方 schema 校验（`.meta` 里有脚本参考）。

### 7.7 提权与自启的连带关系

`HKCU...Run` 无法提权（UAC 会以普通权限拉起）。所以开机自启改成了**计划任务 + `HighestAvailable`**；计划任务创建失败才退回 Run 项。

### 7.8 SDK 版本漂移

`global.json` 原本锁 `10.0.402`，某次环境更新后本机不再有该 SDK。现在写的是 `10.0.100-rc.1.25451.107` + `rollForward: latestMajor`。换机器时先 `dotnet --list-sdks` 对齐。

### 7.9 发布流水线会被测试拦住

tag 推送后 `release.yml` 会先跑测试。测试挂了就是发版失败（曾因周报测试断言与实现口径不一致触发过一次）。**发版前本地务必 `run-tests.ps1`**。

---

### 7.10 WPF 的 XAML 编译会生成一个临时工程（`_wpftmp`）

`UseWPF` 的工程在编译 XAML 时会生成 `PerfScope.Wpf_<hash>_wpftmp.csproj` 再过一遍源码。
**这个临时工程不继承 `Directory.Build.props` 里的一部分属性**（本机实测：`ImplicitUsings` 丢失），
表现为「`dotnet build` 正常、但报 `当前上下文中不存在名称 Path`」这类莫名错误。

修法：在 WPF 工程里放一份显式的 `GlobalUsings.cs`（本项目已有），不要依赖隐式 using。

### 7.11 不要 Freeze 含 DynamicResource 画笔的 Pen

`new Pen(brush, w).Freeze()` 在 `brush` 是从资源字典取来、本身未冻结时必抛
`InvalidOperationException: 无法冻结此 Freezable`。这个异常在 `OnRender` 里抛出会被
Dispatcher 每次重绘重复触发 —— 曾经一秒钟堆出 6 个模态对话框把界面锁死。
现在 `App.xaml.cs` 对界面线程异常只弹一次提示，但仍然要在渲染代码里避免 Freeze。

### 7.12 树图与列表必须用同一套内存口径

旧 WinForms 版树图固定用工作集，而列表跟随 `MemoryMode`（工作集 / 私有字节），
两处数字对不上是历史遗留问题。WPF 版已统一：`TreemapPanelViewModel.SetMemoryMode`
由 `MainViewModel` 按设置同步。新增任何内存展示都要走同一口径。

### 7.13 文案分成三层，改文案时别放错层

| 层 | 位置 | 机制 |
|---|---|---|
| XAML | Views/*.xaml | Text="{loc:Loc '中文原文'}"，加载时求值（语言切换需重启，与旧版一致） |
| 代码 | ViewModel / Services | Loc.T("中文原文")，key 就是中文原文 |
| Core | 报表 / Prompt / 事件说明 | CoreText.T("中文原文")，App 启动时注入 Loc.T，未注入时恒等返回中文 |

新增任何面向用户的文案：先写中文原文，再往 Localization/Strings.en.resx 补一条同名 key。
日志（ILog）保持中文，不做翻译 —— 它们是排障用的，不是界面。

### 7.14 UIA：行级摘要 + 原生 Selection 模式（已实测）

WPF 的 ListBox 默认只把「内容对象」暴露为 UIA 名称，自绘模板里的列值不会被自动读取。
现在的可访问性由两部分组成：

| 能力 | 实现 |
|---|---|
| 行名称 | AutomationProperties.Name = 应用名 |
| 行摘要 | AutomationProperties.HelpText = AccessibilitySummary（CPU / 内存 / 进程数 / IO 一句话） |
| 选择模式 | ListBox 原生实现 SelectionPattern，每行 SelectionItemPattern（WPF 自带，无需自绘回调） |
| 列表名称 | AutomationProperties.Name = 「应用列表」 |

2026-11-28 用 UIAutomationClient 实测（--mock 实例）：列表 SelectionPattern 可用，
16 个可见行均为 ListItem 且都提供 SelectionItemPattern；示例行 Name 为「Terra Game」，
HelpText 为「Terra Game · CPU 7.6% · 内存 3.2 GB · 进程 4 · IO 7.0 MB/s / 736 KB/s」。
旧版「自绘列表没有 UIA Selection 回调」的债在 WPF 下由原生控件模式直接消解，无需自己实现。

### 7.15 性能数字必须说清楚是「整机口径」还是「单核口径」

这是本机踩过的坑：用 `Get-Process` 前后两次 `TotalProcessorTime` 相减算出来的百分比是
**单核口径**（100% = 一个核跑满）；而 `check-perf.ps1` / `dotnet-counters` / App 自报的
「自身 CPU」是**整机口径**（100% = 32 个核全满）。同一份数据两个口径差 32 倍，
曾被误读成「CPU 15%，严重超标」，实际只有 0.3% 整机。

| 工具 | 口径 |
|---|---|
| `Get-Process` 的 `TotalProcessorTime` 差值 / 墙钟时间 × 100 | 单核 |
| `dotnet-counters` 的 `System.Runtime` → `CPU Usage (%)` | 整机 |
| App 状态栏「自身开销」、`check-perf.ps1` | 整机 |

2026-11-28 实测（WPF 版，mock 窗口 1900×1040、每秒刷新一帧）：整机 CPU 均值 **0.32%**、
峰值 1.2%、分配速率 1.17 MB/s、句柄 810 稳定、工作集在修剪后 55~172 MB 波动。
同一时刻按单核口径读出来是 8~16%，看起来吓人但并未超标。

### 7.16 自定义 ListBox 模板会悄悄关掉虚拟化

把 ListBox 的 `Template` 换成自己写的 `ScrollViewer + ItemsPresenter` 时，
**必须在 ScrollViewer 上设 `CanContentScroll=True`**（或给 ListBox 加 `ScrollViewer.CanContentScroll` 设置器）。
漏掉时 `IsVirtualizing` 虽然还是 true，但 VirtualizingStackPanel 会退化成物理滚动、
所有行都会被实例化并跟着每帧重排：本机实测从 0.32% 整机涨到约 1%（单核口径 16%），
TopN 越大越明显（TopN=6 → 8.9% 单核，TopN=30 → 16.7% 单核）。

配套证据获取方法：`PERFSCOPE_DIAG_ROWS=1` 启动后，`%TEMP%\perfscope-ui-diag.log` 会每 2 秒
记录 `items / realized / frames / rowsMs / treemapMs` —— realized 应远小于 items（滚动时），
rowsMs 应该是 1~3 ms 量级。

### 7.17 界面诊断开关（仅排障用，生产不设）

环境变量 `PERFSCOPE_DISABLE_UI` 接受逗号分隔的取值：`list`、`treemap`、`trend`、`hover`、
`icons`、`nouia`、`norowheight`、`noprocess`。逐个关掉再测 CPU，可以快速定位是哪个组件在烧。
另有 `PERFSCOPE_DIAG_ROWS=1` 打开行实例化/耗时日志。两者都不影响正常使用。

## 8. 发版流程

```powershell
# 1. 改版本号
#    src/PerfScope.App/PerfScope.App.csproj  →  <Version>0.1.5</Version>
# 2. 补 CHANGELOG
# 3. 本地自检
.\scripts\run-tests.ps1
dotnet format --verify-no-changes
.\scripts\check-perf.ps1
# 4. 打 tag 并推送（CI 自动构建 + 发布）
git tag v0.1.5; git push origin v0.1.5
```

推送后 `.github/workflows/release.yml` 会：跑测试 → 构建单文件 portable zip → 用 NSIS 打安装包 → 生成 `SHA256SUMS.txt` → 创建 GitHub Release。

**产物**：`PerfScope-<ver>-win-x64-portable.zip`、`PerfScope-Setup-<ver>-win-x64.exe`、`SHA256SUMS.txt`。

安装包是**机器级安装**（admin 提权 + `$PROGRAMFILES64\PerfScope` + HKLM 注册 + 全用户开始菜单），
与 winget 清单的 `Scope: machine` 一致；用户数据仍写在各自的 `%LOCALAPPDATA%\PerfScope`。
卸载时会清理开机自启（计划任务 `PerfScope` 与 HKCU Run 项），免得多余自启项指向已删除的 exe。

**代码签名**：暂无。安装包和 exe 都是未签名状态（这是已知的产品化缺口，见 §9）。

---

## 9. 现状与待办

### 0.2.0：GUI 重写为 WPF + MVVM（已完成）

- 窗口壳换成自绘标题栏的 WPF 壳：保留系统原生最小化 / 最大化 / 还原动画与 Win11 Snap Layouts；
  标题栏高度与 `WindowChrome.CaptionHeight` 一致（改高度必须同时改两处）。
- 全部界面数据驱动：主列表、树图、趋势图、状态栏都由 ViewModel 供给；视图只剩 XAML 与很薄的 code-behind。
- 树图布局算法下沉到 `Core/Analysis/TreemapLayout.cs`（Squarified，纯几何，9 个单测覆盖面积/不重叠/不越界）。
- 托盘改为纯 P/Invoke `Shell_NotifyIcon` + WPF 菜单；关闭到托盘、暂停/继续、开机自启、退出沿用旧行为。
- 主题改为三套调色板资源字典（深色 / 浅色 / 高对比度），换主题即时生效。
- **WPF 的工作集比 WinForms 高**：已用 `MemoryTrimmer` 在启动 10 秒后与每 5 分钟做一次工作集修剪，
  实测稳定在 130~150 MB（阈值 200 MB）。若后续加控件导致上升，先查图标缓存与虚拟化设置。

### 已完成（0.1.x）

- 应用级归并（7 级优先 + 可解释的归并依据）
- 双分布视图（内存树图 / CPU 树图）+ 自绘悬浮卡片 + 图标
- 顶部趋势卡（可悬停看坐标与数值）
- 分钟级历史 + 事件流 + 日报 + 周报（本周 vs 上周）
- 深色/浅色主题、高对比度主题、全自绘主题化控件
- i18n：resx 资源化、中英双语、运行时切换（重启生效）
- 自动更新（GitHub Releases 检查 + SHA256 校验 + 可关闭）
- 脱敏诊断包导出
- 默认管理员运行 + 权限受限降级提示（未归属 CPU 行）
- 首次引导、键盘快捷键、行级 UIA 可访问性
- CI：构建 + 测试 + 格式门禁 + 性能门禁 + CodeQL + dependabot
- winget 清单（PR 已提交，见下）

### 待办（按优先级）

| 优先级 | 事项 | 说明 |
|---|---|---|
| P1 | **代码签名** | Azure Trusted Signing 或 OV 证书。没有签名，SmartScreen 会拦、winget 审核会更严。 |
| P1 | **兼容性验证** | Win10 21H2/22H2、Win11 23H2/24H2、150%/200% DPI、双屏混合缩放。需要真机。 |
| P1 | **winget PR 跟进** | PR [microsoft/winget-pkgs#474051](https://github.com/microsoft/winget-pkgs/pull/474051) 状态 OPEN / BLOCKED：CLA 与 Manifest Validation 已过，后续校验（URL、安装扫描、实际安装）待跑。**注意**：0.1.4 的清单是 `Scope: user`，而代码已改为默认管理员运行；下一版应改 `Scope: machine` 并重新提清单。 |
| P2 | **GPU 指标** | 需要 PDH（`\\GPU Engine(*)\\Utilization Percentage`）。`System.Diagnostics.PerformanceCounter` 要引 NuGet，破"零依赖"；纯 P/Invoke 代码量大。**需要单独立项决策**。 |
| P2 | 告警规则化增强 | 现有：整机阈值 + 应用级 CPU 阈值气泡（按应用冷却）。可加：持续时长条件、写入事件流、托盘红点。 |
| P3 | Store / MSIX 分发 | 低优先级。 |
| — | ~~安装包 Scope~~ | 0.2.0 已完成：NSIS 改为机器级安装（`$PROGRAMFILES64\PerfScope`、HKLM 注册、全用户开始菜单、卸载时清理计划任务与 Run 项），与 `Scope: machine` 一致 |

### 已知限制（有意为之，不是 bug）

- 自绘列表/树图只做到"行级可读 + 控件可访问"，没有完整实现 UIA Selection 事件回调
- Core 层仍有少量中文文案（分析事件 note、报告 Markdown），英文界面下日报正文会混中文
- 更新流程不做静默就地替换（托盘程序自我替换风险高），而是提示 + 下载 + 用户确认
- `v0.1.4` 之后 main 上有 13 个提交（未归属 CPU、默认提权、CI 修复等），**尚未发新版本**

---

## 10. 故障排查速查

| 现象 | 原因 | 处理 |
|---|---|---|
| 只有约 200 个进程 | 沙箱按映像路径生效 | 用 `run-dev.ps1`，exe 必须在工作区外 |
| 应用 CPU 合计 ≠ 整机 CPU | 缺"未归属"部分 | 已有「未归属 CPU」行；若仍差很多，检查权限与 DPC 采样 |
| 数字比任务管理器小 | 非管理员 | 确认进程令牌是 admin |
| 启动闪退 | 单实例互斥 | 先 `Get-Process PerfScope` 清理 |
| CI 格式门禁红 | 行尾 / using 顺序 | 跑 `dotnet format`，检查 `.gitattributes` |
| `makensis` 编码报错 | nsi 缺 BOM | 用 UTF-8 BOM 重写 |
| 测试跑不起来 | VSTest 被环境拒绝 | 用 `run-tests.ps1`（反射运行器） |

---

## 11. 给下一个 AI 会话的提示

1. **每轮收尾都要留一个运行中的 dev 实例**（`run-dev.ps1`），并确认标题与数据目录无 `crash.log`。
2. **能截图自证的改动自己验证**（`--mock` 虚构数据 + `scripts/capture-window.cs`），不能截图的（UIA、CI、性能、文档）攒成一批交给用户验收。
3. **`--mock` 绝不用真实数据截图**，避免把用户隐私带进 README / issue。
4. **改 CPU 相关逻辑时同时验证两种口径**（整机 / 单核），并跑一次 CLI 采样对账：
   ```powershell
   PerfScope.Cli.exe --frames 4 --interval 2000 --top 300 --data-dir <临时目录>
   ```
5. **发版与合并都需要用户明确授权**，CI 全绿不等于可以合并。
6. 本机环境细节（沙箱、SDK、端口、凭据位置）不写进仓库，放在工作区 `.meta/` 下。