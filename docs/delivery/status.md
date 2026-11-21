# SPAWN 项目交付总状态看板（Delivery Status）

> **最后更新**：2026-11-21
> **当前活跃阶段**：`M2` — 三栏原生主壳、游戏库启动追踪、四通道单机成就自动捕获、游戏数据可逆清理与 `SPAWN Dev` 自动安装闭环（计划版本 `1.1`）
> **当前执行状态**：**`M1` 已按 `ACT-002` 经 [PR #1](https://github.com/chialecode/spawn/pull/1) 合入 `main`（`196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`）；`M2` 首轮审查 `F-01`~`F-08` 已返工，2026-11-21 Agent A 复审 **`reviewed`**（`P0`/`P1` = 0），同轮用户反馈（奖杯图标半缺、成就音效改用 Steam 桌面原版 `desktop_toast_default.wav`）已修复并 amend 回唯一阶段提交，门禁全绿并重新 `install-dev`；当前**等待用户授权 `ACT-007`**（`H-001` 非阻塞）**
> **关联文档**：[M2 阶段计划](stage-plan.md) · [M2 阶段执行报告](stage-report.md) · [里程碑路线图](../engineering/delivery-plan.md) · [待收敛工程事项台账](../decisions/open-questions.md) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 仓库基线与当前分支态势

| 状态维度 | 当前事实 |
| :--- | :--- |
| **远端仓库地址** | `https://github.com/chialecode/spawn.git` |
| **不可变第一提交锚点** | `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（0 字节 `README.md`，含 DCO 签名） |
| **最新 `main` / `origin/main` 基线** | `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`（2026-11-19 经 [PR #1](https://github.com/chialecode/spawn/pull/1) Squash 合入 `M1` 基线） |
| **当前本地工作分支** | `feat/m2-launcher-achievement`（唯一父提交锁定 `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`，遵守单阶段单提交 DCO `amend` 纪律 `D-14`） |
| **本机 `SPAWN Dev` 自动安装状态** | 已通过 `python -B scripts/package.py --install-dev` 免 UAC 静默安装至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`（桌面与开始菜单 `"SPAWN Dev"` 快捷方式就绪，系统托盘 `--background` 运行中，`dev-workspace.json` 已桥接仓库 `.local\games` 下全部 3 款真实测试样本） |
| **质量门禁状态** | `python -B scripts/quick_gate.py`（串联 `check_docs.py`、`dotnet build -c Release`、`Spawn.Architecture.GateTests` `PEReader` IAT 审计、`Spawn.Core.UnitTests` 全量测试 + 离线网络绊线、`git diff --check`）连续 3 次全绿（`73 / 73`），单独 `dotnet test Spawn.sln -c Release` 亦全绿；明细与 `not-run` 边界见 [阶段报告](stage-report.md) |
| **远端推送状态** | 遵循保守远端策略（`D-15`），`feat/m2-launcher-achievement` 仅保存在本地，尚未推送至远端，等待用户按 [USER-ACTIONS.md](../../USER-ACTIONS.md) `ACT-007` 明确放行 |

---

## 2. 全生命周期里程碑进度总览（`M1` ~ `M6`）

| 阶段 | 里程碑名称 | 核心交付范围 | 当前状态 | 关联验收门槛 |
| :---: | :--- | :--- | :---: | :--- |
| `M1` | 治理与架构契约基线 | 远端规则集加固、根治理套件、47 项结构化需求、5 篇 ADR、4 篇顶层设计、5 篇模块规格、`.NET 10 LTS` 工具链矩阵、`check_docs.py` 门禁 | **已合并入 `main`（[PR #1](https://github.com/chialecode/spawn/pull/1)）** | `AC-01`（已通过） |
| **`M2`** | **启动器、单机成就与数据管理首发闭环** | `.NET 10` `Spawn.sln`、三栏无边框原生 WPF 主壳与 220+ 虚拟化列表（`D-20`）、`PEReader` 门禁、深度子目录 GOG/Steamrip 识别刮削、PCGamingWiki/引擎游戏数据管理与可逆清理（`D-22`）、双引擎追踪与 15s 反作弊守卫、四类全覆盖成就捕获（`D-21`）、三主题 `Spawn.OverlayHost` 零抢焦点实时弹窗及 Inno Setup 7 `SPAWN Dev` 自动安装体系（`D-23`） | **本地已完成，A 复审 `reviewed`，等待 `ACT-007` 远端授权；本机 `SPAWN Dev` 已更新上机（`H-001` 可直接双击体验）** | `AC-02` ~ `AC-08` |
| `M3` | 存档时间机器与修改器管家 | `Ludusavi` 预编译索引、`FastCDC` 存档去重时间机器、`32MB SPPK` 自建云同步与分叉向导、FLiNG 一键匹配下载、`A0+A` 零报毒去音乐与 `TrainerWorker` 热键代理 | 已规划（待 `M2` 合并后启动） | `AC-09` ~ `AC-12` |
| `M4` | 非侵入 Mod 中枢与高阶环境 | CAS 硬链接部署、`XxHash3` 配置回收、四大引擎加载器编排、多维 Profile `<100ms` Junction 原子切换、HDR/转码/CPU Sets/伴生环境管线及 `Alt+Z` 游戏内交互大面板联动 | 已规划 | `AC-13` ~ `AC-16` |
| `M5` | 二次元并发 DAG 顶层编排 | `MAA` 预写防脱管、`Maa.Framework` C# 内嵌、`MaaFgo+BBChannel` 双端口桥接、米哈游/库洛系 CLI 统管、四维资源锁并发 DAG 与夜间无人值守战报闭环 | 已规划 | `AC-17` ~ `AC-20` |
| `M6` | UE 蓝图虚拟机与极客收官 | `Nodify` 千节点 60FPS 蓝图工坊、Kismet 发光单步调试、`WGC`/`OpenCV`/`PaddleOCR` + `L1~L4` 输入、6 大极客扩展工具箱、二期原生 `.CT` 引擎与正式发布签名包 | 已规划 | `AC-21` ~ `AC-24` |

---

## 3. 当前待用户处理与下一阶段（`M3`）准入清单

1. **免命令直接体验本机 `SPAWN Dev`（`H-001` — 非阻塞，随时可用）**：双击桌面/开始菜单 **`"SPAWN Dev"`** 快捷方式或右下角系统托盘琥珀色 `DEV` 图标，一键扫入 `.local\games` 下全部 3 款游戏（`LIMBO`、`The Stanley Parable Ultra Deluxe`、`Vampire Survivors`），体验 220+ 项虚拟化成就列表、`游戏数据` 一键清理恢复与实时跳杯弹窗（详见 [USER-ACTIONS.md](../../USER-ACTIONS.md)）。
2. **`M2` 远端推送与 PR #2 授权（`ACT-007` — 现已申请，待你回复）**：Agent A 复审已达 `reviewed`，按 `USER-ACTIONS.md` `ACT-007` 申请授权推送 `feat/m2-launcher-achievement`、创建 PR #2 并通过 GitHub Actions CI 后 Squash 合入 `main`，即可切入 `M3`。

---

## 4. 非阻塞后续演进跟踪环（Non-Blocking Follow-up Loops）

以下工程适配与二期增强事项持续维护于 [open-questions.md](../decisions/open-questions.md)，按对应里程碑触发收敛，**不阻塞 `M2` 交付或 `ACT-007` 集成**：

- **外部生态与系统 API 实机适配（`OSS-01` ~ `OSS-06`）**：按用户安装态 `WS_EX_TOPMOST | WS_EX_NOACTIVATE` 无边框覆盖与受保护目录 `%ProgramFiles%\spawn\Overlay\` `ZBID_UIACCESS` 自签双态切换（`OSS-04`）、动态链接 `LGPL-3.0` 组件声明（`OSS-06`）、`M3` 排他锁 `AlphaVSS` 按需提权（`OSS-05`）及 `M5/M6` 外部工具桥接适配（`OSS-01`~`OSS-03`）。
- **`F-03` 剩余部分（`LOOP-06`，`M3`）**：`Tier 2/3` 启动前自动终止 SPAWN 自有高敏 Worker（`M2` 仅检测阻断）与改名 CE 驱动识别。
- **高阶能力分期合龙（`LOOP-01` ~ `LOOP-05`）**：`M6` 二期原生 `.CT` 内存引擎、可选硬件 HID 执行器、专业版桌面分身并发槽与正式版自动更新验签流。
