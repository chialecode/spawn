# SPAWN 项目交付总状态看板（Delivery Status）

> **最后更新**：2026-11-22
> **当前活跃阶段**：`M3` — 单机外部修改器安全管家 + 启动前轻量存档快照 + 正式版每周发布与签名自动更新（计划版本 `1.1`，阶段状态 `reviewed`：`M3-W00` ~ `M3-W12` 已全部本地交付；Agent A 整阶段审查 `F-01` ~ `F-03` 已返工修复，`321 / 321` .NET 测试 + `10 / 10` 发布脚本测试通过，Agent A 2026-11-22 复审通过，待用户授权 `ACT-008`）
> **当前执行状态**：**`M2` 已按 `ACT-007` 经 [PR #2](https://github.com/chialecode/spawn/pull/2) 合入 `main`（`5bcd04c26372602d89d5d4e9eb541cec8b526193`）；`feat/m3-trainer` 已完成全部工作包 `M3-W00` ~ `M3-W12`（含前期已获 Agent A `reviewed` 的单机外部修改器安全管家、启动前轻量存档快照、独立 `修改器` 侧栏与 FLiNG 下载误报修复，以及 `1.1` 增补的正式版自包含打包与 ECDSA P-256 签名清单、`.github/workflows/release.yml` 每周发布流水线与结构门禁、正式版签名应用内自动更新与设置页 `更新` 分类）；`M3-A-00` ~ `M3-A-22` 全量自动化门禁实跑全绿（.NET `321 / 321` + Python `10 / 10`，0 警告 0 漏洞 0 Copyleft 0 新增 NuGet 包），`W10` ~ `W12` 已通过独立分包审查；Agent A 整阶段审查结论“需返工”的 `F-01`（更新器超时/取消状态卡死与命令异常外泄）、`F-02`（发布工作流限定 `main`）、`F-03`（`spawn-test` 身份文档同步）已全部修复并收束回唯一阶段提交，Agent A 2026-11-22 复审 `reviewed`（0 遗留项）；本机 `SPAWN Dev` 按 `D-32` 随最终提交自动静默更新驻留系统托盘，`M3-M-01`/`M3-M-02`/`M3-M-03` 保持 `pending`/`not-run`**
> **关联文档**：[M3 阶段计划](stage-plan.md) · [M3 阶段验收用例集](m3-stage-cases.json) · [M3 阶段执行报告](stage-report.md) · [里程碑路线图](../engineering/delivery-plan.md) · [待收敛工程事项台账](../decisions/open-questions.md) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 仓库基线与当前分支态势

| 状态维度 | 当前事实 |
| :--- | :--- |
| **远端仓库地址** | `https://github.com/chialecode/spawn.git` |
| **不可变第一提交锚点** | `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（0 字节 `README.md`，含 DCO 签名） |
| **最新 `main` / `origin/main` 基线** | `5bcd04c26372602d89d5d4e9eb541cec8b526193`（2026-11-21 经 [PR #2](https://github.com/chialecode/spawn/pull/2) Squash 合入 `M2`） |
| **当前本地工作分支** | `feat/m3-trainer`（唯一父提交锁定 `5bcd04c26372602d89d5d4e9eb541cec8b526193`，遵守单阶段单提交 DCO `amend` 纪律 `D-14`） |
| **本机 `SPAWN Dev` 自动安装状态** | 已通过 `python -B scripts/package.py --install-dev` 免 UAC 静默更新至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`（含 `Workers\Trainer\Spawn.TrainerWorker.exe` 与 `THIRD-PARTY-NOTICES.txt`，根目录零 Worker 泄漏，脱离作业对象 `--background` 驻留系统托盘，`dev-workspace.json` 已桥接仓库 `.local\games` 与 `.local\secrets`，回执见 `.local/reports/m3/install-dev.json`） |
| **质量门禁状态** | `python -B scripts/quick_gate.py` 集成全绿（4 套 .NET 测试工程共 **`321 / 321 passed`** + `scripts/test_release.py` **`10 / 10 passed`**，`0` 警告 `0` 错误，离线网络绊线零触发，含 `spawn-dev-test` 打包隔离演练与 `UpdatePublicKeyParityGateTests`/`ReleaseWorkflowGateTests`）；`release.py --drill-install`（`CI=true`）全绿（正式版自包含安装器 `76.42 MiB`、符号包 `664.5 KiB`、开发版 `12.00 MiB`）；`dotnet list Spawn.sln package --vulnerable --include-transitive` 报 `0` 漏洞；`13` 份 `obj/project.assets.json`（`49` 传递包）零 `GPL`/`AGPL`/`LGPL` 包；明细与 `not-run` 边界见 [阶段报告](stage-report.md) |
| **远端推送状态** | 遵循保守远端策略（`D-15`），`feat/m3-trainer` 仅保存在本地，严禁擅自推送；Agent A 已于 2026-11-22 复审通过 `F-01` ~ `F-03` 返工，`ACT-008`（推送与 PR）已申请用户明确授权，未获授权前不推送 |

---

## 2. 全生命周期里程碑进度总览（`M1` ~ `M6`）

| 阶段 | 里程碑名称 | 核心交付范围 | 当前状态 | 关联验收门槛 |
| :---: | :--- | :--- | :---: | :--- |
| `M1` | 治理与架构契约基线 | 远端规则集加固、根治理套件、结构化需求、ADR、顶层设计、5 篇模块规格、`.NET 10 LTS` 工具链矩阵、`check_docs.py` 门禁 | **已合并入 `main`（[PR #1](https://github.com/chialecode/spawn/pull/1)）** | `AC-01`（已通过） |
| `M2` | 启动器、单机成就与数据管理首发闭环 | `.NET 10` `Spawn.sln`、三栏无边框原生 WPF 主壳（`D-20`）、`PEReader` 门禁、GOG/Steamrip 识别刮削、游戏数据可逆清理（`D-22`）、双引擎追踪与 15s 反作弊守卫、四类成就捕获（`D-21`）、`Spawn.OverlayHost` 实时弹窗及 `SPAWN Dev` 自动安装（`D-23`） | **已合并入 `main`（[PR #2](https://github.com/chialecode/spawn/pull/2)）** | `AC-02` ~ `AC-08` |
| **`M3`** | **单机外部修改器安全管家 + 每周发布与签名更新** | 洁净室 FLiNG 发现 + 用户显式触发下载（被动 JSD 零误报 + 作用域 Cookie + 归档直链合成）+ 本地导入、`A0+A` 零注入默认静音、`Workers\Trainer\Spawn.TrainerWorker.exe` 分级按需 UAC + 倒置 `NamedPipeJsonRpc` + `KILL_ON_JOB_CLOSE` + `MAPVK_VK_TO_VSC_EX` 热键代理、四层 `Tier 0` + 混合单机 `VerifiedOfflineBypass` + 2s 急停看门狗 + `LOOP-06` 闭环、启动前轻量存档快照与一键回滚、Defender 单目录排除助手、独立 `修改器` 一级侧栏与 `全部修改器总览` + 同步详情子页签、顶栏精简收口与合规打包（`D-25` ~ `D-29`）；**`1.1` 增补**：正式版 `win-x64` 自包含打包与符号剥离、纯 BCL ECDSA P-256 DER 签名清单、`.github/workflows/release.yml` 四任务每周发布流水线与结构门禁、正式版签名应用内自动更新与设置页 `更新` 分类（`D-30` ~ `D-33`） | **`M3-W00` ~ `M3-W12` 本地已完成并通过全量集成门禁（.NET `321 / 321` + Python `10 / 10` 全绿，W10~W12 独立分包审查全绿，Agent A 审查 `F-01` ~ `F-03` 已返工），Agent A 复审 `reviewed`；`ACT-008` 已申请用户授权** | `AC-12`、`AC-12b`、`AC-25` |
| `M4` | 非侵入 Mod 中枢、存档时间机器轨道与高阶环境 | CAS 硬链接部署、`XxHash3` 配置回收、四大引擎加载器、多维 Profile `<100ms` Junction 原子切换；**自 `M3` 顺延**的 `Ludusavi` 索引、`FastCDC` 时间机器与 `32MB SPPK` 云同步；HDR/转码/CPU Sets/伴生环境管线及 `Alt+Z` 游戏内交互大面板联动 | 已规划 | `AC-09` ~ `AC-11`、`AC-13` ~ `AC-16` |
| `M5` | 二次元并发 DAG 顶层编排 | `MAA` 预写防脱管、`Maa.Framework` C# 内嵌、`MaaFgo+BBChannel` 双端口桥接、米哈游/库洛系 CLI 统管、四维资源锁并发 DAG 与夜间无人值守战报闭环 | 已规划 | `AC-17` ~ `AC-20` |
| `M6` | UE 蓝图虚拟机与极客收官 | `Nodify` 千节点 60FPS 蓝图工坊、Kismet 发光单步调试、`WGC`/`OpenCV`/`PaddleOCR` + `L1~L4` 输入、6 大极客扩展工具箱、收窄原生修改后端（`D-29`）与正式发布签名包 | 已规划 | `AC-21` ~ `AC-24` |

---

## 3. 当前待处理与 `M3` 实机体验清单

1. **`H-001` 与 `H-002` 人工实机体验（非阻塞）**：本机 `SPAWN Dev` 已静默更新并驻留系统托盘，用户可直接双击托盘/桌面快捷方式按 [USER-ACTIONS.md](../../USER-ACTIONS.md) 体验 `H-001`（含顶栏移除后于 `设置 → 常规与游戏库` 扫描及 `D-24` 音效试听）与 `H-002`（真实修改器静音、无小键盘热键、单次 UAC、启动前快照回滚与 `OSS-07` ①④⑤ 反馈）。
2. **远端集成（严格保守）**：`feat/m3-trainer` 仅保存在本地，严禁未授权 `git push` 或创建 PR；Agent A 已复审通过，`ACT-008` 待你明确授权；每周发布另需 `ACT-009`（离线备份私钥、配置 `release` 环境 Secret、限定 `release` 环境仅 `main` 可部署与补充远端 Actions 白名单）及 `ACT-010`（发布开关）。

---

## 4. 非阻塞后续演进跟踪环（Non-Blocking Follow-up Loops）

以下工程适配与二期增强事项持续维护于 [open-questions.md](../decisions/open-questions.md)，按对应里程碑触发收敛，**不阻塞当前阶段推进**：

- **外部生态与系统 API 实机适配（`OSS-01` ~ `OSS-07`）**：受保护目录 `ZBID_UIACCESS` 双态（`OSS-04`）、存档排他锁 `AlphaVSS` 提权边界随时间机器顺延 `M4`（`OSS-05`）、许可证合规物料 `M3` 增量已收敛待 `M5` 续接 `Maa.Framework`（`OSS-06`）、`M3` 修改器实机项中 `UNV-02`/合成解压/`NAudio 2.4.0` 已验证定案而 ①③④⑤ 待 `H-002` 实机（`OSS-07`）及 `M5/M6` 外部工具桥接适配（`OSS-01` ~ `OSS-03`）。
- **`LOOP-06`（`Resolved (M3)`）**：`Tier 2/3` 启动前自动终止自有 `Spawn.TrainerWorker` + 二次净场复检与改名 CE 驱动只读元数据 / `CEDRIVER*` 识别已在 `M3-W03`/`W05`/`W08` 完整闭环。
- **高阶能力分期合龙（`LOOP-01` ~ `LOOP-05`、`LOOP-07`、`LOOP-08`）**：`M6` 收窄原生修改后端、可选硬件 HID 执行器、专业版桌面分身并发槽、正式版自动更新验签流、`%ProgramFiles%` 免 UAC 提权包与 XInput 手柄热键代理。
