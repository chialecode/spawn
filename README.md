# SPAWN

**面向硬核极客玩家的 100% 纯原生 Windows 全能游戏中枢。**

**S.P.A.W.N.** = **Spawn**（启动与环境管线）+ **Progress**（存档时间机器与修改器管家）+ **Achievement**（全渠道离线/正版成就与高刷 Overlay）+ **Workflow**（二次元外部代肝并发编排与类 UE 蓝图自动化）+ **Nexus**（非侵入式 Mod 与多维快照及极客工具箱）。

本项目针对 Windows 10 (21H1+) / Windows 11 x64 平台原生打造，技术栈锁定 **C# 14 (`.NET 10` LTS, `net10.0-windows`) + 原生无边框三栏 `WPF` + `miroiu/nodify` + `SkiaSharp` + `NAudio.Wasapi` + `SQLite WAL`**，彻底拒绝任何 WebView2、Electron 或前端混合套壳方案，由 **Anti-Cheat Guard 主从进程物理隔离架构** 为强反作弊网游与重度魔改单机共处一机保驾护航。

---

## 五大核心业务域

| 核心域 | 工程模块与宿主 | 核心极客能力概览 |
| :--- | :--- | :--- |
| **S — Spawn**<br/>启动与环境管线 | `Spawn.Modules.Launcher`<br/>(`Spawn.Host`) | 免提权 Job Object IOCP（无强杀标志）+ `0x1000` 35s 目录破壳追踪双引擎；融合手柄/Overlay 白名单的 Active/Background/AFK 三态精准时长；GOG 离线包（`goggame-*.info` + `webcache.zip` 美工直提）与 Steamrip 免安装游戏智能识别刮削；`M4` 演进 Pre/Post-Launch HDR/转区/CPU Sets 自愈管线。 |
| **P — Progress**<br/>单机修改器安全管家与存档快照 | `Spawn.Modules.Progress`<br/>+ `Spawn.TrainerWorker` | **M3 交付范围**：英文标准名/版本指纹、洁净室 FLiNG 发现 + 用户显式触发下载 + 本地拖放导入、**`A0+A` 零改 EXE 默认完全静音**（27B 静音 SMF + MTA WASAPI 全端点）、隔离 `Workers\Trainer\Spawn.TrainerWorker.exe`（`D-11` 分级按需提权 + 倒置 `NamedPipeJsonRpc` + `KILL_ON_JOB_CLOSE` + `MAPVK_VK_TO_VSC_EX` 无小键盘热键代理）、四层 `Tier 0` + 混合单机 `VerifiedOfflineBypass` + 2s 急停看门狗、启动修改器前轻量存档快照与一键回滚、Defender 沙箱单目录排除助手；**M4** 演进 `Ludusavi` + `FastCDC` 时间机器与 `32MB SPPK` 云同步（`D-25`）；**M6** 演进收窄原生修改后端（`D-29`）。 |
| **A — Achievement**<br/>全渠道成就与高刷 Overlay | `Spawn.Modules.Achievement`<br/>+ `Spawn.OverlayHost` | 四类全覆盖单机成就通道：① Goldberg/`gbe_fork`、RUNE/CODEX、EMPRESS、TENOKE、Online-Fix、ALI213、3DM、SmartSteamEmu 等 16+ 模拟器与 Steam Binary VDF；② GOG `127.0.0.1:9977` Comet 兼容守护免改 DLL 捕获；③ 无 `Galaxy.dll` 纯 DRM-Free 游戏原生存档声明式规则引擎（首发内置 `LIMBO`）；④ 无成就桩一键无损 `gbe_fork` 激活器；中文剧透保护 Schema + `SkiaSharp` 灰锁图合成 + 零抢焦点三主题实时跳杯弹窗（Steam/PS5 白金连发/Xbox 紫钻）。 |
| **W — Workflow**<br/>二次元并发编排与 UE 蓝图 | `Spawn.Modules.Workflow`<br/>+ `Spawn.WorkflowWorker`<br/>+ `Spawn.BlueprintWorker` | **外部二游顶层编排**：内嵌官方 `Maa.Framework` C# 原生驱动 `interface.json` V2 全系脚本，打通 `MaaFgo` + `BBChannel` 双端口（`25001/25002`）后台调度，统合 `MAA`、`BetterGI`、`m7a.exe`、`SRC`、`ZZZ-OneDragon`、`ok-ww` 构建四维资源锁并发 DAG。<br/>**类 UE 蓝图引擎**：`Nodify` 双引脚画布 + Kismet 异步单步发光调试 VM + `WGC + OpenCvSharp4 + Sdcb.PaddleOCR` + L1~L4 多模态输入。 |
| **N — Nexus**<br/>非侵入 Mod 与极客工具箱 | `Spawn.Modules.Nexus`<br/>(`Spawn.Host`) | CAS 只读仓库 + 同盘免提权 `CreateHardLinkW` + `XxHash3` 可变配置差异回收；5 阶段 WAL + 免提权 NTFS Directory Junction `<100ms` 多维 Profile 原子切换；原生编排 Unity Doorstop、RE-UE4SS、Reloaded-II 与二次元 `XXMI`；内置便携沙箱、WOF 香农熵透明压缩、DLSS 4 签名替换、Sunshine 虚拟屏、独立声卡分轨与单机急救 6 大极客工具。 |

---

## 物理隔离安全架构基座

SPAWN 从解决方案工程结构（`Spawn.sln`）与操作系统进程完整性级别（Integrity Level）双层切断敏感能力污染：

- **纯净常驻宿主（`Spawn.Host.exe`，Medium IL）**：承载通顶无边框三栏原生 WPF 界面（`52px` 图标栏 + `264px` 上下文侧栏 + 主面板），由 `Spawn.Architecture.GateTests` 在本地门禁与 CI 中通过 `PEReader` 静态断言 PE 导入表 **零敏感 Win32 API**。
- **免注入置顶弹窗宿主（`Spawn.OverlayHost.exe`）**：施加 `WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 零抢焦点穿透扩展样式，通过 SDDL ACL `NamedPipeJsonRpc` 命名管道接收实时跳杯请求。
- **按需即焚高敏工作进程（`TrainerWorker` 已于 `M3` 独立发布至 `Workers\Trainer\`；`BlueprintWorker` / `WorkflowWorker` 随 `M5`~`M6` 接入）**：按 `D-11` 分级按需提权（免 UAC 仅限 `LOOP-07`），平时零驻留，启动 `Tier 2/3` 强反作弊网游前强制清场并上锁，退出后严格执行 **15 秒反作弊守护进程余温冷却窗**。

---

## 当前阶段与仓库结构

`M1` 架构契约基线（[PR #1](https://github.com/chialecode/spawn/pull/1)）与 `M2` 启动器及单机成就闭环（[PR #2](https://github.com/chialecode/spawn/pull/2)）均已合入 `main`；当前处于 **`M3`（单机外部修改器安全管家 + 启动前轻量存档快照）**，执行详情见 [当前状态](docs/delivery/status.md)、[M3 阶段计划](docs/delivery/stage-plan.md) 与 [M3 阶段报告](docs/delivery/stage-report.md)；用户事项与远端授权统一维护在 [USER-ACTIONS.md](USER-ACTIONS.md)。

```text
spawn/
├── Spawn.sln / global.json          # .NET 10 (LTS) 解决方案与 SDK 10.0.404 版本锚定
├── Directory.Build.props            # C# 14 / Nullable / 平台编译全局约束
├── Directory.Packages.props         # NuGet 中央包版本管理 (CPM)
├── THIRD-PARTY-NOTICES.txt          # 第三方开源组件、AreWeAntiCheatYet 数据集与 gbe_fork LGPL-3.0 合规声明
├── AGENTS.md / CLAUDE.md            # AI Agent 与开发者最高工作指令正本
├── REVIEW.md / CONTRIBUTING.md      # 集中审查口径、开发贡献与环境要求
├── DESIGN.md / SECURITY.md          # 三栏极简视觉规范入口与安全隔离声明
├── USER-ACTIONS.md                  # 全仓唯一用户待办、环境配置与远端授权台账
├── assets/                          # 双通道原生图标资产 (icon.ico/png 与琥珀色 DEV 角标 icon-dev.ico/png)
├── installer/
│   └── spawn.iss                    # Inno Setup 7 免 UAC 按用户安装包模板 (隔离 AppId、Workers\Trainer\、无损升级)
├── src/
│   ├── Spawn.Contracts/             # 零业务跨模块强类型 DTO、修改器/存档快照/游戏数据清理与 IPC 接口契约 (net10.0)
│   ├── Spawn.Core/                  # SQLite WAL Schema v4、SpawnPaths、DPAPI、四层反作弊守卫、0x1000 探测与 NamedPipeJsonRpc
│   ├── Spawn.Modules.Launcher/      # GOG/Steamrip 扫描刮削、PCGamingWiki/引擎游戏数据管理、双引擎追踪与三态计时
│   ├── Spawn.Modules.Achievement/   # 四类全覆盖单机成就解析监听引擎、中文剧透 Schema 与 gbe_fork 无损激活器
│   ├── Spawn.Modules.Progress/      # 洁净室 FLiNG 发现与安全导入、A0+A 零注入静音、启动前轻量存档快照与 Defender 排除助手
│   ├── Spawn.Host/                  # 纯净三栏无边框原生 WPF 主程序 (成就 | 游戏数据 | 修改器三子页签与系统托盘)
│   ├── Spawn.OverlayHost/           # 独立零抢焦点三主题实时成就跳杯弹窗宿主 (WinExe)
│   └── Spawn.TrainerWorker/         # 独立隔离修改器托管进程 (发布至 Workers\Trainer\，M3 零跨进程内存 API)
├── tests/
│   ├── Spawn.Architecture.GateTests/# PEReader 静态审计 Clean Host & TrainerWorker 零禁用 API 与 UI 无头门禁
│   ├── Spawn.Core.UnitTests/        # 16+ 模拟器、四层 Tier 0、倒置管道鉴权、PCGW/数据清理与隔离打包单测
│   ├── Spawn.Modules.Progress.UnitTests/ # 修改器发现/下载导入、A0+A 静音、运行时、启动前快照与 Defender 助手单测
│   ├── Spawn.TrainerWorker.UnitTests/    # KILL_ON_JOB_CLOSE 进程树清零、命令白名单与无小键盘热键代理单测
│   └── Spawn.TrainerWorker.TestStub/     # 无窗口合成多子进程桩程序 (供 Worker 作业对象测试使用)
├── docs/                            # 治理、需求、ADR、系统设计、五大模块规格、交付追踪与前期调研归档
└── scripts/
    ├── check_docs.py                # 零依赖 Python 3.12+ 文档注册表、链接锚点与路径卫生门禁
    ├── quick_gate.py                # 低优先级后台静默一键门禁 (check_docs + Release 编译 + 4 工程测试 + diff --check)
    ├── generate_icons.py            # 零依赖 SDF 超采样正式版与 DEV 角标图标生成器
    └── package.py                   # Inno Setup 7 原子打包 (含 Workers\Trainer\ 隔离) 与 SPAWN Dev 自动静默安装器
```

---

## 文档导航与快速门禁

1. **治理与入口**：[文档总导航](docs/README.md) · [AGENTS 工作指令](AGENTS.md) · [项目关键事实](docs/project-baseline.md) · [用户待办](USER-ACTIONS.md)
2. **架构与契约**：[全景系统架构](docs/design/system-architecture.md) · [数据模型与存储](docs/design/data-model.md) · [IPC 与反作弊安全契约](docs/design/ipc-and-security-contracts.md)
3. **五大模块规格**：[模块总览](docs/modules/README.md) · [Spawn 启动管线](docs/modules/spawn-launcher.md) · [Nexus Mod 与工具箱](docs/modules/nexus-mods-and-utilities.md) · [Progress 存档与修改器](docs/modules/progress-saves-and-trainer.md) · [Achievement 成就与 Overlay](docs/modules/achievement-and-overlay.md) · [Workflow 代肝与蓝图](docs/modules/workflow-and-blueprint.md)
4. **选型与交付**：[技术选型与环境清单](docs/engineering/technology-selection.md) · [M1~M6 交付路线图](docs/engineering/delivery-plan.md) · [当前执行状态](docs/delivery/status.md)

日常开发与提交前在仓库根目录运行一键静默门禁（需 `.NET 10 SDK` 与 `Python 3.12+`）：

```powershell
python -B scripts/quick_gate.py
```

打包并静默安装/更新本机开发版 **`SPAWN Dev`**（安装至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`，自动生成桌面/开始菜单快捷方式并以 `--background` 驻留系统托盘，数据隔离于 `%LOCALAPPDATA%\spawn-dev`）：

```powershell
python -B scripts/package.py --install-dev
```

---

## 许可证

本仓库自有源码与文档采用 [Apache License 2.0](LICENSE) 开源许可。第三方开源依赖及外部联动工具保留各自原始许可证与版权声明，选型与许可证兼容性边界见 [开源与架构规则](docs/dev-rules/architecture-and-open-source.md) 与 [技术选型](docs/engineering/technology-selection.md)。
