# 架构设计

PerfScope 的目标不是`功能最多的监控器`，而是**能真的赖在后台一整年、几乎感觉不到它存在**的监控器，
同时把使用者真正关心的问题回答清楚：**哪个应用占了 CPU / 内存**，而不是哪个 PID。

## 1. 关键约束

| 约束 | 做法 |
|---|---|
| 自身开销要低 | 一次系统调用拿全表 + 后台线程 + 无 UI 依赖的 Core；UI 关闭后只剩采样循环 |
| 能长期常驻 | 内存有上限的环形缓冲、磁盘按天定长记录、定期裁剪、日志轮转、异常不杀循环 |
| 用户只关心应用 | 归并引擎把进程挂到`应用`上，基于版本信息 + 安装根目录 + 目录树综合分析 |
| 参数可调 | 所有采样/归并/留存/界面参数集中在设置页，保存后热生效 |
| 可验证 | 界面状态栏显示监控器自身 CPU / 内存；附带 measure.ps1 实测脚本 |

## 2. 分层

    PerfScope.App (WinForms)
      ├─ TrayAppContext      托盘常驻、单实例、开机启动、告警气泡
      ├─ MainForm            应用级列表、趋势线、展开进程明细
      └─ SettingsForm        全参数设置页（分组 Tab + 校验 + 热生效）
             │ 只依赖接口
    PerfScope.Core
      ├─ Abstractions        ICapture / IAppClassifier / ISnapshotComposer / IMonitorEngine
      │                      IHistoryStore / ISettingsStore / ILog
      ├─ Native              NtQuerySystemInformation 批量采样、进程路径解析
      ├─ Sampling            差分合成、平滑、聚合、常驻引擎
      ├─ Grouping            应用归并（版本信息 / 安装根 / 目录 / 规则 / 父进程继承）
      ├─ History             内存趋势 + 磁盘分钟聚合
      ├─ Settings            设置模型、校验、原子读写
      └─ Diagnostics         轻量文件日志（轮转）

依赖方向单向：App -> Core，Core 内部不反向依赖 UI；Core 不引用 WinForms。

## 3. 采样：为什么便宜

逐进程收集（OpenProcess + GetProcessTimes / PerformanceCounter）在 300+ 进程的机器上每轮要几百次系统调用、
还会触发对受保护进程的重复失败。PerfScope 改为：

1. **一次** Read 全部进程结构：NtQuerySystemInformation(SystemProcessInformation)，缓冲区跨帧复用，
   只有结构变大时才扩容重试。单帧耗时通常在毫秒级以内（实测见验收报告）。
2. **只对首次出现的 PID** 解析 exe 全路径（OpenProcess + QueryFullProcessImageNameW），
   用 (pid, 创建时间) 做缓存键，失败做负缓存，避免每帧重复撞权限墙。
3. 系统 CPU 用 SystemProcessorPerformanceInformation 逐核累加；内存用 GlobalMemoryStatusEx。
4. 差分：CPU% = ΔcpuTicks / (Δ经过ticks × 逻辑核心数) × 100；Δ时间用 Stopwatch 测，防系统时钟跳变。
   PID 复用（创建时间变化）、新进程（首帧记 0）、负增量、异常大值都做了防护。
5. 平滑：按应用维护最近 N 帧滑动平均，抑制单帧毛刺（N 可配置，1 表示关闭）。

采样线程优先级 BelowNormal，用可中断等待而不是 Timer 回调；窗口隐藏时按设置自动降频
（默认 2s → 5s），把`没人看的时候`的成本压到接近零。

## 4. 归并：从进程到应用

归并优先级（命中即停，并把依据写进界面的`归并依据`提示，保证结果可解释、可质疑）：

1. **用户规则**（自定义）：合并到指定应用 / 只改名 / 排除。
2. **Windows 系统组**：System32 / SysWOW64 / WinSxS 等目录下的系统组件与 svchost 等合并成一行，
   避免几十个系统进程刷屏；但 explorer.exe 这类位于 C:\Windows 根目录的程序仍按普通应用处理。
3. **版本信息**：exe 的 ProductName + CompanyName。Chrome 的 20 多个进程、Electron 应用的
   renderer/gpu/utility 进程、VS Code 的多个 helper，ProductName 都是一致的，天然合并。
4. **安装根目录**：Program Files 取 Vendor\App；LOCALAPPDATA\Programs 取 App；
   WindowsApps 取 UWP 包族名；steamapps\common\Game；以及向上探测卸载器 /
   Electron 的 resources\app.asar 特征来定位安装根。
5. **可执行文件所在目录**。
6. **可执行文件名**：拿不到路径时的兜底。
7. **兜底组**。

另外支持`父进程继承`：当某个进程自身只能落到弱归属（目录/文件名/兜底）而父进程有强归属时，
沿用父进程的应用归属（例如某些安装器拉起的子进程）。

所有解析结果都进缓存（LRU + TTL），热路径上只是字典命中，不会每帧读盘。

## 5. 存储：内存有界、磁盘可预期

- **内存**：只为 CPU 排名靠前的 N 个应用（默认 12）各维护一条定长环形缓冲，容量 = 保留分钟 × 60s / 采样间隔。
  默认 60 分钟、2s 间隔 → 每应用 1800 点 × 16 字节 ≈ 29KB，12 个应用不到 350KB。
- **磁盘**：按天一个文件，**每分钟每条应用一条 32 字节定长记录**（appId、分钟序号、平均 CPU、峰值 CPU、
  峰值工作集）。appId 由 apps.json 目录表分配。一天 30 个应用 × 1440 分钟 × 32B ≈ 1.4MB（实际只有活跃应用）。
  读取时忽略尾部残缺记录，崩溃不会破坏历史。
- 保留策略默认 30 天，启动后按文件名日期裁剪；写入节流（跨分钟或每 30 秒批量落盘），不阻塞采样线程。

## 6. 界面

- 主窗口只在需要时创建，关闭即释放；托盘常驻只用 NotifyIcon。
- 列表用虚拟列表自绘（不每行 new 控件），图标、字体、画笔全部缓存复用；双缓冲防闪烁。
- 引擎帧事件经 **UI 刷新间隔**节流后再重绘，采样频率与重绘频率解耦（采样 2s、界面 1s 也不重复绘制）。
- 状态栏常显监控器自身开销，作为`轻量`的自我证明。

## 7. 目标与实测

| 指标 | 目标 | 实测（Ryzen 9 9950X / 32 线程 / 250 进程） |
|---|---|---|
| 自身 CPU（默认 2s 采样，窗口隐藏） | < 0.1% | **0.011~0.015%**（整机口径），峰值 0.33% |
| 自身工作集（窗口隐藏） | < 55 MB | **26~33 MB**（私有 18 MB） |
| 自身 CPU / 内存（窗口打开） | < 0.5% / < 120 MB | **0.071% / 94 MB**（自绘列表 + 双缓冲 + 图标缓存） |
| 单帧采样耗时（251 进程） | < 25 ms | **17~20 ms**（内核批量查询占绝大部分） |
| 每帧托管分配 | < 200 KB | **19.8 KB** |
| 磁盘：30 天 | < 60 MB | 约 40 MB（每分钟 30 条 × 32 B） |
| 连续运行句柄增长 | 稳定 | 5 分钟内收敛（+2） |

> 进程数到 1000+ 时单次采集升到 54~75 ms（内核查询本身 64 ms），此时建议保持 2 s 以上间隔。
