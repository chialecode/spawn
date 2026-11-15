# ADR-0003：Clean Host 多进程完整性物理隔离与四级反作弊联锁护栏

- **状态**：Accepted
- **日期**：2026-11-15
- **决策编号**：`D-02`、`D-03`、`D-11`
- **关联文档**：[系统顶层架构](../design/system-architecture.md) · [IPC 与安全隔离契约](../design/ipc-and-security-contracts.md) · [启动器与环境规格](../modules/spawn-launcher.md) · [前期调研归档 02](../research/02-launcher-process-and-mod-nexus.md)

---

## 1. 背景与问题陈述

SPAWN 同时服务两类安全敏感度截然相反的游戏群体：
- **离线单机极客场景**：需要修改器沙箱热键代理、蓝图自动化键鼠/虚拟手柄注入、单机过场 `NtSuspendProcess` 暂停，甚至二期原生 `.CT` 内存特征码扫描。
- **高敏反作弊竞技网游场景**：玩家电脑上往往同时安装有受 Riot Vanguard（`vgk.sys`）、腾讯 ACE（`SGuard64.exe`）、EasyAntiCheat（`EasyAntiCheat_EOS.sys`）或 BattlEye（`BEDaisy.sys`）保护的在线竞技游戏。

如果在单体常驻主进程中直接编译链接 `ReadProcessMemory`、`WriteProcessMemory`、`SendInput`、`SetWindowsHookEx`、`MinHook` 或 `ViGEmClient`，哪怕当前并未对网游执行任何操作，现代 Ring-0 反作弊驱动也会因以下机制触发封禁或踢出风险：
1. **PE 导入表（IAT）与内存特征静态扫描**：常驻桌面管理器携带外挂高频敏感 Win32 API 导入符号。
2. **父子进程血缘（PPID）与句柄继承审查**：由携带复杂句柄表的第三方进程直接 `CreateProcess` 拉起受保护网游。
3. **游戏退出后的后台残影延迟扫描**：部分反作弊守护进程（典型如腾讯 ACE `SGuard64.exe`）在游戏主窗口关闭后仍会驻留内核与用户态数秒至十余秒，若此时立即唤醒修改器或自动化引擎极易撞上尾扫。

## 2. 备选方案对比

| 方案 | 描述 | 评估结论 |
| :--- | :--- | :--- |
| **方案 A：单体宿主 + 软开关禁用** | 所有功能模块打包进 `Spawn.Host.exe`，通过业务代码 `if (isOnlineGame) return;` 跳过敏感调用。 | **坚决否决**。敏感 Win32 API 依然存在于主程序 PE 导入表中，且一旦插件或后台线程生命周期泄露句柄，玩家账号面临不可逆封禁风险。 |
| **方案 B：Clean Host 多进程物理隔离 + 四级联锁 + Explorer 血缘脱钩 + 15s 退出冷却** | 主界面与弹窗进程绝对剥离一切敏感 API（由 CI 二进制扫描守门）；敏感能力封装进按需启停的独立 Worker 可执行文件；按四级反作弊等级执行物理清场、Explorer 代启动与 15 秒残影冷却。 | **正式采纳**。从二进制静态指纹、OS 完整性级别（IL）、进程树血缘到时间窗口四个维度建立纵深物理防火墙。 |

## 3. 最终决策

### 3.1 六域多进程与完整性级别（Integrity Level）物理切分
1. **`Spawn.Host.exe`（Medium IL `0x2000`）**：常驻桌面主界面、`spawn.db` 单写者、元数据与 CAS 文件管理、反作弊守卫。**绝对禁止引用任何远程内存、输入模拟与 Hook 库**（支持开机自启与资源管理器文件拖放）。
2. **`Spawn.OverlayHost.exe`（Medium+UIAccess IL `0x2010`）**：纯渲染成就弹窗与 `Alt+Z` 游戏内面板（`DirectComposition` + `SkiaSharp`），**对游戏进程零句柄打开、零 DLL 注入**。
3. **`Spawn.BlueprintWorker.exe`（按需 Medium / High IL）**：仅在 `Tier 0`（及用户显式授权的 `Tier 1` 单人客户端白名单）按需启动，承载 `WGC` 捕获、`OpenCV`/`PaddleOCR` 与 `L1~L4` 输入执行器，停用即销毁进程。
4. **`Spawn.TrainerWorker.exe`（按需 High IL `0x3000`）**：**严格限定仅在 `Tier 0` 纯离线单机且系统无任何 `Tier 2/3` 进程运行时允许启动**，承载外部 FLiNG 沙箱作业对象、硬件扫描码代理与二期 `.CT` 引擎。
5. **`Spawn.WorkflowWorker.exe`（按需 High IL `0x3000`）**：承载二次元外部生态 DAG 编排、`Maa.Framework` 内嵌与 `MaaFgo` 桥接，检测到 `Tier 2/3` 反作弊游戏启动时自动暂停/排空冲突节点。
6. **`Spawn.LegacyHook.Helper.exe`（可选独立隔离组件，默认禁用）**：仅面向 `<5%` 远古 DirectX 9 独占全屏离线游戏，完全独立于主安装包默认运行链之外。

### 3.2 四大刚性安全执行铁律
1. **CI `PEReader` 二进制静态红线门禁**：自动化测试通过 `System.Reflection.PortableExecutable.PEReader` 扫描 `Spawn.Host` 与 `Spawn.OverlayHost` 的导入表与程序集引用，出现 `ReadProcessMemory`、`WriteProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、`NtSuspendProcess`、`SetWindowsHookEx`、`SendInput`、`mouse_event` 等任一符号即刻阻断构建。
2. **全域 `0x1000` 最低权限进程探测**：宿主监控外部进程存活与路径时一律仅使用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION [0x1000], false, pid)` + `QueryFullProcessImageNameW`，严禁调用会申请 `PROCESS_VM_READ` 的 `.NET Process.Modules / MainModule`。
3. **Explorer Shell COM 切断父子血缘与启动前清场**：启动 `Tier 2/3` 游戏前，强制终止所有运行中的特权 Worker 并扫描系统是否存在 `dbk64.sys` / `WinRing0` 等高危驱动（发现则弹窗阻断启动）；随后强制通过 `IShellDispatch2.ShellExecute` 委派 `explorer.exe` 拉起游戏，确保游戏父进程为系统桌面外壳。
4. **反作弊残影 15 秒强制退出冷却期**：`Tier 2/3` 游戏主进程退出后，全域熔断锁持续监测 `SGuard64.exe`、`SGuardSvc64.exe`、`EasyAntiCheat_EOS.exe`、`BEService.exe` 直至其彻底退出，再执行 **15 秒不可跳过冷却倒计时**后方可恢复特权 Worker 可用态。

### 3.3 免 UAC 弹窗静默提权与 SDDL 管道契约
- 首次需要 High IL 能力时引导用户单次确认注册绑定精确路径的 Windows 计划任务（`LogonType = InteractiveToken`, `RunLevel = HighestAvailable`），后续按需毫秒级静默拉起 High IL Worker，零 UAC 黑屏打断。
- 跨 IL `gRPC over Named Pipes` 统一使用 `NamedPipeServerStreamAcl.Create` 显式授权当前登录用户 SID 并 `Deny` `NetworkSid`。

## 4. 影响与后果

- **正面收益**：主宿主二进制纯净度与普通音乐播放器/笔记软件完全等价，玩家无需在玩竞技网游前手动退出 SPAWN。
- **工程代价**：跨模块实时交互（如蓝图单步断点与修改器状态同步）必须通过 Protobuf 命名管道与共享内存契约通信，已在 [ipc-and-security-contracts.md](../design/ipc-and-security-contracts.md) 标准化封装。

## 5. 验证判据

- `Spawn.Architecture.GateTests` 静态 PE 导入表扫描 100% 通过。
- 夹具验证 `Tier 3` 模拟启动链父进程 PID 等于 `explorer.exe` PID，且退出后 `SGuard64` 残影 + 15 秒冷却状态机时序精准闭环。
