# SPAWN 项目关键事实与技术基线

更新日期：2026-11-21（`M3` 单机外部修改器安全管家基线同步）。本文件汇总 SPAWN 项目的核心身份、已拍板技术路线、范围边界与跨会话恢复入口；各项细则以所链接的职责正本为准。

---

## 1. 产品身份与核心定位

- **项目名称**：**SPAWN**（面向小圈子硬核极客玩家的非商业纯原生全能游戏中枢；路径、通道与数据标识统一使用小写 `spawn` / `spawn-dev`）。
- **核心痛点闭环**：极客玩家日常在同一台 Windows 主机（及联动 Windows 掌机）上游玩**带内核反作弊的二次元/竞技网游**与**重度打 Mod、开修改器、离线解锁成就的单机大作**。传统工具生态高度割裂且存在严重交叉隐患（修改器背景电音炸耳、硬链接 Mod 写坏原包、网盘按时间戳覆盖丢档、后台残留调试驱动导致网游误封）。SPAWN 将 **S.P.A.W.N.** 五大能力域置于统一的本地数据库、内容寻址仓库与 **Anti-Cheat Guard 物理隔离架构** 下实现丝滑联动。

---

## 2. 已拍板核心技术定案基线

全量决策背景见 [已确认决策](decisions/confirmed-decisions.md) 与 [ADR 索引](decisions/README.md)：

1. **100% 纯原生桌面技术栈（[ADR-0002](decisions/0002-pure-native-wpf-and-nodify-ui-stack.md)）**：
   - 目标平台：**Windows 10 (21H1, Build 19043+) / Windows 11 x64**。
   - UI 与运行时：坚定选型 **`.NET 10` (LTS, C# 14, `net10.0-windows`) + `WPF` + `lepoco/wpfui` (DWM 原生硬件 Mica/Acrylic) + `miroiu/nodify` + `CommunityToolkit.Mvvm` + `SkiaSharp`**（本机开发环境已核验就绪 `.NET 10 SDK 10.0.404`）。
   - **明确否决**：彻底拒绝 WebView2 / Electron / CEF 前端混合方案（规避百 MB 内存常驻与 HWND Airspace 空域撕裂）；放弃 WinUI 3（因其缺失工业级类 UE 蓝图开源画布、管理员权限下拖拽与文件选择器瘫痪、异形透明穿透窗与多屏 DPI 坐标分裂硬伤）。
2. **Clean Host + Ephemeral Workers 物理隔离架构（[ADR-0003](decisions/0003-anti-cheat-guard-and-process-isolation.md)）**：
   - 常驻宿主 `Spawn.Host.exe`（Medium IL）与置顶渲染宿主 `Spawn.OverlayHost.exe`（UIAccess IL）强制通过 CI `PEReader` IAT 门禁保证 **零敏感 Win32 API 导入**。
   - 高敏操作物理拆分至按需启停、用完即焚的独立子进程（`BlueprintWorker`、`TrainerWorker`、`WorkflowWorker`），按 `D-11` 分级按需提权（免 UAC 仅限 `LOOP-07`），通过零依赖 `NamedPipeJsonRpc`（`NamedPipeServerStreamAcl` 当前用户 SID SDDL + 倒置跨 IL 单实例与双向 PID/路径/`LaunchNonce` 互验）与 `MemoryMappedFile` 零拷贝帧缓冲通信，并强制执行四级反作弊熔断与 **15 秒 `SGuard64` 退出冷却锁**。
3. **CAS + FastCDC 存储与无钩子非侵入 Mod 拓扑（[ADR-0004](decisions/0004-cas-fastcdc-storage-and-mod-junction-topology.md)）**：
   - 单一本地 `spawn.db`（SQLite WAL 模式，严禁直放网盘）+ `SPAWN_Vault` 内容寻址仓库。
   - 存档时间机器采用 `ludusavi` 规则（隔离 `[save]` 与 `[config]`）+ Gear-Hash `FastCDC`（均值 1MB 变长切块）+ `BLAKE3` + `Zstd`，多端云同步采用 `32MB SPPK Packfile` + `ParentSnapshotId` Git 祖先链三路防覆盖。
   - Mod 管理弃用必触发杀软与反作弊的 `USVFS` API Hook，采用同盘免提权 `CreateHardLinkW` + 可变配置物理副本 `XxHash3` 回收 + 5 阶段 WAL 与免提权 NTFS Directory Junction `<100ms` 原子切换。
4. **外生生态顶层编排与本机自签 `ZBID_UIACCESS` 覆盖层（[ADR-0005](decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md)）**：
   - 二次元自动化绝不重造单游戏识图脚本，在 `WorkflowWorker` 内嵌官方 `Maa.Framework` C# 绑定解析 `interface.json` V2，结合 `MaaFgo + BBChannel` 双端口 TCP 桥接及 `BetterGI` / `m7a.exe` / `SRC` / `OneDragon` / `ok-ww` 标准适配器实现四维资源锁并发 DAG。
   - 修改器一期（`M3`）聚焦外部独立修改器安全管家（洁净室 FLiNG 发现 + 用户显式触发下载 + 本地导入、`A0+A` 零改 EXE 零注入静音、隔离 `TrainerWorker` 热键代理与启动前存档快照，见 [ADR-0006](decisions/0006-m3-external-trainer-manager-elevation-and-ipc.md)），二期（`M6`）在隔离 `TrainerWorker` 内扩展收窄的原生修改后端（静态指针 / 基础 `.CT` 子集，逆序指针 `.Reverse()` + `.NET 10` `Vector256` 自研 AOB 扫描 + `Iced`）。
   - 游戏内 Overlay 通过安装期本机原地生成一次性 4096-bit 自签证书（签完即毁私钥）激活 `ZBID_UIACCESS = 2` + `DirectComposition` Flip-Model 交换链，实现 144Hz/240Hz 零掉帧免注入压制。

---

## 3. 明确不纳入的边界（Explicit Non-Goals）

1. **绝不触碰任何联机网游作弊能力**：严禁针对 `Tier 2` / `Tier 3` 强反作弊联机游戏开发内存读写、透视自瞄、网络封包篡改、变速齿轮或非合规输入宏。
2. **不运营中心化商业云后端**：不设官方账号注册、不设付费会员墙、不托管用户存档或凭据；云同步完全对接用户自有的 S3/R2、WebDAV 或局域网存储。
3. **不维护单款二游日常活动识图脚本**：二次元具体关卡像素坐标与活动逻辑完全依托上游活跃开源社区（`MaaFramework` 生态、`BetterGI` 等），SPAWN 专注跨生态并发排班、电源/虚拟屏环境保障与战报推送。
4. **不做跨平台 macOS / Linux 桌面移植**：深度扎根 Windows 10/11 原生内核机制（Job Object、NTFS Hardlink/Junction、WASAPI、DirectComposition、WGC、`ZBID_UIACCESS`、WOF 压缩），拒绝为虚假的跨平台妥协原生极客体验。
5. **不内置盗版游戏本体下载器或资源分发站**：仅对本地已有游戏目录提供环境接管、离线模拟器成就兼容解析与合法公开元数据刮削。

---

## 4. 跨会话信息恢复速查表

| 待确认问题 | 唯一权威正本 |
| :--- | :--- |
| 开发红线、A/B 闭环与 Swarm 并行隔离怎么配合？ | [AGENTS.md](../AGENTS.md)、[开发协作流程](dev-rules/development-workflow.md) |
| 当前做到哪个里程碑、本地分支与父提交是什么？ | [当前执行状态](delivery/status.md)、[阶段计划](delivery/stage-plan.md)、[Git 工作流](dev-rules/git-workflow.md) |
| 哪些决策已拍板、哪些工程细节待后续阶段实测？ | [已确认决策](decisions/confirmed-decisions.md)、[集中工程事项台账](decisions/open-questions.md) |
| 需要用户装什么环境、配什么密钥或授什么远端权限？ | [USER-ACTIONS.md](../USER-ACTIONS.md) |
| `Spawn.sln` 子工程怎么拆、各工程允许引哪些 NuGet 包？ | [仓库地图](dev-rules/repo-map.md)、[技术选型与环境清单](engineering/technology-selection.md) |
| 五大核心模块的接口、状态机与 15 大深坑怎么实现？ | [模块规格总览](modules/README.md)、[全景系统架构](design/system-architecture.md) |
| 需求 ID 与里程碑验收标准怎么一一对应？ | [产品需求正本](requirements/product-requirements.md)、[交付路线图](engineering/delivery-plan.md)、[需求追踪矩阵](delivery/traceability.json) |
