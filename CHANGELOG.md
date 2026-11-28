# Changelog

All notable changes to PerfScope are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-11-28

### Changed

- **安装包改为机器级安装**：装到 `%ProgramFiles%\PerfScope`，注册信息写 HKLM，开始菜单用全用户上下文，
  与 `Scope: machine` 的 winget 清单一致；卸载时清理计划任务与 HKCU Run 里的自启项。
- UIA 补齐并实测：列表提供原生 `SelectionPattern`、每行提供 `SelectionItemPattern`，
  行名称 + HelpText 摘要（CPU / 内存 / 进程数 / IO）可被屏幕阅读器读取。
- **分布视图的方块一律显示应用图标**：有标题时贴右下角（空间紧时缩到 13px），标题放不下时
  图标在块内居中并按块大小缩放，再小的块也能认出是哪个应用。
- **内存占用优化**：折叠的应用不再逐帧刷新子进程行、每帧临时集合改为复用、工作集修剪
  从 5 分钟一次改为 30 秒一次；实测后台工作集均值 144 → 107 MB、分配速率 3.81 → 2.3~3.2 MB/s、
  整机 CPU 1.09% → 0.4~0.5%。
- **分布视图补上中性块**（与旧 WinForms 版一致）：内存视图加入「未归属（内核 / 缓存 / 共享）」
  与「空闲内存」，CPU 视图加入「空闲 CPU」，整张图的面积口径分别等于物理内存总量与 100%；
  工作集之和超过「在用内存」时应用块等比缩小，空闲块用点阵、未归属块用斜纹区分。
- 分布视图的方块改为**直角**，不再有圆角。
- **界面文案全部走本地化**：XAML 里的中文改用 {loc:Loc '原文'} 标记扩展求值，
  Strings.en.resx 补齐界面与报表两套英文条目；Core 层新增 CoreText（可注入翻译器，
  默认恒等），日报 / 周报 / Agent Prompt / 事件说明在英文界面下输出英文。
- **GUI 层整体重写为 WPF + MVVM**：外壳换成自绘标题栏的 WPF 窗口壳（保留系统原生最小化/最大化动画与
  Win11 Snap Layouts），界面全部改为数据绑定驱动 —— 视图不再持有业务状态，主列表、分布视图、趋势图
  的数据装配都在 ViewModel 里完成。WinForms 工程与全部自绘控件代码已删除。
- **分布视图（树图）改为数据驱动**：Squarified 布局算法下沉到 `PerfScope.Core.Analysis.TreemapLayout`
  （纯几何、带单测），ViewModel 输出像素矩形，视图只负责绘制。
- **托盘改为纯 P/Invoke 的 Shell_NotifyIcon + WPF 菜单**，不再依赖 WinForms 的 NotifyIcon；
  关闭到托盘、暂停/继续、开机自启、退出等入口保持一致。
- **设置页重写**为 7 个标签页的完整表单（采样 / 界面 / 归并规则 / 历史 / 告警 / 智能分析 / 诊断），
  自定义规则改成「左侧列表 + 右侧内联编辑」，不再弹独立对话框。
- 主题改为三套调色板（深色 / 浅色 / 高对比度）资源字典，换主题即时生效，不需要重建窗口。

- Header cards are slightly taller and the trend axis labels are aligned correctly, so the hover axes stay
  readable.
- Path-fragment matching for system modules is now case-insensitive, so differently-cased install paths no
  longer miss the intended module.

### Added

- **UIA 行级数值**：应用行的 CPU / 内存 / 进程数 / IO 合成一句摘要暴露给屏幕阅读器（AccessibilitySummary）。
- **趋势图悬停坐标轴**：顶部 CPU / 内存卡片内嵌折线图，鼠标悬停显示该点数值（`--hover-demo` 可强制显示）。
- 主界面列头可点击切换排序（CPU / 内存 / 应用名 / 进程数 / IO），排序状态记入 `ui.json`。

- **Drivers & firmware** system module (`驱动与固件`): driver services and OEM hardware helpers
  (Armoury Crate, AMD/ATI services, Realtek, DriverStore helpers, …) are grouped into one readable row
  instead of showing up as unrecognised processes.
- Application grouping now recognises **release channels**, so e.g. Microsoft Edge stable and Edge Beta
  are reported as two separate applications instead of being merged into one.
- **Weekly report**: the toolbar gained a `Weekly` button (and `PerfScope.Cli --weekly`) that compares this
  week with the previous week and writes `analysis/<date>-weekly.md|json`.
- **High contrast support**: when Windows enables a high contrast theme, the palette switches to system colours.
- **Accessibility**: the application list exposes a UI Automation row tree (name, CPU, memory, process count,
  selection state) and the self-drawn surfaces report accessible names, roles and descriptions.
- **Keyboard shortcuts**: F5 refresh, Ctrl+, settings, Ctrl+H history, Ctrl+Shift+A agent hand-off,
  Ctrl+Shift+D distribution view.
- **First-run onboarding**: a one-time three-point introduction (applications vs processes, hover hints,
  history / AI hand-off), reopenable from `Settings → Interface`.
- **Automatic updates**: PerfScope checks GitHub Releases once at start-up (can be disabled in
  `Settings → Diagnostics`) and shows a tray balloon when a newer version is available.
  The portable archive is downloaded with SHA-256 verification against `SHA256SUMS.txt`.
- **Redacted diagnostic bundle**: `Settings → Diagnostics → Export diagnostic bundle` packages the logs,
  a redacted settings snapshot, recent insights and environment details into one zip
  (machine name, user name and user profile paths are replaced with placeholders).
- **Limited-permission hint**: when PerfScope runs without administrator rights and many processes cannot
  be inspected, a clickable hint appears in the status bar offering a restart with elevation.
- **Localization (i18n)**: UI strings are now resource-based (`src/PerfScope.App/Localization`),
  with English translations in `Strings.en.resx`; the language can be switched in Settings
  (Follow system / 中文 / English) and is stored in `settings.json`.
  Untranslated strings fall back to Chinese instead of showing empty text.

### Fixed

- **修复列表虚拟化失效**：自定义 ListBox 模板补上 `CanContentScroll`，否则所有行都被实例化并每帧重排
  （实测 TopN=30 时单核占用 16.7% → 修复后整机 0.32%）。
- 分布视图改为增量更新（不再每秒 Clear/Add 瓷砖），画笔按应用键缓存。
- 主题切换不再清空并重取所有应用图标；可访问性摘要改为每 5 帧通知一次，减少 UIA 事件。
- 修复「权限受限」徽标的自赋值死代码，改为按是否提权判定；系统模块名恢复 UI 层本地化查表。
- CHANGELOG 里「发行通道」条目被拆成两行的排版错误。

## [0.1.2] - 2026-11-02

### Added

- Custom-drawn combo boxes and dark scrollbars, so dropdowns and scrollbars follow the dark theme instead of
  keeping the system look.
- Hovering a child process row inside an expanded application now shows the full executable path.

### Changed

- Version bumped to 0.1.2.

## [0.1.1] - 2026-11-02

### Fixed

- Settings page now adapts fully to the dark theme (group boxes, labels and controls).
- Custom grouping rules layout no longer clips or misaligns rows.
- Dialogs now show the PerfScope application icon.

### Changed

- Version bumped to 0.1.1.

## [0.1.0] - 2026-11-02

First public release.

### Added

- **Application-level monitoring**: processes are merged into applications using version information, install
  roots, directory trees and parent-process inheritance; the reason behind every grouping is shown in the UI.
- **System feature modules** — Windows Subsystem for Linux, virtual machines & containers, graphics & GPU
  drivers, security, search & indexing, Windows Update and the kernel are reported as their own rows; other
  system components collapse into a `Windows 系统` group, while `explorer.exe` stays a normal application.
- **Distribution maps** for memory and CPU: treemaps with proportional areas, icons in each block, a self-drawn
  hover card, and an idle slice in the CPU view.
- **Header cards** with live system CPU and physical memory, plus hover trend curves with axes, crosshair and
  the exact value at any moment.
- **History and insights**: minute-level aggregation on disk with a configurable retention, noticeable episodes
  (CPU bursts, memory growth, system pressure) and a readable daily report.
- **Agent handoff**: one click copies a ready-to-paste prompt with real paths, metric definitions and
  preliminary findings.
- **Tray-first UI**: single instance, start with Windows, alert balloons, dark theme, and a settings page where
  sampling, smoothing, grouping, retention and analysis thresholds are adjustable and applied live.
- **Headless CLI** (`tools/PerfScope.Cli`) for frames, process lists, JSON output, reports and benchmarks.
- **Mock data mode** and a screenshot script, used to produce the images in `docs/images`.
- **Packaging and releases**: self-contained single-file portable build, per-user NSIS installer, and a
  tag-driven GitHub Actions workflow that publishes both artefacts together with `SHA256SUMS.txt`.
- English README, MIT license and project conventions in `AGENTS.md`.

### Notes

- No third-party runtime dependencies, no administrator rights required, no network access and no telemetry.
- Sampling uses a single `NtQuerySystemInformation` batch query per frame with differential composition, and
  automatically throttles while the window is hidden.

[Unreleased]: https://github.com/Dynesshely/PerfScope/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Dynesshely/PerfScope/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Dynesshely/PerfScope/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Dynesshely/PerfScope/releases/tag/v0.1.0
