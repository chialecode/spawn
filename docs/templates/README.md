# SPAWN 文档模板索引

按变更复杂度与架构影响范围按需选用以下标准模板，无需为常规局部小修复重复创建全套独立文件。新建正式文档后必须同步登记至 [document-registry.json](../governance/document-registry.json) 并运行 `python scripts/check_docs.py` 验证通过。

| 标准模板 | 适用触发场景 | 目标落盘位置 |
| :--- | :--- | :--- |
| [Agent A/B 与阶段交接模板](agent-handoff.md) | 制定整阶段实施计划（A→B）、完成连续编码自检交付（B→A）、整批返工（A→B）及审查收敛后登记远端授权请求 | 复用固定入口 `docs/delivery/stage-plan.md`、`docs/delivery/stage-report.md` 与根 [USER-ACTIONS.md](../../USER-ACTIONS.md) |
| [架构决策记录（ADR）模板](decision-record.md) | 满足 [文档治理政策](../governance/documentation-policy.md) ADR 触发条件的长期技术栈、进程隔离、存储格式或开源基座裁决 | `docs/decisions/NNNN-<kebab-case-slug>.md`（同步更新 `confirmed-decisions.md`） |
| [跨模块变更提案模板](change-proposal.md) | 涉及两个及以上核心域（S.P.A.W.N.）、修改 `spawn.db` Schema 或调整 Named Pipe IPC 契约的功能设计 | 合入对应 `docs/design/` 或 `docs/modules/` 职责正本，不保留冗余第二契约 |
| [业务模块规格模板](module-spec.md) | 新增业务子模块、新增外部生态适配器家族或重构现有模块的进程/权限边界 | `docs/modules/<module-name>.md` |
| [阶段与专项验证报告模板](validation-report.md) | 里程碑整阶段交付验证、高刷 Overlay 帧率实测、FastCDC/WAL 断电恢复演练或 `PEReader` IAT 审计报告 | 当前阶段写入 `docs/delivery/stage-report.md`；专项或历史留存归档为 `reference` 状态证据 |

> [!NOTE]
> 使用模板时请填写真实命令、版本、退出码与源码指纹，删去当前阶段不适用的空占位条目并注明原因；严禁将示例占位符保留为 `passed`。
