# SPAWN

**面向硬核极客玩家的 100% 纯原生 Windows 全能游戏中枢。**

**S.P.A.W.N.** = **Spawn**（启动与环境管线）+ **Progress**（存档时间机器与修改器管家）+ **Achievement**（全渠道离线/正版成就与高刷 Overlay）+ **Workflow**（二次元外部代肝并发编排与类 UE 蓝图自动化）+ **Nexus**（非侵入式 Mod 与多维快照及极客工具箱）。

本项目针对 Windows 10 (21H1+) / Windows 11 x64 平台原生打造，技术栈锁定 **C# (`.NET 10` LTS, `net10.0-windows`) + `WPF` + `lepoco/wpfui` (DWM 硬件级 Mica/Acrylic) + `miroiu/nodify` + `SkiaSharp` + `SQLite WAL`**，彻底拒绝任何 WebView2、Electron 或前端混合套壳方案，由 **Anti-Cheat Guard 主从进程物理隔离架构** 为强反作弊网游与重度魔改单机共处一机保驾护航。

---

## 五大核心业务域

| 核心域 | 工程模块与宿主 | 核心极客能力概览 |
| :--- | :--- | :--- |
| **S — Spawn**<br/>启动与环境管线 | `Spawn.Modules.Launcher`<br/>(`Spawn.Host`) | 免提权 Job Object IOCP（无强杀标志）+ `0x1000` 35s 目录破壳追踪双引擎；融合手柄/超分白名单/Galgame 音频峰值的 Active/Background/AFK 三态精准时长；Pre/Post-Launch 崩溃自愈管线（Magpie、Lossless Scaling、CCD API 原生开关 HDR、x86/x64 免安装日系转区、CPU Sets 大小核/X3D 绑定）；三级漏斗离线多源刮削（IGDB + SteamGridDB + Bangumi + VNDB）。 |
| **P — Progress**<br/>存档时间机器与修改器管家 | `Spawn.Modules.Progress`<br/>+ `Spawn.TrainerWorker` | 原生解析 `ludusavi` 16,000+ 游戏规则（严格隔离 `[save]` 与 `[config]`）；Gear-Hash `FastCDC` 变长分块 + `BLAKE3` + `Zstd` 全局去重快照与 `AlphaVSS` 热备份；`32MB SPPK Packfile` + `ParentSnapshotId` Git 祖先链防覆盖多端云同步（S3/R2、坚果云/NAS WebDAV）；风灵月影双站确定性检索、**`A0+A`（预置 INI/空 MIDI + 全端点 WASAPI 会话监听）零改 EXE 零报毒去电音与按键音独立控音**、35ms 硬件扫描码代理，以及二期 CE `.CT` 逆序指针与 `Iced` 内存引擎扩展。 |
| **A — Achievement**<br/>全渠道成就与高刷 Overlay | `Spawn.Modules.Achievement`<br/>+ `Spawn.OverlayHost` | 完整覆盖 Goldberg/`gbe_fork`、RUNE/CODEX、TENOKE、Online-Fix、ALI213、SmartSteamEmu 等 15+ 离线模拟器与正版 Steam Binary VDF 监听；内置类 `comet` 本地 `127.0.0.1:9977` 守护零侵入捕获 GOG 离线包成就；FLT 痛点一键无损 `gbe_fork` 激活器与隐藏成就中文真相补全；`DirectComposition` + `SkiaSharp` + 本机一次性自签 `ZBID_UIACCESS` 实现 144Hz/240Hz **0.0 FPS 掉帧**多主题弹窗（Steam/PS5 白金/Xbox 紫钻/原神/星铁）与 `Alt+Z` 锁鼠交互面板。 |
| **W — Workflow**<br/>二次元并发编排与 UE 蓝图 | `Spawn.Modules.Workflow`<br/>+ `Spawn.WorkflowWorker`<br/>+ `Spawn.BlueprintWorker` | **外部二游顶层编排**：内嵌官方 `Maa.Framework` C# 原生驱动 `interface.json` V2 全系脚本，深度打通 `MaaFgo` 注入 `BBChannel` 双端口（`25001/25002`）免焦点后台调度，统合 `MAA`、`BetterGI`、`m7a.exe`、`SRC`、`ZZZ-OneDragon`、`ok-ww` 构建四维资源锁并发 DAG，配套 `WaitableTimer` 唤醒、虚拟屏/OLED 全黑防窥幕布与 `SkiaSharp` 战报推送。<br/>**类 UE 蓝图引擎**：`Nodify` 双引脚画布 + Kismet 异步单步发光调试 VM + `RawInput` 无钩子触发 + `WGC + OpenCvSharp4 + Sdcb.PaddleOCR` 毫秒级视觉 + L1~L4（含 ViGEmBus 虚拟手柄与 KMBox 硬件盒）多模态输入。 |
| **N — Nexus**<br/>非侵入 Mod 与极客工具箱 | `Spawn.Modules.Nexus`<br/>(`Spawn.Host`) | CAS 只读仓库 + 同盘免提权 `CreateHardLinkW` + `XxHash3` 可变配置差异回收（根治硬链接写污染）；5 阶段 WAL + 免提权 NTFS Directory Junction `0.2ms` 多维 Profile（Mod + 物理存档槽 + 启动环境）原子切换；原生编排 Unity Doorstop、RE-UE4SS、Reloaded-II 与二次元 `XXMI`；内置**便携绿色沙箱、WOF 香农熵透明压缩（带 DirectStorage 熔断）、DLSS 4 官方签名替换、Sunshine 虚拟屏熄屏串流、进程独立声卡分轨、单机过场时间静止与死锁强杀** 6 大极客工具。 |

---

## 物理隔离安全架构基座

SPAWN 从解决方案工程结构（`Spawn.sln`）与操作系统进程完整性级别（Integrity Level）双层切断敏感能力污染：

- **纯净常驻宿主（`Spawn.Host.exe`，Medium IL）**：仅承载 WPF-UI 界面与无侵入管理逻辑，由 `Spawn.Architecture.GateTests` 在 CI 中通过 `PEReader` 静态断言 PE 导入表 **零敏感 Win32 API**。
- **免注入置顶渲染宿主（`Spawn.OverlayHost.exe`，Medium + UIAccess IL）**：常驻内存 `< 18MB`，纯进程外硬件合成渲染，严禁图形 API Hook 注入。
- **按需即焚高敏工作进程（`BlueprintWorker` / `TrainerWorker` / `WorkflowWorker`）**：平时零驻留，通过 `.NET 10` 原生 `gRPC over Named Pipes`（带当前用户 SID SDDL ACL）与 `MemoryMappedFile` 环形帧缓冲通信；启动 `Tier 2/3` 强反作弊网游前强制物理清场并上锁，退出后严格执行 **15 秒 `SGuard64` 余温冷却窗**。

---

## 当前阶段与仓库结构

当前处于 **`M1`（仓库治理体系、顶层架构契约、五大模块规格正本与开发环境基线）**，阶段执行状态见 [当前状态](docs/delivery/status.md) 与 [M1 阶段计划](docs/delivery/stage-plan.md)；需要用户确认、安装环境或授权远端操作的事项统一维护在 [USER-ACTIONS.md](USER-ACTIONS.md)。

```text
spawn/
├── AGENTS.md / CLAUDE.md            # AI Agent 与开发者最高工作指令正本
├── REVIEW.md / CONTRIBUTING.md      # 集中审查口径、开发贡献与环境要求
├── DESIGN.md / SECURITY.md          # 视觉交互入口与安全隔离声明
├── USER-ACTIONS.md                  # 全仓唯一用户待办、环境准备与远端授权台账
├── docs/
│   ├── README.md                    # 全局文档总导航地图
│   ├── project-baseline.md          # 项目关键事实、技术定案基线与不做边界
│   ├── governance/                  # 文档治理政策与全仓文档注册表 (document-registry.json)
│   ├── product-rules/               # 五大长期核心产品不变量
│   ├── design-rules/                # Fluent + Mica 极客暗色美学、Nodify 与 Overlay 设计规范
│   ├── dev-rules/                   # 仓库地图、开源准则、数据安全、A/B+Swarm 协作、Git 与门禁规范
│   ├── requirements/                # 结构化产品需求正本 (REQ-SPW/PRG/ACH/WFL/NEX/SEC)
│   ├── decisions/                   # ADR-0001~0005、已确认决策台账与集中工程事项
│   ├── design/                      # 多进程系统架构、SQLite/Vault 数据模型、IPC 安全契约与交互流
│   ├── modules/                     # S.P.A.W.N. 五大领域模块正式实现规格书
│   ├── engineering/                 # 开源选型/NuGet/本机环境清单与 M1~M6 里程碑验收路线图
│   ├── delivery/                    # 当前阶段计划、报告、状态与机器可读需求追踪矩阵
│   ├── research/                    # 前期五份源码级深度技术调研档案（只读论据与协议逆向参考）
│   └── templates/                   # 交接、ADR、变更提案、模块规格与验证报告模板
├── scripts/
│   └── check_docs.py                # 零外部依赖 Python 3.12+ 文档完整性、链接与卫生门禁
└── (M2 起初始化 Spawn.sln，首发交付桌面主壳、启动接管与 Steamrip/GOG 单机成就 + Overlay 闭环)
```

---

## 文档导航与快速门禁

首次阅读或接手开发请按以下最短路径进入：

1. **治理与入口**：[文档总导航](docs/README.md) · [AGENTS 工作指令](AGENTS.md) · [项目关键事实](docs/project-baseline.md) · [用户待办](USER-ACTIONS.md)
2. **架构与契约**：[全景系统架构](docs/design/system-architecture.md) · [数据模型与存储](docs/design/data-model.md) · [IPC 与反作弊安全契约](docs/design/ipc-and-security-contracts.md)
3. **五大模块规格**：[模块总览](docs/modules/README.md) · [Spawn 启动管线](docs/modules/spawn-launcher.md) · [Nexus Mod 与工具箱](docs/modules/nexus-mods-and-utilities.md) · [Progress 存档与修改器](docs/modules/progress-saves-and-trainer.md) · [Achievement 成就与 Overlay](docs/modules/achievement-and-overlay.md) · [Workflow 代肝与蓝图](docs/modules/workflow-and-blueprint.md)
4. **选型与交付**：[技术选型与环境清单](docs/engineering/technology-selection.md) · [M1~M6 交付路线图](docs/engineering/delivery-plan.md) · [当前执行状态](docs/delivery/status.md)

在仓库根目录执行当前 `M1` 已启用的自动化门禁（需要 Python 3.12+ 标准库，零第三方依赖）：

```powershell
python scripts/check_docs.py
git diff --check
```

进入 `M2` 初始化 `Spawn.sln` 后（本机 `.NET 10 SDK 10.0.404` 已验证就绪，见 [USER-ACTIONS.md](USER-ACTIONS.md) `ACT-001`），日常增量开发运行 `python scripts/quick_gate.py`，整阶段交付运行完整测试与 `Spawn.Architecture.GateTests` PE 导入表审计门禁，详见 [质量门禁规范](docs/dev-rules/quality-gates.md)。

---

## 许可证

本仓库自有源码与文档采用 [Apache License 2.0](LICENSE) 开源许可。第三方开源依赖及外部联动工具保留各自原始许可证与版权声明，选型与许可证兼容性边界见 [开源与架构规则](docs/dev-rules/architecture-and-open-source.md) 与 [技术选型](docs/engineering/technology-selection.md)。
