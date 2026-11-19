# SPAWN 待收敛工程事项台账（Open Engineering Questions & Spikes）

> **文档状态**：Active（随里程碑动态更新）
> **关联文档**：[已确认架构决策](confirmed-decisions.md) · [技术选型与环境清单](../engineering/technology-selection.md) · [里程碑交付路线图](../engineering/delivery-plan.md) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

本台账集中记录 **不阻塞 M1 架构基线锁定**、需在后续具体编码里程碑（M2 ~ M6）通过实机 Spike、ABI 联调或打包审查予以最终闭合的技术事项。

> **管理规则**：
> 1. 严禁在本文件之外散落零碎的 `TODO` 或未追踪的待定假设。
> 2. 某事项在对应里程碑实机验证收敛后，应将其状态更新为 `Resolved` 并补充验证结论（涉及重大契约变更时同步升格为新 ADR）。
> 3. 本台账仅跟踪工程实现与适配细节；需仓库所有者手工确认的外部可选配置或远端发布审批动作（如 `ACT-002` 远端推送授权、`ACT-006` 可选配置 Steam Web API Key）统一维护在 [USER-ACTIONS.md](../../USER-ACTIONS.md)。

---

## 1. 外部开源生态与底层 API 适配事项（`OSS-*`）

| 编号 | 归属阶段 | 事项描述与潜在风险 | 收敛触发条件与既定降级方案 | 当前状态 |
| :--- | :--- | :--- | :--- | :--- |
| `OSS-01` | `M5` | **`Maa.Framework` C# 绑定对 `interface.json` V1 / V2 混合生态的兼容边界**：社区十余款 `MaaFramework` 衍生二次元工具存在不同版本运行时与 `interface.json` Schema 演进差异，单一内嵌 `MaaFramework.dll` 版本可能与个别老项目资源包产生 ABI 偏差。 | 进入 M5 开发时，优先加载外部项目自带目录中的原生 `MaaFramework.dll` 或校验 Schema 版本；一旦检测到大版本不匹配，自动无感回退至随包 `MaaPiCli.exe -d` 进程级标准 CLI 调度。 | Open |
| `OSS-02` | `M5` | **闭源 `BBChannel` 客户端更新对 `_ctypes.pyd` 代理补丁符号稳定性的影响**：`MaaFgo` 依赖的 `BBChannel` 若更新内嵌 Python 版本或调整内部类名（如 `_RetiredMumuApi`），代理初始化脚本需按特征自适应。 | 在 M5 编写 `_ctypes_proxy` 引导层时，增加 Python 次版本号探测与 `getattr`/`hasattr` 防御性反射检查；若目标函数签名变更，立即优雅回退并向用户输出版本兼容告警而非引发宿主崩溃。 | Open |
| `OSS-03` | `M6` | **Windows 11 24H2/25H2+ 未公开音频路由接口 `IAudioPolicyConfigFactory` 的 IID 稳定性**：微软曾在历史大版本更新中微调未文档化音频策略 COM 接口 GUID。 | 进入 M6 实现进程级音频端点路由前，封装基于系统构建号（Build `22621` / `26100` / `26300+`）的多版本 IID 探测适配器（参考 `EarTrumpet` 实现），探测失败时自动隐藏该单项极客开关。 | Open |
| `OSS-04` | `M2` | **便携免安装模式与 `ZBID_UIACCESS` 置顶签名的双态体验对齐**：Windows 内核强制要求 `uiAccess="true"` 进程必须带受信根证书签名且位于 `%ProgramFiles%` 等受保护目录；纯解压便携版用户在不运行安装向导时无法获得 `ZBID_UIACCESS`。 | M2 实现 `OverlayHost` 实时成就弹窗启动能力自检：已部署受保护目录签名版自动启用 `ZBID_UIACCESS`；纯便携版自动回退至 `SetWindowPos(HWND_TOPMOST)` + 无边框窗口化友好提示，并在设置页提供一键本地证书与受保护目录部署向导。 | Open |
| `OSS-05` | `M3` | **排他文件锁存档场景下的 `AlphaVSS` 卷影复制权限边界**：`AlphaVSS` 创建 NTFS 卷影副本需要管理员（High IL）权限，而日常 `Spawn.Host` 运行于 Medium IL。 | M3 默认在 Medium IL 执行 `FileShare.ReadWrite | FileShare.Delete` 非阻塞读取；仅当连续 3 次命中 `ERROR_SHARING_VIOLATION` 排他锁时，按需通过已授权计划任务唤醒 High IL 辅助任务执行单次 VSS 快照提取。 | Open |
| `OSS-06` | `M2` / `M5` / `M6` | **`LGPL-3.0` 动态链接组件的发行包合规物料自动化生成**：M2 一键成就激活器所用的外部 `Detanup01/gbe_fork`、M5 `Maa.Framework` 与 M6 二期 `Reloaded.Memory.SigScan` 采用 `LGPL-3.0` 许可证，与核心仓 `Apache-2.0` 合规共存要求保持独立 DLL 动态部署并附带许可证文本与用户可替换说明。 | 在 M2 接入 `gbe_fork` 激活器资源池起即编写构建/打包辅助脚本，自动收集独立动态库许可证并在产物根目录生成 `THIRD-PARTY-NOTICES.txt` 与动态库替换指南。`M2` 起 `gbe_fork` 固定为 `release-2026_09_27` / `emu-win-release-vs22.7z`（SHA-256 `300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85`），下载后校验失败即拒绝解压，缓存按版本隔离并附 SHA-256 清单；升级版本须同步修改常量与本条记录。 | Open |

---

## 2. 分阶段高级演进与可选硬件闭环（`LOOP-*`）

| 编号 | 归属阶段 | 演进闭环主题 | 设计预留与激活条件 | 当前状态 |
| :--- | :--- | :--- | :--- | :--- |
| `LOOP-01` | `M6` | **二期原生 Cheat Engine `.CT` 内存修改引擎落地** | M3 先冻结 `ITrainerBackend` 接口与 `ExternalFlingBackend`；M6 在 High IL `Spawn.TrainerWorker` 中实现 `NativeCheatTableBackend`（逆序指针链解析、`SigScan` AVX2 扫描与 `Iced` ±1.75GB 安全跳板），严守 `Tier 0` 离线单机红线。 | Planned (M6) |
| `LOOP-02` | `M6+` | **`Spawn.LegacyHook.Helper` 远古独占全屏可选渲染桥** | 针对 `<5%` 仅支持 DirectX 9 传统独占全屏的老旧单机游戏预留独立 Helper 边界；M2 主线优先交付 100% 零注入的 `Spawn.OverlayHost`（覆盖 95%+ DX12/Vulkan/DX11 Flip-Model 游戏），Helper 默认不编译进常驻启动链。 | Deferred |
| `LOOP-03` | `M6` | **外部硬件级 HID（KMBox Net / B+ 串口）输入执行器插件** | 在 `Spawn.BlueprintWorker` 的 `IInputActuator` 接口预留 `L4 HardwareHidActuator` 扩展点；M6 优先完成软件级 `L1`（后台消息）、`L2`（`SendInput`）与 `L3`（`ViGEmBus` 虚拟手柄）闭环后按需接入硬件串口协议。 | Planned (M6) |
| `LOOP-04` | `M5` / `M6` | **Windows 10/11 专业版 `WTSEnableChildSessions` 桌面分身后台并发槽** | 在 `WorkflowWorker` 四维资源锁模型中预留 `ChildSessionSlot` 资源维度；实机检测到 Windows Home 家庭版或未启用子会话策略时，自动将 PC 原生二次元游戏收敛至 `PrimaryForegroundLock` 串行队列并配合夜间 RTC 唤醒执行。 | Planned (M5/M6) |
| `LOOP-06` | `M3` | **`Tier 2/3` 启动前自动终止 SPAWN 自有高敏 Worker 与改名驱动识别（`F-03` 剩余部分）** | `M2` 已实现启动前检出 Cheat Engine 进程与 `dbk64.sys` / `dbk32.sys` / `WinRing0x64.sys` / `inpoutx64.sys` 已加载驱动即阻断 `Tier 2/3` 启动，对 `Spawn.*Worker` 进程同样仅检测并阻断（`M2` 尚无 Worker 子工程）；`M3` 引入 `Spawn.TrainerWorker` 后，改为在放行前经其 `KILL_ON_JOB_CLOSE` 专用 Job 主动关闭自有 Worker 并复检。用户自定义改名的 CE 驱动（非 `dbk*.sys` 文件名）识别需结合设备名/签名特征，届时评估误报率后决定。 | Planned (M3) |
| `LOOP-05` | `M6` | **Inno Setup 7 双模式安装包与 Ed25519 增量更新签名管线** | 本机已具备 `Inno Setup 7` 与 `Windows SDK signtool.exe`；在 M6 收官阶段编写标准 `.iss` 安装脚本（自动配置 `%ProgramFiles%\spawn\Overlay\` 本地瞬态自签名证书与 Defender 沙箱目录排除向导）及离线公钥校验的增量更新清单。 | Planned (M6) |
