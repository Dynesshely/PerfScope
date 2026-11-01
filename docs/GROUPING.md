# 应用归并引擎（Grouping）设计说明

> 归属：应用归并实现线（task-2）。代码在 `src/PerfScope.Core/Grouping/`，单测在 `tests/PerfScope.Tests/Grouping/`。
> 契约见 [CONTRACTS.md](CONTRACTS.md)：实现类固定为 `PerfScope.Core.Grouping.AppClassifier`，构造 `(AppSettings settings)`。

## 1. 为什么需要它

用户不关心「哪个进程占用高」，只关心「哪个应用占用高」。但 Windows 上「一个应用」往往是十几个进程：Chrome 的
browser / renderer / GPU / utility、Electron 应用的 renderer / crashpad、VS Code 的 `Code.exe` + 各种 helper。
归并引擎的职责就是把这些进程**挂钩到一个应用**上，输出稳定的 `AppIdentity(Key, DisplayName)`。

`Key` 是稳定标识（跨帧、跨重启一致），UI 按 Key 聚合、历史按 Key 落盘，因此任何会让 Key 抖动的因素都要避免
（本实现里 Key 内嵌路径统一小写，产品名 Key 统一小写）。`DisplayName` 只负责好看，且**同一帧内唯一**。

## 2. 归并决策表（严格按优先级，命中即返回）

| 优先级 | 条件 | Key | DisplayName | Kind |
|---|---|---|---|---|
| 1 | 用户规则 `Exclude` | `rule:exclude:<pattern>` | 规则 pattern（清洗后）；`Excluded=true` | CustomRule |
| 1 | 用户规则 `MergeToName` | `rule:merge:<target 小写>` | rule.Target | CustomRule |
| 1 | 用户规则 `Rename` | 沿用下层 Key | 规则 Target（只改展示名） | 下层 Kind |
| 2 | 路径位于 `%SystemRoot%` 下的 System32 / SysWOW64 / WinSxS / servicing（含 System32\DriverStore），或映像名属于系统组件集合 | `sys:windows` | `settings.SystemGroupName`（默认「Windows 系统」） | SystemProcess |
| 2.5 | 映像名 `explorer`（内置映射，**不并入系统组**） | `exe:explorer.exe` | Windows 资源管理器 | CustomRule |
| 3 | 版本信息 ProductName 有意义 | `product:<company>|<product>`（小写） | 清洗后的 ProductName | ProductName |
| 4 | 解析出安装根且命中「安装根别名」 | 别名的 Key（通常是产品级） | 别名的 DisplayName | InstallRoot |
| 4 | 解析出安装根 | `dir:<安装根 小写>` | 见第 3 节 | InstallRoot |
| 5 | 有路径（或安装根兜底为 exe 目录） | `dir:<目录 小写>` | 见第 3 节 | ExeDirectory |
| 6 | 只有映像名 | `exe:<小写映像名含 .exe>` | 去扩展名的映像名 | ExeName |
| 7 | 什么都拿不到 | `unknown` | `settings.UnknownGroupName` | Unknown |

每条结果的 `Reason` 都写清依据（示例：「版本信息 ProductName「Google Chrome」+ CompanyName「Google LLC」」、
「安装根目录：C:\Program Files\Google\Chrome」、「继承自父进程 chrome.exe」）。

补充规则：

- **用户规则求值顺序**：先筛 `Exclude`（任一命中即排除），其次第一条 `MergeToName`，最后第一条 `Rename`
  （Rename 在下层解析完成后才改写 DisplayName，Key 不变）。规则先于系统进程聚合，所以用户可以把 `notepad.exe`
  从系统组里单独拎出来或排除。
- **无意义 ProductName 过滤**：空、纯数字/纯版本号、单字符、`Microsoft® Windows® Operating System`、`Windows`、
  `Microsoft Windows`、`Unknown`、`Setup`/`Installer` 等通用词、与映像名/文件名相同（含去空格标点后相同）、
  与 CompanyName 相同 —— 一律视为「没有产品名」，继续往下降。
- **展示名清洗**：去商标符号（™ ® ©）、去 `(x64)` / `(x86)` / `(64-bit)` / `(32-bit)` / `- 64-bit`、折叠空白、
  去首尾分隔符；目录名额外去掉开头版本号前缀（仅当后面还有真名字，例如 `1.2.3 AppName` → `AppName`，
  而 `9.9.36-53644` 这种纯版本目录不剥）。
- **父进程继承**（`InheritParentGroup`）：自身只能落到 5/6/7 级（弱归属），而父进程归属是 ProductName / InstallRoot /
  CustomRule（强归属）时，沿用父进程的 Key/DisplayName/RootPath，Reason 记为「继承自父进程 {映像名}」。
  只继承一层；SystemProcess 与 Excluded 不参与继承（避免把用户进程吞进系统组）。

### 为什么还要「安装根别名」

优先级表里「产品名」在第 3 级、「安装根」在第 4 级。同一个应用里主程序通常有 ProductName，而 helper 往往没有
（`crashpad_handler.exe`、`nacl64.exe`、各种宿主进程），于是主程序落到 `product:…`、helper 落到 `dir:…`，同一应用被拆两行。

解决方式：某个 exe 通过版本信息判定身份后，把它所在安装根记入「安装根 → 应用身份」别名表；之后同根下没有版本信息的 exe
在第 4 级命中别名，直接并进产品级 Key。为了让「helper 先被看到、主程序后看到」也能收敛，新增别名时会清掉该安装根下
已缓存的 `dir:<安装根>` 归类结果，下一帧自动并回来（单帧内最多一次瞬时拆行，见第 8 节）。

## 3. 展示名解析（目录级 / 安装根级）

顺序（先命中先返回）：

1. **安装根提示**：UWP 包族名（`名称_发布者ID`）、Steam 游戏名。
2. **目录末段**（可用时优先：它与 Key 一一对应，跨帧最稳定）：清洗版本号前缀后若不属于「不可用目录名」就用它。
3. **版本信息 `FileDescription`**（例如「PerfScope 命令行工具」）：非空、不是通用词、不等于 exe 名时采用。
4. **版本信息 `ProductName`**：同上去噪后采用。
5. **向上找第一个可用祖先目录段**（最多 4 层）：把 `…\PerfScope.Cli\bin\Release\net10.0-windows` 还原成 `PerfScope.Cli`。
6. **exe 基名**（去扩展名）。
7. 兜底组名。

### 「不可用目录名」判定

构建产物目录与通用容器目录都不适合当应用名，命中即跳到第 3 步：

- 正则：`^net\d+(\.\d+)?(-windows)?$`、`^net(fx|coreapp)\d*(\.\d+)*(-windows)?$`、`^v?\d+(\.\d+)+(?:[-_][0-9A-Za-z._]+)?$`；
- 名单：app / apps / application / bin / obj / build / builds / debug / release / dist / out / output / publish /
  artifacts / resources / current / versions / x64 / x86 / win-x64 / win-x86 / arm64 / amd64 / temp / tmp / programs /
  windows / windowsapps / system32 / syswow64 / program files / programdata / users。

实测效果：以往显示成 `net10.0-windows` 的命令行工具，现在显示为工程名（或 FileDescription），不再出现构建目录名。

## 4. 展示名唯一化（DisplayNameRegistry）

即便层级判断正确，也可能出现「同名不同 Key」：同一软件装在两个目录、多账号/多沙箱根目录、UWP 与桌面版并存等。
`DisplayNameRegistry` 保证**同一帧内一个 DisplayName 只对应一个应用**，并按 Key 固定决策（跨帧稳定）。

区分段挑选顺序：

1. 存在某一层尾部路径段使组内所有成员互不相同 → 各取该段。例：`Weixin（Mainland_Apps）` /
   `Weixin（Mainland_Apps_Multi_Account_1）`。
2. 混合场景（有成员拿不到 exe 路径）→ 无路径的标成「进程」，有路径的取「组内唯一且不等于自身名字」的尾部段。
   例：`Everything` + `everything（进程）`。
3. 只有两个成员 → 用最短唯一尾部切片。
4. 都不行 → 序号：`QQNT`、`QQNT（2）`、`QQNT（3）`……（按 Key 排序分配，确定性）。

其他细节：

- 后缀里的 `.exe` 会去掉（`NvContainer（nvcontainer）` 而不是带扩展名）。
- 同一 Key 的展示名第一次登记后就固定，不再因为同 Key 下不同 exe 读到的版本信息不同而抖动。
- 相关 Key 超过 5 分钟不再出现后自动释放（每 1024 次登记顺带清理一次过期组）。
- `OnSettingsChanged` / `ResetCache` 会清空注册表（让改名规则立即生效）。

## 5. 安装根解析（InstallRootResolver）

| 顺序 | 规则 | 安装根 | DisplayNameHint |
|---|---|---|---|
| 1 | `%ProgramFiles%\WindowsApps\<包目录>` | 该包目录 | `名称_发布者ID`（包目录名按 `_` 分段取第 0 段与最后一段） |
| 2 | `%ProgramFiles%` / `%ProgramFiles(x86)%` | 相对路径前两段 `Vendor\App`；只有一段就直接用它 | 无 |
| 3 | `%LOCALAPPDATA%\Programs\<App>` | 该 App 目录 | 无 |
| 3 | `%LOCALAPPDATA%\<Vendor>\<App>` | 两段 | 无 |
| 3 | LOCALAPPDATA 下系统容器（Temp、Packages、Microsoft、CrashDumps、D3DSCache…） | 不适用，降到后续规则 | 无 |
| 4 | `%APPDATA%\<Vendor>\<App>`（系统容器同理降级） | 两段；只有一段就取该段 | 无 |
| 5 | Steam：路径含 `steamapps\common\<Game>` | `<Game>` 目录 | 游戏名 |
| 6 | 通用向上探测：从 exe 目录向上最多 4 层（含自身共 5 个候选，不越过盘符根） | 首个满足特征的目录 | 无 |
| 7 | 都不满足 | exe 所在目录，Kind = ExeDirectory | 无 |

向上探测的「安装根特征」：

- **Electron**：`<dir>\resources\app.asar` 或 `<dir>\resources\app\package.json` 存在（先测：两次 File.Exists 比目录枚举便宜）。
- **卸载器**：`uninstall.exe`、`uninst.exe`、`unins000.exe`、`unins001.exe`，或 `Uninstall*.exe`。

探测时跳过明显不能当安装根的目录：盘符根、Windows 目录及其子目录、Program Files 根及其子目录、LOCALAPPDATA /
APPDATA / ProgramData 根本身以及前述系统容器的一级子目录。

## 6. 缓存与线程安全

`AppClassifier` 内部三张缓存，都是 **LRU 上限 4096 + TTL 10 分钟**（`LruCache<T>`，lock 保护，读时惰性清理过期条目）：

| 缓存 | 键 | 值 | 说明 |
|---|---|---|---|
| 归类缓存 | exe 全路径（不区分大小写；无路径时用「U+0001 前缀 + 映像名」） | `AppIdentity`（未叠加父进程继承） | 热路径 O(1) 字典命中，不读盘、不 new 大对象 |
| 版本信息缓存 | exe 全路径 | `ExeVersionInfo`（读不到也缓存 Empty） | 受 `UseVersionInfoCache` 控制 |
| 安装根缓存 | exe 所在目录 | `InstallRootResult` | 与设置无关，始终缓存 |

- `OnSettingsChanged`：重建不可变设置快照（volatile 字段，热路径只读它），清空**归类缓存**、别名表、展示名注册表；
  版本信息缓存与安装根缓存保留（与设置无关）。
- `ResetCache`：清空全部缓存 + 别名表 + 展示名注册表。
- `UseVersionInfoCache = false`：**不缓存版本信息读数**，归类缓存未命中时重新读盘；归类缓存本身仍生效。
- 线程安全：缓存、别名表（`ConcurrentDictionary`）、展示名注册表（lock）、设置快照都可并发访问；
  `Classify` 从不抛异常，任何意外都降级为 `unknown` 兜底组，绝不拖垮采样线程。
- 展示名去重对热路径的开销：一次 lock + 1~2 次字典查找（实测 500 次归类约 1ms）。

## 7. 实测（本机 NativeCapture 真采样）

- 进程 **984 ~ 1055** 个 → 归并成 **183** 行（含系统组），排除 0 个；`DisplayName` 重复行数 **0**。
- 归并来源：SystemProcess 1 行（253 进程）、ProductName 约 50 行、InstallRoot 约 38 行、ExeDirectory 约 11 行、
  ExeName 约 85 行（拿不到 exe 路径）、CustomRule 1 行（explorer）。
- **Chrome**：19~35 进程 → 1 行（`product:google llc|google chrome`）。
- **Edge**：178 进程 → 1 行；另有独立的 **Edge WebView2** 30 进程 → 1 行（两者 ProductName 不同，属合理拆分）。
- **Windows 系统**：253 进程 → 1 行。
- **explorer.exe**：独立一行「Windows 资源管理器」，不并入系统组。
- **构建产物目录**：不再出现 `net10.0-windows` 这类行；命令行工具显示为工程名 / FileDescription。
- **同名不同安装目录**：Weixin → `Weixin（Mainland_Apps）` / `Weixin（Mainland_Apps_Multi_Account_1）`；
  QQNT 出现 4 行时用 `QQNT` / `QQNT（2）` / `QQNT（3）` / `QQNT（4）`。
- 性能：热路径 500 次 `Classify` 约 **1ms**（约 2µs/次）；全量冷启动首轮归类约 **155~240ms**（文件缓存已热）。

## 8. 已知限制与风险

1. **首帧冷启动**：文件缓存与杀软拦截都冷时，首轮要读约 170 个 exe 的版本信息，最坏实测约 5.9s（热态 0.2s）。
   只发生一次，但会让第一帧出现较晚；引擎侧可考虑首帧先出系统级数据。
2. **别名 / 去重都有一帧收敛**：第一帧可能出现「原名 + 带后缀」的过渡形态，第二帧起稳定。
   若 UI 对单帧抖动敏感，可在分类前预热一轮。
3. **拿不到 exe 路径的进程只能按文件名归并**（`exe:<name>`）。实测约 85 个此类分组，主要来自跨用户 / 受保护 / 沙箱进程；
   Native 线的 ProcessPathResolver 增强后会自动升级到更高级别，本模块无需改动。
4. **ProductName 归并是「宽」而不是「准」**：同厂商同产品名的不同版本会并成一行（符合预期）；反过来个别应用把
   ProductName 写成通用词会被过滤，退化到安装根级（可接受）。
5. **UWP 展示名**依赖包目录名（`Microsoft.WindowsCalculator_8wekyb3d8bbwe`），不是本地化友好名；
   如需友好名可在展示层再做一次映射。
6. **不同沙箱/多账号根目录**会被解析成不同安装根 → 同一应用多行，靠展示名后缀区分（实测 Weixin、QQNT、Feishu 均如此）。
   这是安装目录本身不同导致的，属预期。
7. **规则 ProductName 匹配是「包含」语义**，短 pattern 可能误伤，建议 UI 提示用户写长一点。
8. **QQNT 这类 4 行同名应用**在单段路径无法区分时退化为序号后缀，不如路径后缀直观，但确定性更好、不会出现超长串。

## 9. 集成注意（给 Lead / 其他实现线）

- 构造：`new AppClassifier(settings)`（契约签名）。单测/嵌入场景可用
  `new AppClassifier(settings, IFileSystemProbe probe)` 注入假文件系统。
- 设置热更新必须调用 `OnSettingsChanged(settings)`（会清归类缓存、别名表、展示名注册表）；只改设置对象不调用不会生效。
- `Classify(process, parent)` 的 parent 由合成器按本帧进程表查好传进来；传 `null` 只是失去继承能力，不会出错。
- `Excluded == true` 的进程由合成器过滤，不进列表、不计入总数；Key 仍然稳定可读。
- 归并只读 `AppSettings` 里的 `GroupByProductName` / `GroupByInstallRoot` / `InheritParentGroup` /
  `MergeWindowsSystemProcesses` / `UseVersionInfoCache` / `UnknownGroupName` / `SystemGroupName` / `Rules`。
- 产品级身份现在会带上 `RootPath`（安装根），UI 取图标 / 打开目录可用。
- 单测全部使用假文件系统（`FakeFileSystemProbe`），不依赖本机真实路径，可在任意机器稳定运行。
