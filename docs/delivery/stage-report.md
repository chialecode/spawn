# SPAWN M1 阶段执行与验证报告（Stage Report — M1 Foundation）

> **阶段代号**：`M1`（计划版本 `1.0` — `.NET 10 LTS` 与 `M2` 单机成就首发基线）
> **执行日期**：2026-11-15
> **所在分支**：`feat/m1-foundation`（基线 `main` @ `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`）
> **阶段结论**：**本地自动化门禁全部通过，本机 `.NET 10 SDK 10.0.404` 已就绪，等待用户审阅与 `ACT-002` 远端推送授权**
> **关联文档**：[项目交付总状态](status.md) · [M1 阶段计划](stage-plan.md) · [M1 阶段验收用例集](m1-stage-cases.json) · [需求追踪矩阵](traceability.json) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 阶段交付成果摘要

围绕 [stage-plan.md](stage-plan.md) 定义的五大工作包（`M1-W01` ~ `M1-W05`），本阶段完成了 SPAWN 从零初始化至完整开源治理与系统架构基线的建设，并同步落实了升级锁定 **`.NET 10 (LTS)`** 与 **`M2` 启动器 + Steamrip/GOG 单机成就闭环先行** 的用户决策：

1. **Git 不可变锚点与 GitHub 远端安全防线（`M1-W01`）**：
   - 建立不可变第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（`chore: initialize repository with empty README`，0 字节 `README.md`，附带标准 DCO `Signed-off-by`）并推送至 `origin/main`；
   - 切出本地阶段分支 `feat/m1-foundation`，落盘 `.gitignore`、`.gitattributes`、`.editorconfig`、`LICENSE`（Apache-2.0）、GitHub 策略声明快照（`.github/*.json`）、`dependabot.yml`、PR 模板及 SHA 锁定的 `.github/workflows/docs.yml`。
2. **文档即契约治理与机械化门禁（`M1-W02`）**：
   - 建立根级核心文档（`README.md`、`AGENTS.md`、`CLAUDE.md`、`CONTRIBUTING.md`、`REVIEW.md`、`SECURITY.md`、`DESIGN.md`、`USER-ACTIONS.md`）与 `docs/governance/`、`docs/product-rules/`、`docs/design-rules/`、`docs/dev-rules/`、`docs/templates/` 全套研发与多智能体协作规范；
   - 建立受控文档注册表 [document-registry.json](../governance/document-registry.json) 与零依赖门禁脚本 `scripts/check_docs.py`。
3. **需求、ADR 与四大系统顶层设计正本（`M1-W03`）**：
   - 冻结覆盖六大维度的 47 项结构化产品与非功能需求（[product-requirements.md](../requirements/product-requirements.md)）；
   - 汇总 `D-01` ~ `D-19` 已确认架构决策、[open-questions.md](../decisions/open-questions.md) 工程台账及 `ADR-0001` ~ `ADR-0005`（锁定纯原生 `.NET 10 LTS` + `WPF-UI` + `Nodify`）；
   - 输出 [system-architecture.md](../design/system-architecture.md)、[data-model.md](../design/data-model.md)、[ipc-and-security-contracts.md](../design/ipc-and-security-contracts.md) 与 [interaction-and-workflows.md](../design/interaction-and-workflows.md) 四大顶层正本。
4. **五大业务模块规格与调研归档治理（`M1-W04`）**：
   - 完成 [spawn-launcher.md](../modules/spawn-launcher.md)、[nexus-mods-and-utilities.md](../modules/nexus-mods-and-utilities.md)、[progress-saves-and-trainer.md](../modules/progress-saves-and-trainer.md)、[achievement-and-overlay.md](../modules/achievement-and-overlay.md)、[workflow-and-blueprint.md](../modules/workflow-and-blueprint.md) 五大模块规格正本；
   - 将 5 篇前期专题调研报告规范化归档至 `docs/research/01..05-*.md` 并移除临时总纲文件。
5. **工程选型、开发环境核验与重排交付路线图（`M1-W05`）**：
   - 输出 38+ 开源参考合规矩阵、`.NET 10` 隔离进程域 NuGet 清单、`gbe_fork` 激活器 DLL 资源准备规范与本机环境核验全表（[technology-selection.md](../engineering/technology-selection.md)，实测本机已安装 **`.NET 10 SDK 10.0.404`**，`ACT-001` 已完成）；
   - 按“M2 启动器与 Steamrip/GOG 单机成就自动捕获弹窗闭环 -> M3 存档时间机器与外部修改器 -> M4 硬链接 Mod 中枢与高阶环境/`Alt+Z` 大面板 -> M5 二次元并发 DAG -> M6 类 UE 蓝图虚拟机与极客收官”输出 [delivery-plan.md](../engineering/delivery-plan.md) 及双向一致的 [m1-stage-cases.json](m1-stage-cases.json)、[traceability.json](traceability.json)。

---

## 2. 远端 GitHub `chialecode/spawn` 配置与回读核验事实

通过 GitHub REST API 执行配置并逐项回读验证，全部安全与治理策略一次性生效（**零 403/404 失败项，无需用户在网页端补做手工安全开关设置**）：

| 审计域 | 远端实测回读状态 |
| :--- | :--- |
| **分支与合并策略** | `default_branch="main"`；仅启用 `allow_squash_merge=true`（`allow_merge_commit=false`、`allow_rebase_merge=false`、`allow_auto_merge=false`）；`delete_branch_on_merge=true`；`squash_merge_commit_title="PR_TITLE"`、`squash_merge_commit_message="PR_BODY"`；`web_commit_signoff_required=true`；关闭未使用的 `issues/wiki/projects/discussions` |
| **代码安全与漏洞防护** | Private Vulnerability Reporting `enabled=true`；Dependabot Alerts `enabled`；Dependabot Security Updates `enabled`；Secret Scanning & Push Protection `enabled` |
| **GitHub Actions 策略** | `allowed_actions="selected"`；`sha_pinning_required=true`；允许列表严格限定 `["actions/checkout@*", "actions/setup-dotnet@*"]`；`default_workflow_permissions="read"`；`can_approve_pull_request_reviews=false`；外部贡献者工作流审批 `all_external_contributors` |
| **主干保护规则集** | Ruleset `main-protection`（ID `27309712`，`enforcement="active"`，`bypass_actors=[]`，目标 `refs/heads/main`）：强制启用禁止删除（`deletion`）、禁止强推（`non_fast_forward`）、强制线性历史（`required_linear_history`）、强制 PR Squash 合并与解决全部评审评论线程，并要求必选状态检查 `docs`（GitHub Actions `integration_id: 15368`）通过 |

---

## 3. 质量门禁执行记录（含诚实 `not-run` 声明）

本阶段严格区分“已在当前环境真实运行通过的门禁”与“因 M1 纯架构契约阶段尚未建立业务代码工程而标记为 `not-run` 的构建门禁”：

| 检查项 | 执行命令 / 手段 | 状态 | 结果说明 |
| :--- | :--- | :---: | :--- |
| **文档治理与注册表门禁** | `python -B scripts/check_docs.py` | **`passed`** | 全仓受控文档注册表双向匹配、Markdown 相对路径链接与锚点 100% 存活、`traceability.json` 47 项需求映射校验通过 |
| **路径脱敏与命名纯净扫描** | 正则扫描盘符绝对路径与无关外部项目名 | **`passed`** | 零本机盘符路径泄漏，零兄弟仓库名称串味，零过时框架版本残留 |
| **空白字符与编码规范检查** | `git diff main..HEAD --check` & LF 核验 | **`passed`** | 全仓受管文本文件 100% UTF-8 LF，无尾随空格冲突 |
| **本机 `.NET 10` 工具链就绪核验** | `dotnet --list-sdks` & `where.exe signtool` | **`passed`** | 实测已安装 `.NET 10 SDK 10.0.404`（含 `.NET 10.0.15` Desktop Runtime）及 Windows SDK `10.0.26100.0` `signtool.exe`（`ACT-001` 已就绪） |
| **远端规则集与安全开关回读** | `gh api repos/chialecode/spawn/...` | **`passed`** | 仓库设置、Actions 策略、安全扫描与 `main-protection` 规则集全部激活生效 |
| **Git 拓扑与单提交 DCO 检查** | `git log main..feat/m1-foundation` | **`passed`** | 不可变初始提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e` 保持原状，阶段分支收敛为单一带 `Signed-off-by` 提交 |
| **`.NET 10` 解决方案编译检查** | `dotnet build Spawn.sln` | **`not-run`** | **未执行客观原因**：M1 阶段范围定义为治理与架构契约基线，尚未创建 `Spawn.sln` 业务代码工程（归属 `M2` 初始化并执行） |
| **`.NET 10` 单元与 `PEReader` 门禁测试** | `dotnet test Spawn.sln` | **`not-run`** | 同上，归属 `M2` 建立 `Spawn.Architecture.GateTests` 与领域测试工程后强制执行 |

---

## 4. 保守远端边界与后续交接指引

1. 遵照已锁定决策 `D-15`，本阶段全套交付物当前仅提交在本地 `feat/m1-foundation` 分支，未执行 `git push origin feat/m1-foundation` 或创建 Pull Request。
2. 请仓库所有者查阅根目录 [USER-ACTIONS.md](../../USER-ACTIONS.md)：
   - **远端发布授权（`ACT-002`）**：审阅确认 M1 架构与治理基线后，可授权推送 `feat/m1-foundation`、创建 PR 并通过 Squash Merge 合入 `main`；
   - **进入 M2 首发开发**：因 `ACT-001`（`.NET 10 SDK 10.0.404`）已就绪，M1 合入 `main` 后可立即启动 `M2`（启动器 + Steamrip/GOG 单机成就自动捕获与实时弹窗管理闭环），可选 `ACT-006`（Steam Web API Key 与单机测试样本）不阻塞开发推进。
