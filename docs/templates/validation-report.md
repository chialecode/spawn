# 验证与阶段交付报告模板

本模板用于编写 `docs/delivery/stage-report.md` 及专项技术验证报告。核心纪律：**运行状态仅允许使用 `passed`、`failed`、`blocked`、`not-run` 四种标准值；未执行的检查严禁推断或标记为 `passed`**。

---

## 1. 验证基线元数据

- **阶段与计划版本**：如 `M2`（对应 `docs/delivery/stage-plan.md` `v1.0`）
- **本地分支与父提交 SHA**：`feat/m2-...`（父提交 `<40位完整SHA>`，交付提交以 `git rev-parse HEAD` 核验）
- **测试环境快照**：Windows 版本号、`.NET SDK` 版本、`Python` 版本、测试数据隔离目录（`%TEMP%\spawn-gate-...`）

---

## 2. 门禁命令与用例实跑矩阵

| 检查编号 / 门禁项 | 关联 `REQ-*` / 工作包 | 实际执行命令与输入夹具 | 退出码与证据摘要 | 状态（`passed`/`failed`/`blocked`/`not-run`） |
| :--- | :--- | :--- | :--- | :--- |
| `DOC-GATE` | 全仓文档与卫生 | `python scripts/check_docs.py` & `git diff --check` | `exit 0`，XX 篇文档与追踪矩阵通过 | `passed` |
| `ARCH-IAT` | `REQ-SEC-*` | `dotnet test tests/Spawn.Architecture.GateTests` | 扫描 `Spawn.Host.exe` 等导入表零敏感符号 | `passed` / `not-run` |
| `UNIT-CORE` | `M2-W01`~`W05` | `dotnet test tests/Spawn.Core.Tests` | 用例通过数、耗时与无窗口静音核验 | `passed` / `not-run` |

---

## 3. 未测边界（`not-run`）与已知限制说明

逐项列出当前因本机尚未安装特定 SDK/驱动、未配置可选云端凭据（`ACT-*`）或需真实高刷全屏游戏配合（`H-*`）而保持 `not-run` 的范围，确保审阅者一目了然。

---

## 4. 集中技术审查记录与三项独立结论

- **审查发现与闭环**：记录审查中发现的 `F-01`… 问题、修复方式与复跑门禁结果（无阻断问题则写明 `P0/P1 = 0`）。
- **三项正交结论**：
  1. **实现与自检状态**：`ready-for-review` / `completed`
  2. **集中技术审查结论**：`reviewed` / `changes-requested`
  3. **产品实机验收状态**：`pending`（列明顺延的 `H-*` / `ACT-*` 编号）
- **唯一推荐下一步**：指向跨角色交接动作，或指向 [USER-ACTIONS.md](../../USER-ACTIONS.md) 中待用户明确授权的具体 `ACT-*`。
