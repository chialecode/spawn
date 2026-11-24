# ADR-0001：文档即契约治理、多智能体并行协作与单阶段单提交规则

- **状态**：Accepted（2026-11-24 部分被 [D-34](confirmed-decisions.md#d-34) 替代：§3.3 的协作分工、保守远端授权与单阶段单提交改为 issue 驱动的小 PR 流程，见[开发流程](../dev-rules/development-workflow.md)；文档即契约、注册表与仓库洁净规则仍有效）
- **日期**：2026-11-15
- **决策编号**：`D-13`、`D-14`、`D-15`、`D-16`、`D-17`、`D-18`
- **关联文档**：[文档治理策略](../governance/documentation-policy.md) · [Git 工作流规范](../dev-rules/git-workflow.md) · [质量门禁规范](../dev-rules/quality-gates.md) · [USER-ACTIONS.md](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md)

---

## 1. 背景与问题陈述

SPAWN 是一个横跨进程调度、存储去重、离线协议解析、图形合成 Overlay、外部自动化编排与可视化蓝图虚拟机的多进程复杂桌面系统（规划周期 M1 ~ M6）。若缺乏严格的工程治理与协作边界，极易在演进过程中出现以下系统性失控：

1. **架构漂移与隐式妥协**：代码实现与架构文档脱节，甚至在局部便利驱动下破坏 Clean Host 反作弊物理隔离红线。
2. **多智能体（Agent / Swarm）并发踩踏**：多个专业 Agent 同时修改同一目录或共享接口，导致内容覆盖、相对链接断裂或责任不清。
3. **Git 提交历史碎片化与不可回滚**：频繁产生“修拼写”“补漏文件”等噪音提交，难以按里程碑粒度精确审计与回退。
4. **环境与路径污染**：将个人本机绝对路径、无关外部项目词汇或测试副产物混入开源仓库，破坏跨机器可复现性与真实玩家数据安全。

## 2. 备选方案评估

| 方案 | 描述 | 评估结论 |
| :--- | :--- | :--- |
| **方案 A：敏捷边写边改，事后补文档** | 直接进入 `.NET` 代码脚手架编写，架构与需求随写随调，常规多提交 PR 合并。 | **否决**。跨 6 个隔离进程域的 IPC、数据库 DDL 与反作弊导入表红线一旦返工成本极高，且无法支撑多 Worker 零冲突并行作业。 |
| **方案 B：M1 先行冻结全套架构与治理契约 + 机械门禁 + 单阶段单提交 DCO** | 在编写业务代码前，先于 M1 完成 GitHub 规则集加固、需求-设计-模块规格-追踪矩阵闭环，配套 `check_docs.py` 自动化校验与严格单阶段单提交演进纪律。 | **采纳**。前期以极低成本将全部边界契约显式化、机器可检化，后续 M2 ~ M6 编码具备确定性验收标尺。 |

## 3. 最终决策

### 3.1 不可变第一提交与单阶段单提交 DCO 规则
1. **不可变初始锚点**：仓库 `main` 分支永久保留第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（仅含 0 字节空 `README.md` 与标准 DCO `Signed-off-by`，提交信息 `chore: initialize repository with empty README`），任何阶段严禁重写或强推覆盖该初始提交。
2. **阶段分支单提交收敛**：每个里程碑在独立分支（如 `feat/m1-foundation`、`feat/m2-clean-host-and-launcher`）推进；同一阶段内的增量修正与审查闭环统一使用 `git commit --amend -s` 合并至当前阶段的唯一提交，保持“一阶段一分支、一阶段一原子提交”。
3. **受保护主干合并**：远端 `main` 启用 `main-protection` 规则集（禁止删除、禁止强推、强制线性历史、仅允许 Squash Merge、强制解决全部评审会话并通过必选状态检查；2026-11-24 起唯一的必选检查是 `repository-quality`，见 [#12](https://github.com/chialecode/spawn/issues/12)）。

### 3.2 文档即契约与机械化注册表门禁
1. 全仓受控文档统一登记在 [document-registry.json](../governance/document-registry.json)，明确主责角色、更新时机与阶段分类。
2. 需求编号（`REQ-*` / `NFR-*`）、设计正本、模块规格与验收判据（`AC-*`）在 [traceability.json](../delivery/traceability.json) 中建立双向机器可读映射。
3. 本地与 GitHub Actions（`.github/workflows/ci.yml` 的 `repository-quality`）统一运行 `python scripts/check_docs.py`，强制拦截死链、未登记孤儿文档、引用式链接、任何盘符绝对路径（`[A-Za-z]:[\\/]`）以及任何外部无关项目命名残留。

### 3.3 多智能体独占分片与保守远端授权
1. 并行 Swarm / 双 Agent 协作时严格按目录分片授权写操作，共享契约（如 `Spawn.Contracts`、数据库 DDL、注册表）先对齐接口再落盘，并由无写偏见的独立角色执行全量门禁复核。
2. 严守保守远端边界：除初始化锚点外，任何推送远端分支、开启/合并 PR 与创建 Release 动作均须等待仓库所有者审阅阶段报告并明确授权；所有需人工配合事项统一汇总至 [USER-ACTIONS.md](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md)。
3. 严守开发隔离与静默测试纪律：源码开发固定指向 `%LOCALAPPDATA%\spawn-dev`（与正式版 `%LOCALAPPDATA%\spawn` 彻底物理隔离），一切自动化测试必须在后台无界面静默完成。

## 4. 影响与后果

- **正面收益**：架构边界在 M1 一次性清晰闭环，CI 从第一天起即可自动捕获链接腐化、路径泄漏与 DCO 缺失；Git 主干演进历史清晰如教科书。
- **工程约束**：新增或重命名任何文档文件时必须同步维护 `document-registry.json`；任何跨阶段需求变更必须同步更新 `product-requirements.md` 与 `traceability.json`。

## 5. 验证判据

- `python scripts/check_docs.py` 返回退出码 `0`。
- `git log main` 验证初始提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e` 完好无损，`feat/m1-foundation` 在 `main` 之上仅包含 1 个带合法 `Signed-off-by` 的阶段提交。
