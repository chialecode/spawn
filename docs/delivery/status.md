# SPAWN 项目交付总状态看板（Delivery Status）

> **最后更新**：2026-11-15
> **当前活跃阶段**：`M1` — 文档治理、远端安全基线与系统架构契约（计划版本 `1.0`，已锁定 `.NET 10 LTS` 与 `M2` 首发单机成就路线）
> **当前执行状态**：**M1 全部交付物已在本地分支 `feat/m1-foundation` 落盘并通过文档门禁，本机 `.NET 10 SDK 10.0.404` 已就绪，等待仓库所有者审阅授权远端推送（保守远端边界 `D-15`）**
> **关联文档**：[M1 阶段计划](stage-plan.md) · [M1 阶段执行报告](stage-report.md) · [里程碑路线图](../engineering/delivery-plan.md) · [待收敛工程事项台账](../decisions/open-questions.md) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 仓库基线与当前分支态势

| 状态维度 | 当前事实 |
| :--- | :--- |
| **远端仓库地址** | `https://github.com/chialecode/spawn.git` |
| **不可变第一提交锚点** | `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（0 字节 `README.md`，含 DCO 签名，已同步 `main` 与 `origin/main`） |
| **当前本地工作分支** | `feat/m1-foundation`（遵守单阶段单提交 DCO `amend` 纪律 `D-14`） |
| **目标技术栈与本机 SDK** | **`.NET 10 (LTS)`（`net10.0` / `net10.0-windows10.0.26100.0`）**；本机已实测安装 **`SDK 10.0.404`**（`ACT-001` 已完成） |
| **远端规则集与安全策略** | `main-protection`（Ruleset ID `27309712`）已激活生效；Squash-only 合并、DCO 签名要求、Private Vulnerability Reporting、Dependabot Alerts/Security Updates、Secret Scanning & Push Protection 及 Actions SHA Pinning 白名单均已 API 开启并回读确认 |
| **远端推送状态** | 遵循保守远端策略，`feat/m1-foundation` 尚未推送至远端，等待用户审阅本阶段成果后按 `ACT-002` 指令放行 |

---

## 2. 全生命周期里程碑进度总览（`M1` ~ `M6`）

| 阶段 | 里程碑名称 | 核心交付范围 | 当前状态 | 关联验收门槛 |
| :---: | :--- | :--- | :---: | :--- |
| **`M1`** | **治理与架构契约基线** | 远端规则集加固、根治理套件、47 项结构化需求、5 篇 ADR、4 篇顶层设计、5 篇模块规格、`.NET 10 LTS` 开源与工具链矩阵、`check_docs.py` 追踪门禁 | **本地已完成（待 `ACT-002` 授权合并）** | `AC-01` |
| `M2` | 启动器与单机成就首发闭环 | `.NET 10` `Spawn.sln`、纯净 `WPF-UI` 主壳、`PEReader` 门禁、Steamrip/GOG 游戏库管理与刮削、双通道启动追踪与反作弊守卫、15+ 模拟器与 GOG `9977` 实时捕获、一键 `gbe_fork` 激活、中文剧透 Schema、`Spawn.OverlayHost` 144Hz 实时弹窗与成就管理面板 | **开发工具链已就绪（待 `M1` 合并即启动）** | `AC-02` ~ `AC-08` |
| `M3` | 存档时间机器与修改器管家 | `Ludusavi` 预编译索引、`FastCDC` 存档去重时间机器、`32MB SPPK` 自建云同步与分叉向导、FLiNG 一键匹配下载、`A0+A` 零报毒去音乐与 `TrainerWorker` 热键代理 | 已规划 | `AC-09` ~ `AC-12` |
| `M4` | 非侵入 Mod 中枢与高阶环境 | CAS 硬链接部署、`XxHash3` 配置回收、四大引擎加载器编排、多维 Profile `<100ms` Junction 原子切换、HDR/转码/CPU Sets/伴生环境管线及 `Alt+Z` 游戏内交互大面板联动 | 已规划 | `AC-13` ~ `AC-16` |
| `M5` | 二次元并发 DAG 顶层编排 | `MAA` 预写防脱管、`Maa.Framework` C# 内嵌、`MaaFgo+BBChannel` 双端口桥接、米哈游/库洛系 CLI 统管、四维资源锁并发 DAG 与夜间无人值守战报闭环 | 已规划 | `AC-17` ~ `AC-20` |
| `M6` | UE 蓝图虚拟机与极客收官 | `Nodify` 千节点 60FPS 蓝图工坊、Kismet 发光单步调试、`WGC`/`OpenCV`/`PaddleOCR` + `L1~L4` 输入、6 大极客扩展工具箱、二期原生 `.CT` 引擎与安装包 | 已规划 | `AC-21` ~ `AC-24` |

---

## 3. 进入 `M2` 首发代码开发的就绪清单

1. **本机核心开发环境（`ACT-001` — 已完成 `done`）**：当前工作站已实测具备 **`.NET 10 SDK 10.0.404`**（含 `.NET 10.0.15` Desktop Runtime）、Windows 11 SDK `10.0.26100.0`（含 x64 `signtool.exe`）、VS Build Tools 18、Python `3.12.16`、Git `2.56.0`、`gh 2.103.3` 与 Inno Setup 7，无需额外安装任何必选编译器。
2. **M1 远端推送与合并授权（`ACT-002` — 待授权）**：仓库所有者审阅本地 `feat/m1-foundation` 治理与架构文档体系后，按 [USER-ACTIONS.md](../../USER-ACTIONS.md) `ACT-002` 授权推送分支、通过 GitHub Actions `docs` 状态检查并 Squash 合并至 `main`，即可无缝切入 `M2` 编码。
3. **M2 可选实机联调资源（`ACT-006` — 非阻塞可选）**：后续实机体验时可按需配置个人免费 Steam Web API Key（不配亦可自动走本地缓存与免 Key 镜像兜底）及本地 Steamrip / GOG 单机游戏样本。

---

## 4. 非阻塞后续演进跟踪环（Non-Blocking Follow-up Loops）

以下工程验证与二期增强事项已完整建档于 [open-questions.md](../decisions/open-questions.md)，按对应里程碑触发收敛，**不构成 M1 准出或 M2 启动阻塞**：

- **外部生态与系统 API 实机适配（`OSS-01` ~ `OSS-06`）**：涵盖 M2 便携版与受保护目录 `ZBID_UIACCESS` 双态切换（`OSS-04`）、M2/M5/M6 `LGPL-3.0` 动态库合规通知自动生成（`OSS-06`）、M3 排他锁 `AlphaVSS` 按需提权（`OSS-05`）、M5 `Maa.Framework` V1/V2 Schema 降级（`OSS-01`）与 `BBChannel` 反射防卫（`OSS-02`）及 M6 音频策略 IID 多版本适配（`OSS-03`）。
- **高阶能力分期合龙（`LOOP-01` ~ `LOOP-05`）**：涵盖 M6 二期原生 `.CT` 内存后端、可选硬件 HID 执行器、专业版桌面分身并发槽、Inno Setup 7 双模式打包脚本及远期可选的远古 D3D9 兼容层。
