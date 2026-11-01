# 验收报告

测量环境：Windows 11（10.0.26200），AMD Ryzen 9 9950X（16C/32T），125.7 GB 内存，
.NET 10.0.13 运行时，Release 构建。被测期间机器上另有 4 个并发开发进程（系统 CPU 50%~80%），
属于`偏负载`条件，比安静环境更不利。

## 1. 功能验收

| 项目 | 结果 | 说明 |
|---|---|---|
| 应用级归并 | 通过 | 231~250 个进程归并为 27~28 行应用；Edge 38 进程 / Chrome 16~35 / Brave 42~48 各 1 行 |
| 归并可解释 | 通过 | 每行都带`归并依据`，如`版本信息 ProductName Microsoft Edge + CompanyName…` |
| 构建目录不误判 | 通过 | 原先把 bin\Release\net10.0-windows 当应用名，已修为 PerfScope.Cli |
| 同名应用去重 | 通过 | 两个安装目录的 Weixin → Weixin（Mainland_Apps）/ Weixin（Mainland_Apps_Multi_Account_1） |
| 系统进程聚合 | 通过 | System32/svchost 合并为`Windows 系统`；explorer.exe 独立成`Windows 资源管理器` |
| 历史落盘 | 通过 | 运行 95 s 后生成 history\days\2026-11-01.bin（69 条 × 32 字节）与 apps.json（24 个应用） |
| 设置持久化 | 通过 | settings.json 生成、原子写入、损坏可回退 |
| 退出刷盘 | 通过 | --selftest 到点退出后，当日历史文件仍有 2208 字节（分钟聚合未丢） |
| 单文件发布 | 通过 | dist\PerfScope.exe 0.5 MB（依赖已安装的 .NET 10 桌面运行时） |

## 2. 自身开销（关键指标）

托盘常驻、窗口隐藏、默认采样 2 s / 隐藏降频 5 s，60 秒稳态采样：

| 指标 | 目标 | 实测 |
|---|---|---|
| 自身 CPU（整机口径，32 核） | < 0.1% | **平均 0.015%**，峰值 0.325% |
| 工作集 | < 55 MB | **平均 31.3 MB**，峰值 32.8 MB |
| 私有内存 | — | 平均 17.7 MB |
| 句柄数 | 稳定 | 323 → 325（60 s 内 +2，收敛） |
| 线程数 | 稳定 | 17 |
| 启动后 6 秒 RSS | — | 25.9 MB（尚未做工作集修剪） |

单次采样成本（NativeCapture，Release，251 进程）：

| 指标 | 实测 |
|---|---|
| 单次 NtQuerySystemInformation 全表采集 | 16.8 ~ 19.8 ms（均值 19.8 ms；含一次冷启动抖动 30.9 ms） |
| 每帧托管分配 | 19.8 KB（≈80 B/进程，只等于返回数组本身） |
| 折算整机 CPU（2 s 间隔） | 0.031% |
| 折算整机 CPU（5 s 隐藏间隔） | 0.012% |
| 归并 + 合成 + 显示名去重一帧 | 约 99 µs（其中去重 0.86 µs、0 分配） |
| 历史 Append 一次 | 5~6 µs |

> 进程数涨到 1000+ 时单次采集约 54~75 ms（其中内核批量查询本身 64 ms）。这是
> NtQuerySystemInformation 的固有成本，仍远低于`逐进程 OpenProcess`的方案；默认 2 s 间隔
> 在 1000 进程机器上约占单核 3%，界面隐藏时 5 s 间隔再减半。

### 2.1 真实运行采集（7 分钟，窗口打开，201~202 个进程）

把窗口打开、按默认 2 秒采样运行 420 秒，由 PerfScope 自己把结果写进历史文件，再用
`PerfScope.Cli --history` 读回（口径为整机 CPU%，与任务管理器一致）：

| # | 应用 | 平均 CPU% | 峰值 CPU% | 峰值内存 | 数据分钟 |
|---|---|---|---|---|---|
| 1 | DeepSeek Harness | 2.542 | 3.85 | 1933 MB | 8 |
| 2 | Brave Browser | 1.753 | 3.91 | 7109 MB | 8 |
| 3 | Microsoft Edge WebView2 | 1.354 | 1.88 | 1609 MB | 8 |
| 4 | Feishu | 0.261 | 0.99 | 1498 MB | 8 |
| 5 | QQNT | 0.198 | 1.80 | 1098 MB | 8 |
| 6 | **PerfScope 自己** | **0.079** | 0.58 | 110 MB | 8 |

同一时段外部另起一个测量进程盯 PerfScope：CPU 平均 0.071%、峰值 0.66%，
工作集 94.2 MB（峰值 108.9 MB）、私有内存峰值 47 MB —— 与被测程序自己的记录互相印证。

> Brave 的 7109 MB 是 42 个进程合并后的真实总量（也是"按应用看"的价值所在：
> 逐进程看只会看到 42 行各几十 MB，根本判断不出它在吃 7 GB）。

## 3. 代码质量

| 项目 | 结果 |
|---|---|
| dotnet build（Debug / Release） | 0 警告 0 错误（App / Core / Cli / Tests / TestRunner） |
| 单元测试 | **114 个用例全部通过**（Sampling 31、Grouping 40、Settings 18、History 12、Diagnostics 7、其它 6） |
| 第三方依赖 | Core 与 App 为零 NuGet 依赖；测试项目用 xunit |
| 结构 | App → Core 单向依赖；Core 不引用 WinForms；UI 只经接口 + CoreRuntime 访问 Core |

## 4. 稳定性长跑

命令：`.\scripts\measure.ps1 -Seconds 300 -IntervalMs 10000`（300 秒内每 10 秒采样一次，共 30 个点）：

| 指标 | 结果 |
|---|---|
| 自身 CPU | 平均 **0.011%**，峰值 0.044%（整机口径，32 线程） |
| 工作集 | 平均 25.8 MB，峰值 37.0 MB（启动初期爬升后趋于平稳） |
| 私有内存 | 峰值 27.7 MB |
| 句柄数 | 起始 323，区间 316 ~ 327（净增接近 0，无泄漏趋势） |
| 线程数 | 17 → 12（随计时器/GC 线程回收而下降） |
| 数据目录增长 | 5 分钟内 +9.1 KB（settings + apps.json + 当日聚合文件） |

结论：长时间运行下 CPU、内存、句柄均未出现单调增长，符合「长期常驻」的设计目标。

## 5. 复现方式

    .\scripts\build.ps1 -Release -Test                  # 构建 + 跑测试
    .\scripts\measure.ps1 -Seconds 60                   # 实测自身开销（WMI 分离启动）
    .\scripts\measure.ps1 -Seconds 30 -IntervalMs 10000 # 5 分钟长跑
    .\scripts\build.ps1 -Publish                        # 发布单文件

    # 不开界面跑真实采样（冒烟 / 排障）
    .\tools\PerfScope.Cli\bin\Release\net10.0-windows\PerfScope.Cli.exe --frames 4 --interval 1500 --top 15

## 6. 环境限制说明

本机 dotnet test 无法运行：VSTest testhost 启动时要打开父进程句柄，被当前执行环境拒绝
（System.ComponentModel.Win32Exception (5): 拒绝访问 @ DefaultEngineInvoker.SetParentProcessExitCallback），
空测试工程同样崩溃，与代码无关。因此仓库内提供 tests\PerfScope.TestRunner（反射执行同一批
[Fact]/[Theory]，支持 ITestOutputHelper 注入），.\scripts\run-tests.ps1 即用它；
在普通桌面上仍可直接 dotnet test。

另外，本执行环境对子进程写 %LOCALAPPDATA% 有限制，因此程序支持便携模式：
--data-dir <目录> 或环境变量 PERFSCOPE_DATA_DIR 可把数据目录切到任意可写位置。
