# SPAWN 全局文档总导航地图

基线建立日期：2026-11-15（`M1` 基础建设基线）。全仓文档遵循单一事实正本治理原则；设计规格与契约处于 `active` 仅代表开发约束生效，功能是否已落地交付以 [当前执行状态](delivery/status.md) 与阶段验证报告为准。

---

## 最短接手阅读路径

首次参与开发或开启新会话时，建议按以下 5 步快速建立完整上下文（无需一次性通读全仓，其余专题按变更触发查阅）：

1. [AGENTS.md](../AGENTS.md)（工作红线、按变更必读表、Agent A/B + Swarm 多智能体协同协议）
2. [项目关键事实与基线](project-baseline.md)（S.P.A.W.N. 定位、纯原生技术栈拍板、明确不做边界与恢复入口）
3. [当前执行状态](delivery/status.md) 与 [当前阶段计划](delivery/stage-plan.md)（当前里程碑进度、父提交锚点与下一步）
4. [集中工程事项台账](decisions/open-questions.md) 与 [用户待办](../USER-ACTIONS.md)（待锁定工程问题 vs 用户环境/授权待办）
5. 按本次任务涉及范围查阅下方对应**系统设计正本**或**五大领域模块规格书**。

---

## 全仓文档分层索引表

| 分层域 | 核心正本入口 | 权威管辖范围 |
| :--- | :--- | :--- |
| **根级入口** | [README](../README.md) · [AGENTS](../AGENTS.md) · [CONTRIBUTING](../CONTRIBUTING.md) · [REVIEW](../REVIEW.md) · [DESIGN](../DESIGN.md) · [SECURITY](../SECURITY.md) · [USER-ACTIONS](../USER-ACTIONS.md) | 项目门面、最高工作指令、环境要求、审查一票否决门禁、安全隔离声明与全仓唯一用户待办 |
| **文档治理** | [文档治理政策](governance/documentation-policy.md) · [文档注册表](governance/document-registry.json) | 单一事实正本、ADR 触发条件、阶段开始四分类清理与去阶段标签规则、全仓文档登记 |
| **产品与需求** | [核心产品原则](product-rules/core-product-principles.md) · [产品需求正本](requirements/product-requirements.md) | 五大长期产品不变量、`REQ-SPW/PRG/ACH/WFL/NEX/SEC` 结构化需求与验收判据 |
| **架构决策** | [决策总览](decisions/README.md) · [已确认决策](decisions/confirmed-decisions.md) · [工程事项台账](decisions/open-questions.md)<br/>[ADR-0001](decisions/0001-document-governance-and-collaboration.md) · [ADR-0002](decisions/0002-pure-native-wpf-and-nodify-ui-stack.md) · [ADR-0003](decisions/0003-anti-cheat-guard-and-process-isolation.md) · [ADR-0004](decisions/0004-cas-fastcdc-storage-and-mod-junction-topology.md) · [ADR-0005](decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) | 已拍板决策台账（`D-01`~`D-18`）、纯原生 WPF 裁决、反作弊物理隔离、CAS/FastCDC/Junction 存储、外生编排与 `ZBID_UIACCESS` 长期取舍 |
| **系统顶层设计** | [全景系统架构](design/system-architecture.md) · [数据模型与存储契约](design/data-model.md) · [IPC 与反作弊安全契约](design/ipc-and-security-contracts.md) · [交互与工作流设计](design/interaction-and-workflows.md) | 多进程拓扑与四级 `AntiCheatTier` 联锁、`spawn.db` SQLite WAL + `SPAWN_Vault` + `SPPK` 格式、Named Pipe SDDL + `PEReader` IAT 门禁、跨模块端到端交互流 |
| **五大业务模块规格** | [模块总览](modules/README.md) · [Spawn 启动管线](modules/spawn-launcher.md) · [Nexus Mod 与工具箱](modules/nexus-mods-and-utilities.md) · [Progress 存档与修改器](modules/progress-saves-and-trainer.md) · [Achievement 成就与 Overlay](modules/achievement-and-overlay.md) · [Workflow 代肝与蓝图](modules/workflow-and-blueprint.md) | 直接指导 `M2`~`M6` 编码实现的接口契约、状态机、解析规则表、15 大避坑措施与测试判据 |
| **视觉与工程规则** | [UI 极客设计规范](design-rules/DESIGN.md) · [开发规则索引](dev-rules/README.md)（含 [仓库地图](dev-rules/repo-map.md)、[开源准则](dev-rules/architecture-and-open-source.md)、[数据安全](dev-rules/data-and-security.md)、[开发协作](dev-rules/development-workflow.md)、[沟通协议](dev-rules/communication-protocol.md)、[Git 工作流](dev-rules/git-workflow.md)、[本地资料](dev-rules/local-files-and-references.md)、[质量门禁](dev-rules/quality-gates.md)） | Fluent+Mica 暗色视觉、`Nodify` 引脚色板、144Hz Overlay、`Spawn.sln` 依赖方向、A/B+Swarm 并行开发、单阶段 DCO 提交与两级门禁 |
| **选型与里程碑交付** | [技术选型与环境清单](engineering/technology-selection.md) · [M1~M6 交付路线图](engineering/delivery-plan.md) · [当前状态](delivery/status.md) · [阶段计划](delivery/stage-plan.md) · [阶段报告](delivery/stage-report.md) · [需求追踪矩阵](delivery/traceability.json) | 35+ 活跃开源库与许可证核验、NuGet/Win32 矩阵、本机开发环境清单、里程碑验收门槛与双向追溯 |
| **技术调研与模板** | [前期调研档案索引](research/README.md)（`01`~`05` 源码级调研） · [文档模板索引](templates/README.md) | 选型横评推导过程、第三方工具协议逆向证据（`reference` 状态）及标准工程模板 |

---

## 维护与门禁约定

- 任何新增、重命名或退役文档必须在同一变更中更新 [document-registry.json](governance/document-registry.json)；涉及需求或验收编号调整时同步更新 [traceability.json](delivery/traceability.json)。
- 每次提交前在仓库根目录运行 `python scripts/check_docs.py` 与 `git diff --check`，确保注册表完整性、相对链接与锚点有效性以及公开卫生门禁 100% 通过。
