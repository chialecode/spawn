# SPAWN M1 阶段实施计划（Stage Plan — M1 Foundation）

> **阶段代号**：`M1`（Governance, Security Baseline & Architecture Contracts）
> **计划版本**：`1.0`
> **工作分支**：`feat/m1-foundation`（基线锚点 `main` @ `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`）
> **关联文档**：[项目交付总状态](status.md) · [M1 阶段执行报告](stage-report.md) · [M1 阶段验收用例集](m1-stage-cases.json) · [里程碑路线图](../engineering/delivery-plan.md)

---

## 1. 阶段目标与边界定义

`M1` 阶段的核心使命是在编写任何 `.NET 10` 业务代码前，把 SPAWN 从前期深度技术调研沉淀为一套**结构自洽、安全边界刚性、双向机器可追踪的工程治理与架构契约基线**：

- **阶段范围内（In-Scope）**：
  1. 初始化本地与远端不可变空 `README.md` 第一提交锚点，通过 GitHub API 加固 `chialecode/spawn` 仓库设置、安全扫描开关、Actions SHA 锁定策略与 `main-protection` 分支保护规则集；
  2. 建立根级开源治理套件、AI Agent 与多智能体 Swarm 协作守则、文档注册表（`document-registry.json`）及 CI/本地同源的 `scripts/check_docs.py` 质量门禁；
  3. 冻结 47 项编号需求（`REQ-*` / `NFR-*`）、19 项已确认决策（`D-01` ~ `D-19`，含升级锁定 `.NET 10 LTS` 及 `M2` 启动器+单机成就闭环先行）、5 篇核心 ADR、4 篇系统顶层设计正本与 5 篇领域模块规格正本；
  4. 归档并清洗 5 篇前期深度调研报告（清退临时总纲冗余入口），输出 38+ 开源合规矩阵、本机开发环境核验全表（确认 `SDK 10.0.404` 就绪）、M1~M6 里程碑路线图与 `traceability.json` 追踪矩阵。
- **阶段范围外（Out-of-Scope，移交 M2+）**：
  - 创建 `Spawn.sln` / `.csproj` 编译工程或编写 C# 业务代码（归属 `M2`）；
  - 未经用户审阅授权擅自向远端推送 `feat/m1-foundation` 分支或创建 PR。

---

## 2. 工作包拆解（Work Packages）

| 工作包编号 | 工作包主题 | 核心产出文件范围 | 质量控制焦点 |
| :--- | :--- | :--- | :--- |
| `M1-W01` | **Git 初始锚点、远端安全加固与基础设施配置** | `main` 初始提交、`.gitignore`、`.gitattributes`、`.editorconfig`、`LICENSE`、`.github/repository-settings.json`、`.github/rulesets/main.json`、`.github/actions-policy.json`、`.github/dependabot.yml`、`.github/PULL_REQUEST_TEMPLATE.md`、`.github/workflows/docs.yml` | 40 位初始提交不可变；远端规则集与安全开关回读零偏差；Actions 全量 40 位 Commit SHA 固定 |
| `M1-W02` | **根级治理套件、开发规范与自动化文档门禁** | `README.md`、`AGENTS.md`、`CONTRIBUTING.md`、`REVIEW.md`、`SECURITY.md`、`DESIGN.md`、`USER-ACTIONS.md`、`docs/README.md`、`docs/project-baseline.md`、`docs/governance/*`、`docs/product-rules/*`、`docs/design-rules/*`、`docs/dev-rules/*`、`scripts/check_docs.py` | 全仓受控文件 100% 纳入注册表；机械拦截死链、盘符绝对路径与无关外部仓库名 |
| `M1-W03` | **产品需求正本、ADR 体系与系统顶层设计正本** | `docs/requirements/product-requirements.md`、`docs/decisions/README.md`、`confirmed-decisions.md`、`open-questions.md`、`0001..0005-*.md`、`docs/design/system-architecture.md`、`data-model.md`、`ipc-and-security-contracts.md`、`interaction-and-workflows.md` | `.NET 10 LTS` 六进程隔离拓扑、SQLite `snake_case` DDL、`FastCDC`/`SPPK` 格式、SDDL 命名管道、15s 反作弊冷却无歧义冻结 |
| `M1-W04` | **五大领域模块规格正本与前期调研脱敏归档** | `docs/modules/README.md`、`spawn-launcher.md`、`nexus-mods-and-utilities.md`、`progress-saves-and-trainer.md`、`achievement-and-overlay.md`、`workflow-and-blueprint.md`、`docs/research/README.md`、`01..05-*.md`（删除临时总纲） | 吸收原总纲全部工程细节至正式规格并与 `M2` 首发联动边界精确对齐 |
| `M1-W05` | **选型环境全表、交付路线图、追踪矩阵与阶段验收** | `docs/engineering/technology-selection.md`、`delivery-plan.md`、`docs/delivery/status.md`、`stage-plan.md`、`stage-report.md`、`m1-stage-cases.json`、`traceability.json` | 47 项需求按新 `M2`~`M6` 路线 100% 闭环映射；核验本机 `.NET 10 SDK 10.0.404` 就绪；收敛为单阶段单提交 DCO |

---

## 3. 阶段准出判据与验证映射

| 用例编号 | 类型 | 对应工作包 | 验证内容 | 准出标准 |
| :--- | :---: | :---: | :--- | :--- |
| `M1-A-01` | 自动化 | `M1-W01` | 不可变第一提交 SHA、本地分支拓扑与远端 GitHub 规则集/安全策略回读核验 | `origin/main` 恒等于 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`，`main-protection` 处于 `active` |
| `M1-A-02` | 自动化 | `M1-W02` | 运行 `python -B scripts/check_docs.py` 全仓文档治理、注册表覆盖与相对链接校验 | 返回退出码 `0`，零死链、零未登记孤儿文档、零引用式链接 |
| `M1-A-03` | 自动化 | `M1-W02`, `M1-W04` | 仓库洁净度红线扫描（严禁盘符绝对路径与无关外部项目名称残留） | 全仓扫描匹配数为 `0`，临时研究总纲已清退归档 |
| `M1-A-04` | 自动化 | `M1-W03`, `M1-W05` | 需求（`REQ-*`/`NFR-*`）、决策（`D-01`~`D-19`）、设计、模块与验收（`AC-01`~`AC-24`）双向追踪校验 | 47 项需求在 `traceability.json` 中 100% 闭合，JSON Schema 合法 |
| `M1-A-05` | 自动化 | `M1-W01`, `M1-W05` | Git 工作树洁净度、`git diff --check` 空白/行尾校验与单提交 DCO `Signed-off-by` 校验 | `feat/m1-foundation` 领先 `main` 恰好 1 个含合法 DCO 的提交，工作区干净 |
| `M1-A-06` | 自动化 | `M1-W01` | `.github/workflows/docs.yml` Actions SHA 固定与最小只读权限静态核验 | `permissions: contents: read`，Action 引用使用完整 40 位 Commit SHA |
| `M1-M-01` | 人工门禁 | `M1-W05` | 仓库所有者审阅 M1 成果并按 `ACT-002` 授权推送分支与合并 PR #1 | 已在 [USER-ACTIONS.md](../../USER-ACTIONS.md) 清晰登记指引（`ACT-001` `.NET 10 SDK` 已就绪） |
