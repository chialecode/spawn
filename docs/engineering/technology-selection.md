# SPAWN 工程技术选型、开源参考矩阵与开发环境全表（Technology Selection & Environment）

> **文档状态**：Active（M1 基线锁定 — `.NET 10 LTS`）
> **适用范围**：38+ 活跃开源项目调研与 `Apache-2.0` 许可证合规矩阵、按进程隔离域划分的 `.NET 10` NuGet 包清单、`CsWin32` 原生 API 映射表、正式编码前本机必备与可选环境安装全表及 `gbe_fork` 资源准备规范
> **关联文档**：[ADR-0002 UI 技术栈](../decisions/0002-pure-native-wpf-and-nodify-ui-stack.md) · [ADR-0003 反作弊隔离](../decisions/0003-anti-cheat-guard-and-process-isolation.md) · [系统顶层架构](../design/system-architecture.md) · [开源与架构规范](../dev-rules/architecture-and-open-source.md) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 核心技术底座总览

| 技术层次 | 锁定选型 | 精确目标版本 / 规范 | 选型核心理由 |
| :--- | :--- | :--- | :--- |
| **运行时与语言** | `.NET 10 (LTS)` + `C# 14` | `net10.0` / `net10.0-windows10.0.26100.0`（最低 OS 基线 `10.0.22621.0`） | 长期支持（LTS）生命周期，原生支持 Kestrel `gRPC over Named Pipes`、硬件 SIMD（`XxHash3`/AVX2）、增强动态 PGO 与极致桌面启动性能 |
| **主桌面 UI 框架** | 原生 WPF + `WPF-UI` + `CommunityToolkit.Mvvm` | `WPF-UI 4.x` / `CommunityToolkit.Mvvm 8.4+` | 原生 Windows 11 Fluent 2 + Mica 材质，零 `WebView2` 内存膨胀与空域撕裂 |
| **类 UE 蓝图画布** | `Nodify` (`miroiu/nodify`) | `7.x` | 专为 Unreal Kismet 风格打造的高性能虚拟化节点画布，千节点平移缩放稳超 60FPS |
| **高刷 Overlay 渲染** | `DirectComposition` + `DXGI Flip-Model` + `SkiaSharp` | `SkiaSharp 3.x` + `ZBID_UIACCESS` | 144Hz/240Hz 零掉帧硬件合成透明图层，对目标游戏零注入 |
| **本地元数据库** | SQLite 3 (`Microsoft.Data.Sqlite`) | `10.0.x`（`WAL` 单写多读模式） | 零服务部署、微秒级预编译查询、崩溃强一致 |
| **去重压缩与哈希** | 自研确定性 `FastCDC` + `Blake3` + `ZstdSharp` + `XxHash3` | `Blake3 1.x` / `ZstdSharp.Port 0.8+` | `>1.5 GB/s` 分块与校验吞吐，跨平台无重型 C++ 运行库束缚 |
| **跨进程 IPC** | `Grpc.AspNetCore` / `Grpc.Net.Client` + `MemoryMappedFile` | `2.67+` | 命名管道 SDDL 细粒度跨完整性级别鉴权 + 三缓冲共享内存 60FPS 零拷贝传图 |
| **原生 Win32 互操作** | `Microsoft.Windows.CsWin32` | `0.3.x+` Source Generator | 编译期按需生成类型安全 P/Invoke 签名，杜绝手工结构体内存对齐错误 |

---

## 2. 经查证的 38+ 开源项目参考与 `Apache-2.0` 合规性矩阵

SPAWN 核心仓库采用 **`Apache-2.0`** 开源许可证。所有外部开源项目严格划分为四种合规接入模式：
- **模式 A（宽松协议 NuGet 直接编译引用）**：`MIT` / `Apache-2.0` / `BSD`，与 `Apache-2.0` 100% 天然兼容。
- **模式 B（`LGPL-3.0` 独立 DLL 动态链接）**：仅在独立 Worker 或可选赋能层以未修改动态库（`.dll`）加载，随发行包附带 `THIRD-PARTY-NOTICES` 并保障用户自由替换同 ABI 动态库权利，不传染宿主源码。
- **模式 C（纯外部独立进程 CLI / RPC 互操作）**：针对 `GPL-3.0` / `AGPL-3.0` 生态工具，仅通过命令行参数、标准输入输出、公开配置文件（JSON/YAML）或本地 Socket RPC 编排用户自行下载的独立可执行文件，**零源码复制、零静态/动态库链接**，严格处于操作系统进程隔离边界之外。
- **模式 D（仅架构思想或公开格式规范参考，100% 纯 C# 从零自研）**：仅调研其公开文件格式或系统 API 用法，完全独立编写代码。

| 序号 | 领域 | 开源仓库 (`owner/repo`) | 开源协议 | SPAWN 接入模式 | 用途与合规边界说明 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | UI / 桌面 | `lepoco/wpfui` | MIT | 模式 A（NuGet） | 主窗口 `FluentWindow`、Mica 云母材质与导航控件库 |
| 2 | UI / 蓝图 | `miroiu/nodify` | MIT | 模式 A（NuGet） | 类 UE 蓝图编辑器画布、引脚连线与视口虚拟化 |
| 3 | UI / MVVM | `CommunityToolkit/dotnet` | MIT | 模式 A（NuGet） | `CommunityToolkit.Mvvm` Source Generator 无反射绑定 |
| 4 | UI / 托盘 | `HavenDV/H.NotifyIcon` | MIT | 模式 A（NuGet） | 纯原生 WPF XAML 系统托盘图标与快捷弹出浮窗 |
| 5 | UI / 列表 | `sbaeumlisberger/VirtualizingWrapPanel` | MIT | 模式 A（NuGet） | 万级游戏海报墙流式网格 UI 虚拟化容器 |
| 6 | 系统互操作 | `microsoft/CsWin32` | MIT | 模式 A（NuGet） | 编译期按需生成精确 Win32 / COM P/Invoke 绑定 |
| 7 | 2D 图形 | `mono/SkiaSharp` | MIT | 模式 A（NuGet） | `OverlayHost` 144Hz 动效绘制、灰阶成就图标合成与战报长图渲染 |
| 8 | 机器视觉 | `shimat/opencvsharp` | Apache-2.0 | 模式 A（NuGet） | `BlueprintWorker` 多尺度模板匹配与特征检测 |
| 9 | 离线 OCR | `sdcb/PaddleSharp` | Apache-2.0 | 模式 A（NuGet） | `BlueprintWorker` 基于 `Sdcb.PaddleOCR` 的纯离线游戏文本识别 |
| 10 | 音频控制 | `naudio/NAudio` | MIT | 模式 A（NuGet） | 全声卡端点 `WASAPI` 会话监控，实现外部修改器零注入微秒级静音 |
| 11 | 虚拟手柄 | `nefarius/ViGEm.NET` | MIT | 模式 A（NuGet） | `BlueprintWorker` L3 虚拟 Xbox 360 / DualShock 4 控制器客户端 |
| 12 | 虚拟驱动 | `nefarius/ViGEmBus` | BSD-3-Clause | 可选系统驱动 | L3 虚拟手柄底层内核总线驱动（引导按需通过 `winget` 安装） |
| 13 | 密码学哈希 | `xoofx/blake3.net` | BSD-2-Clause | 模式 A（NuGet） | CAS Blob、存档 `FastCDC` 分块与 `SPPK` 包完整性超高速哈希 |
| 14 | 无损压缩 | `oleg-st/ZstdSharp` | MIT | 模式 A（NuGet） | 纯托管高性能 Zstandard 压缩/解压流 |
| 15 | 规则编译 | `aaubry/YamlDotNet` | MIT | 模式 A（NuGet） | 离线构建阶段解析 `ludusavi-manifest` YAML 编译入 SQLite |
| 16 | 存档拷贝 | `alphaleonis/AlphaVSS` | MIT | 模式 A（NuGet） | 游戏持有排他文件锁时的 Windows VSS 卷影复制兜底 |
| 17 | 二期特征码 | `Reloaded-Project/Reloaded.Memory.SigScan` | LGPL-3.0 | 模式 B（动态 DLL） | 仅二期（M6）在 `TrainerWorker` 内动态加载执行 AVX2 AOB 扫描 |
| 18 | 二期反汇编 | `icedland/iced` | MIT | 模式 A（NuGet） | 仅二期（M6）在 `TrainerWorker` 内执行 x86/x64 指令重定位与跳板生成 |
| 19 | 启动器参考 | `JosefNemec/Playnite` | MIT | 模式 D（思想参考） | 参考平台游戏库扫描与生命周期状态抽象，原生 C# 独立实现 |
| 20 | Mod 架构参考 | `Nexus-Mods/NexusMods.App` | GPL-3.0 | 模式 D（思想对比） | 仅对比不可变负载模型思路，**零代码参考/拷贝**，独立设计硬链接+Junction 管线 |
| 21 | 通用加载器 | `Reloaded-Project/Reloaded-II` | GPL-3.0 | 模式 C（外部配置） | 通过公开 JSON 应用配置协议协同用户本地安装的 Reloaded-II |
| 22 | Unity 加载器 | `BepInEx/BepInEx` | LGPL-2.1 | 模式 C（外部资产） | 管理 Unity Mono/IL2CPP Doorstop 目录结构与插件启停状态 |
| 23 | UE 加载器 | `UE4SS-RE/RE-UE4SS` | MIT | 模式 C（外部资产） | 管理 Unreal Engine 4/5 `dwmapi.dll` 与 Lua/BP Mod 目录结构 |
| 24 | 二次元加载器 | `SpectrumQT/XXMI-Launcher` | GPL-3.0 | 模式 D（沙箱思路） | 仅参考非侵入外部目录挂载思路，独立实现 `HideUID` 安全提示与路径编排 |
| 25 | 64位转码 | `xuan525/Locale-Remulator` | GPL-3.0 | 模式 C（外部 CLI） | 作为外部独立可执行工具（`LRProc.exe`）按命令行参数引导 x64 日系游戏 |
| 26 | 32位转码 | `xupefei/Locale-Emulator` | GPL-3.0 | 模式 C（外部 CLI） | 作为外部独立可执行工具（`LEProc.exe`）按命令行参数引导 x86 日系游戏 |
| 27 | 窗口超分 | `Blinue/Magpie` | GPL-3.0 | 模式 C（进程协同） | 伴生检测与启停用户安装的 Magpie 进程并纳入活跃游玩焦点白名单 |
| 28 | 透明压缩参考 | `ImminentFate/CompactGUI` | GPL-3.0 | 模式 D（Win32 API） | 直接调用微软公开文档化 `FSCTL_SET_EXTERNAL_BACKING` WOF API 自研熵值采样器 |
| 29 | 超分管理参考 | `beeradmoore/dlss-swapper` | GPL-3.0 | 模式 D（Win32 API） | 自研基于 Windows `WinVerifyTrust` 官方签名验证的超分 DLL 备份替换器 |
| 30 | 远程串流 | `LizardByte/Sunshine` | GPL-3.0 | 模式 C（JSON 互操作） | 读写公开 `apps.json` 格式并响应 `SUNSHINE_CLIENT_*` 环境变量钩子 |
| 31 | 虚拟显示器 | `VirtualDrivers/Virtual-Display-Driver` | MIT | 模式 C（驱动协同） | 协同基于微软 `IddCx` 的虚拟显示器实现 OLED 熄屏挂机与串流动态分辨率 |
| 32 | 音频路由参考 | `File-New-Project/EarTrumpet` | MIT | 模式 D（COM 接口） | 参考 Windows 音频策略 `IAudioPolicyConfigFactory` COM 接口声明实现单进程声卡绑定 |
| 33 | 存档规则库 | `mtkennerly/ludusavi-manifest` | MIT | 模式 A（数据清单） | 消费 MIT 协议存档路径知识库离线预编译为 SQLite 索引表 |
| 34 | 云备份参考 | `restic/restic` | BSD-2-Clause | 模式 D（协议参考） | 借鉴 Packfile 聚合与无状态内容寻址仓库树思想设计 `32MB SPPK` 格式 |
| 35 | 成就目录参考 | `xan105/Achievement-Watcher` | GPL-3.0 | 模式 D（格式重写） | **零代码复用**，针对公开的各模拟器 INI/JSON/二进制落盘格式用纯 C# 从零重写解析器 |
| 36 | 成就兼容层 | `Detanup01/gbe_fork` | LGPL-3.0 | 模式 B（可选赋能包） | 面向无成就桩老旧离线游戏提供独立 DLL 部署与一键原始文件还原 |
| 37 | 方舟自动化 | `MaaAssistantArknights/MAA` | AGPL-3.0 | 模式 C（外部进程） | 启动前原子覆写用户本地 `MAA` 的 `config/gui.json` 并拉起独立进程，零代码链接 |
| 38 | 通用自动化底座 | `MaaXYZ/MaaFramework` | LGPL-3.0 | 模式 B（动态 NuGet） | 在 `WorkflowWorker` 内通过 `Maa.Framework` 动态绑定解析 `interface.json` V2（支持 CLI 降级） |
| 39 | FGO 自动化 | `xlxyvergil/MaaFgo` | GPL-3.0 | 模式 C（本地 TCP RPC） | 通过独立进程与本地 `25001/25002` JSON Socket 协议通信，零 GPL 代码合入 |
| 40 | 原神/星铁/绝区零/鸣潮生态 | `babalae/better-genshin-impact`、`moesnow/March7thAssistant`、`LmeSzinc/StarRailCopilot`、`DoctorReid/ZenlessZoneZero-OneDragon`、`ok-oldking/ok-wuthering-waves` | GPL-3.0 | 模式 C（外部 CLI） | 全部通过标准化命令行开关、YAML 时间戳与进程退出码进行纯外部黑盒调度 |
| 41 | 增量更新参考 | `CollapseLauncher/Hi3Helper.Sophon` | MIT | 模式 D / A | 参考米哈游 Sophon 流式分块清单预检二次元客户端版本更新状态 |

---

## 3. 按进程隔离域划分的 `.NET 10` NuGet 依赖包边界表

所有 NuGet 包版本统一由仓库根目录 `Directory.Packages.props`（Central Package Management，M2 建立）集中锁定：

| NuGet 包标识 | `Spawn.Core` | `Spawn.Host` (Clean) | `Spawn.OverlayHost` (Clean) | `BlueprintWorker` | `TrainerWorker` | `WorkflowWorker` | 安全隔离备注 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| `WPF-UI` | - | **允许** | - | - | - | - | 仅桌面主壳使用 |
| `Nodify` | - | **允许** | - | - | - | - | 仅承载画布编辑视图 |
| `CommunityToolkit.Mvvm` | 允许 | **允许** | - | - | - | - | 编译期 MVVM 生成器 |
| `H.NotifyIcon.Wpf` | - | **允许** | - | - | - | - | 纯原生 XAML 托盘 |
| `VirtualizingWrapPanel` | - | **允许** | - | - | - | - | 海报墙虚拟化布局 |
| `Microsoft.Data.Sqlite` | **允许** | 传递引用 | - | - | - | - | 仅 `Spawn.Host` 持有写通道 |
| `Blake3` / `ZstdSharp.Port` | **允许** | 传递引用 | - | - | - | - | CAS、存档块与 `SPPK` 校验 |
| `System.IO.Hashing` (`XxHash3`) | **允许** | 传递引用 | - | - | - | - | `MutableCopy` 配置差异秒扫 |
| `NAudio.Wasapi` | - | **允许** | - | - | - | - | 外部修改器零注入会话静音 |
| `SkiaSharp` | - | 允许 (离线灰阶图) | **允许 (核心渲染)** | - | - | **允许 (战报长图)** | GPU / 离线位图光栅化 |
| `Grpc.AspNetCore` / `Grpc.Net.Client` | 契约层 | **允许 (Client)** | **允许 (Server)** | **允许 (Server)** | **允许 (Server)** | **允许 (Server)** | 纯 Named Pipes 传输，零 TCP |
| `Microsoft.Windows.CsWin32` | 受限白名单 | 受限白名单 | 受限白名单 | 扩展集 | 扩展集 | 扩展集 | `Host`/`Overlay` 受 `PEReader` 门禁审查 |
| `OpenCvSharp4` + `.runtime.win` | - | **严禁** | **严禁** | **允许** | - | 可选 (看门狗) | 视觉推理物理隔离在 Worker |
| `Sdcb.PaddleOCR` + `PaddleInference` | - | **严禁** | **严禁** | **允许** | - | - | 大体积推理库按需随 Worker 生命周期释放 |
| `Nefarius.ViGEm.Client` | - | **严禁** | **严禁** | **允许** | - | - | 虚拟手柄严禁进入 Clean Host |
| `Maa.Framework` | - | **严禁** | **严禁** | - | - | **允许** | LGPL-3.0 动态绑定隔离于 `WorkflowWorker` |
| `Reloaded.Memory.SigScan` / `Iced` | - | **严禁** | **严禁** | - | **允许 (M6)** | - | 严禁出现在 `Tier 0` 沙箱之外任何项目 |
| `xunit` / `FluentAssertions` | - | - | - | - | - | - | 仅 `tests/*` 测试工程使用 |

---

## 4. 核心 Win32 / NTAPI / COM 互操作安全映射表（`CsWin32`）

| 系统能力场景 | 原生 API / COM 接口 | 所属系统模块 | 允许驻留的程序集 | 最低 IL 要求 | 反作弊安全约束 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 无强杀进程树追踪 | `CreateJobObjectW`、`AssignProcessToJobObject`、`CreateIoCompletionPort` | `kernel32.dll` | `Spawn.Modules.Launcher` | Medium IL | 显式清零 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`；`Tier 2/3` 不挂载 JobObject |
| 受限进程路径探测 | `OpenProcess(0x1000)`、`QueryFullProcessImageNameW` | `kernel32.dll` | `Spawn.Core` (`SafeProcessProbe`) | Medium IL | 全域禁止申请 `PROCESS_VM_READ` 等越权访问位 |
| 切断父子进程血缘 | `IShellDispatch2::ShellExecute` (`ShellWindows`) | `shell32.dll` / `oleaut32.dll` | `Spawn.Modules.Launcher` | Medium IL | `Tier 2/3` 强制必选通道，使父 PID 归属 `explorer.exe` |
| CPU 亲和与省电豁免 | `SetProcessDefaultCpuSets`、`SetProcessInformation` | `kernel32.dll` | `Spawn.Modules.Launcher` | Medium IL | 仅针对 `Tier 0/1` 本地单机启用 |
| 系统 HDR 与显示拓扑 | `GetDisplayConfigBufferSizes`、`QueryDisplayConfig`、`DisplayConfigSetDeviceInfo` | `user32.dll` | `Spawn.Modules.Launcher` | Medium IL | 变更前强制写盘 `environment_rollback.json` |
| 硬链接与 Junction | `CreateHardLinkW`、`DeviceIoControl(FSCTL_SET_REPARSE_POINT)` | `kernel32.dll` | `Spawn.Core` | Medium IL | 创建 NTFS Directory Junction 无需管理员提权；严禁递归删除目标 |
| 透明磁盘无损压缩 | `DeviceIoControl(FSCTL_SET_EXTERNAL_BACKING)` | `kernel32.dll` | `Spawn.Modules.Nexus` | Medium IL | 检测到 `dstorage.dll` 强制熔断禁用 |
| 超分 DLL 官方验签 | `WinVerifyTrust` | `wintrust.dll` | `Spawn.Modules.Nexus` | Medium IL | 仅 NVIDIA / AMD / Intel 有效签名放行；`Tier 2/3` 硬禁用 |
| 全屏高刷透明合成 | `DCompositionCreateDevice`、`CreateSwapChainForComposition` | `dcomp.dll` / `dxgi.dll` | `Spawn.OverlayHost` | UIAccess IL | 零注入独立窗口合成，严禁 Hook 游戏 `Present` |
| 面板焦点与锁鼠接管 | `RegisterHotKey`、`GetClipCursor`、`ClipCursor`、`SHQueryUserNotificationState` | `user32.dll` / `shell32.dll` | `Spawn.OverlayHost` | UIAccess IL | 面板收起即刻归还游戏原锁鼠矩形 |
| 模拟键鼠与扫描码 | `SendInput` (`KEYEVENTF_SCANCODE`) | `user32.dll` | `BlueprintWorker`、`TrainerWorker` | Medium / High IL | **CI 门禁严禁进入 `Spawn.Host` 与 `OverlayHost`** |
| S3 睡眠唤醒与休眠 | `CreateWaitableTimerExW`、`SetThreadExecutionState`、`SetSuspendState` | `kernel32.dll` / `powrprof.dll` | `Spawn.WorkflowWorker` | High IL | 自动睡眠前强制提供 60 秒键鼠可取消倒计时 |

---

## 5. 正式代码开发前本机必备与可选环境安装全表

基于 M1 阶段（`2026-11-15`）对当前开发工作站的实机探测快照，以下列出进入 M2 正式 C# 编码前的**必备核心工具链**与后续里程碑**按需安装的可选伴生环境**。

### 5.1 必备核心开发工具链（进入 M2 编码前本机已 100% 就绪）

| 组件名称 | 要求版本 | 当前本机实测状态 | 标准安装命令（`winget`） | 安装后验证命令 | 核心工程用途 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Windows 11 x64 OS** | `22H2 (Build 22621)` 及以上 | **已就绪**（`NT 10.0.26300.0`） | 系统内置 | `[Environment]::OSVersion` | 提供 Mica 材质、Flip-Model、`CreateHardLinkW`、WOF 与 `DirectComposition` 底座 |
| **`.NET 10 SDK (LTS)`** | `10.0.x`（LTS 最新版） | **已就绪（`ACT-001` 已完成）**<br>*(实测已安装 `SDK 10.0.404` 及 `.NET 10.0.15` Desktop Runtime)* | `winget install --id Microsoft.DotNet.SDK.10 --exact --source winget` | `dotnet --list-sdks` | 编译构建 `Spawn.sln` 全套 `.NET 10` WPF / OverlayHost / Worker / gRPC / GateTests 工程的核心编译器 |
| **Windows 10/11 SDK (`signtool.exe`)** | `10.0.22621.0` 及以上 | **已就绪**（`10.0.26100.0` 含 x64 `signtool.exe`） | `winget install --id Microsoft.WindowsSDK.10.0.26100 --exact` | `where.exe signtool` | M2 本地瞬态自签名证书签发 `Spawn.OverlayHost.exe`（`ZBID_UIACCESS`）及原生互操作头文件参考 |
| **Visual Studio Build Tools / IDE** | VS 2022/2026 或 Build Tools `18` | **已就绪**（Build Tools `18` 已安装） | `winget install --id Microsoft.VisualStudio.2022.BuildTools --exact` | `vswhere -latest` | 提供 MSBuild 桌面构建链与原生依赖打包支持（如需可视化 XAML 热重载可选装桌面开发工作负载） |
| **Python 3** | `3.12+` | **已就绪**（`Python 3.12.16`） | `winget install --id Python.Python.3.12 --exact` | `python --version` | 运行本地与 CI 一致的文档治理及架构注册表门禁脚本 `python -B scripts/check_docs.py` |
| **Git & GitHub CLI** | `git 2.40+` / `gh 2.50+` | **已就绪**（`git 2.56.0` / `gh 2.103.3`） | `winget install --id Git.Git --exact` / `winget install --id GitHub.cli --exact` | `git --version` / `gh auth status` | 单阶段单提交 DCO 签名演进、远端规则集审计与受控 PR 交付 |
| **Inno Setup** | `6.3+` / `7.x` | **已就绪**（`Inno Setup 7` `ISCC.exe`） | `winget install --id JRSoftware.InnoSetup --exact` | `iscc /?` | M6 制作标准安装包（部署 `%ProgramFiles%\spawn\Overlay\` 受保护目录与本地自签名向导） |

> **M2 启动就绪结论**：本机全部 M2 核心编译、签名与验证基础设施均已 100% 就位；待 M1 分支按 [USER-ACTIONS.md](../../USER-ACTIONS.md) `ACT-002` 获准合并入 `main` 后，即可零阻塞直接启动 M2 编码与测试。

### 5.2 `M2` 离线成就激活器（`Detanup01/gbe_fork`）DLL 资源准备与合规规范

针对 M2 核心交付项“无成就桩 / FLT 补丁单机游戏一键无损成就激活器（`REQ-ACH-03`）”，外部 `LGPL-3.0` 组件 `Detanup01/gbe_fork` 遵循以下纯净准备与校验机制：
1. **Git 源码仓零二进制提交铁律**：严禁将预编译 `steam_api.dll` / `steam_api64.dll` 直接提交进 Git 仓库。
2. **锁定版本与 `SHA-256` 清单按需拉取**：在 `Spawn.Modules.Achievement` 内置锁定的上游官方 Release 版本号与官方构建产物（`release/regular` 纯净构建，不含网络联机注入特性）精确 `SHA-256` 校验字典；首次使用“一键激活成就”或运行本地资源预热脚本时，自动拉取并校验落盘至本地应用数据缓存池 `%LOCALAPPDATA%\spawn[-dev]\vault\Toolchains\gbe_fork\<version>\`（离线环境亦支持用户手动导入官方归档包校验入池）。
3. **无损备份还原与 `LGPL-3.0` 合规**：部署至目标 `Tier 0` 单机目录前，强制将游戏原版 DLL 同目录备份为 `.spawn_orig_bak` 并记录 `OriginalApiDllSha256`；同时自动在缓存池与部署说明中写入 `LGPL-3.0` 许可证声明及用户自由替换同 ABI 动态库的指引。

### 5.3 按里程碑触发的可选伴生与联调环境清单（M2 ~ M6 按需准备）

以下配置与外部工具**不阻塞核心代码编译与自动化单元测试**（所有单元与集成测试均基于内置 Mock/黄金样本夹具后台静默完成），仅在实机深度体验时按需准备：

| 可选组件 / 样本 | 触发阶段 | 推荐版本 / 获取方式 | 标准获取命令或配置入口 | 用途与缺失时的优雅降级行为 |
| :--- | :---: | :--- | :--- | :--- |
| **Steam Web API Key & 单机样本** | `M2` | 免费个人 Key（`ACT-006`） | 在客户端设置页录入（DPAPI 本地加密） | 加速拉取 Steamrip 游戏官方权威简中成就 Schema；不配置时自动走本地 Steam 缓存与免 Key 社区镜像兜底 |
| **7-Zip 原生命令行 / 动态库** | `M3` / `M4` | `24.08+` | `winget install --id 7zip.7zip --exact --source winget` | 高速解压 `.7z` / `.rar` 外部修改器归档与大型 Mod 整合包；未安装时内置托管解压器原生支持 `.zip` / `.tar` / `.zst` |
| **Blinue Magpie 窗口缩放器** | `M4` | `v0.11+` | `winget install --id Blinue.Magpie --exact --source winget` | 实机联调老旧低分辨率单机伴生超分与焦点白名单继承；未安装时对应伴生开关置灰提示 |
| **Locale Remulator / Emulator** | `M4` | `LR 1.5+` / `LE 2.5+` | 官方 GitHub Release 解压至用户配置工具箱目录 | 实机联调日文 Shift-JIS Galgame x64 (`LRProc`) / x86 (`LEProc`) 无乱码引导 |
| **Android Platform-Tools (`adb`)** | `M5` | `35.0+` | `winget install --id Google.PlatformTools --exact --source winget` | 调试 MuMu12 / 雷电模拟器 ADB 实例枚举与 `MaaFgo` 后台控制器连接（主流模拟器安装目录亦自带 `adb.exe`） |
| **Nefarius ViGEmBus 虚拟手柄驱动** | `M6` | `v1.22.0` | `winget install --id Nefarius.ViGEmBus --exact --source winget` | 启用 `Spawn.BlueprintWorker` 的 `L3` 虚拟 Xbox360 / DS4 手柄后台输入执行器；未安装驱动时自动隐藏 L3 选项并保留 L1/L2 键鼠执行器 |
| **LizardByte Sunshine & IddCx 虚拟屏** | `M5` / `M6` | `Sunshine v0.23+` | `winget install --id LizardByte.Sunshine --exact --source winget` | 实机验证 Moonlight 掌机串流动态虚拟屏切换与夜间物理显示器断电防烧屏；未安装虚拟屏驱动时自动回退纯黑全屏软件防烧屏幕布 |
