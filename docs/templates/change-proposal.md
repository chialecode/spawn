# 跨模块变更提案模板

本模板用于规划涉及多个业务域（S.P.A.W.N.）、跨进程 IPC 契约或 `spawn.db` / `SPAWN_Vault` 存储结构演进的中大型功能变更。提案获采纳落地后，必须将最终有效契约合入 `docs/design/*` 与 `docs/modules/*` 职责正本，避免产生双份永久文档。

| 提案维度 | 必填技术说明 |
| :--- | :--- |
| **变更目标与范围** | 解决的玩家场景、关联 `REQ-*` 需求编号、目标里程碑，以及明确不在本次变更中处理的边界 |
| **反作弊与进程域归属** | 明确新代码落在 `Spawn.Host`、`Spawn.OverlayHost` 还是特定 `Spawn.*Worker`；对 `PEReader` IAT 导入表与 `AntiCheatTier 0..3` 联锁矩阵的影响 |
| **开源查证与许可证** | 拟复用的开源仓库/NuGet 包、版本、活跃度证据及与 `Apache-2.0` 的许可证兼容方式（进程内链接 vs 进程外 CLI 调度） |
| **数据、IPC 与文件契约** | `Spawn.Contracts` Protobuf/DTO 增改、`spawn.db` 向前迁移脚本、硬链接/NTFS Junction/WAL 原子恢复策略 |
| **异常降级与回滚保护** | 权限不足、目标文件被独占锁定、外部工具缺失或中途断电时的自愈回滚与诚实 UI 提示 |
| **验证用例与待决事项** | 自动化单元测试、架构门禁断言与可选实机检查；工程遗留引用 `open-questions.md`，用户配置/授权引用 `USER-ACTIONS.md` |
