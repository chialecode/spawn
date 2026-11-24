# 开发规则索引（Dev Rules）

本目录定义 SPAWN 的工程结构、开源选型、数据与反作弊安全、issue 驱动的开发流程、Git 与远端规则、本地参考隔离与自动化质量门禁。产品长期行为见 [核心产品原则](../product-rules/core-product-principles.md)，界面视觉见 [UI 设计规范](../design-rules/DESIGN.md)，系统契约与五大业务域细节见 [文档总导航](../README.md)。

| 规则正本 | 触发阅读时机 | 核心约束内容 |
| :--- | :--- | :--- |
| [仓库地图](repo-map.md) | 初始化或修改 `Spawn.sln`、新增子工程、调整项目引用或目录布局 | `Spawn.Contracts` → `Spawn.Core` → `Spawn.Modules.*` → `Host / OverlayHost / *Worker` 单向依赖硬约束与目录所有权 |
| [开源与架构规则](architecture-and-open-source.md) | 评估技术方案、引入新 NuGet 包、对接外部工具或决定自研边界 | 活跃开源先行核查、Apache-2.0 许可证兼容性矩阵、外生生态编排与薄适配原则 |
| [数据与安全规则](data-and-security.md) | 操作 `spawn.db`、`SPAWN_Vault`、硬链接/Junction、DPAPI 凭据、云同步或提权子进程 | `spawn` vs `spawn-dev` 数据隔离、SQLite WAL 禁放网盘、非递归卸载 Junction、自签证书即毁私钥与脱敏日志 |
| [开发流程](development-workflow.md) | 领取 issue、提 PR、审查与合并、找用户、多个 Agent 并行、每周维护 | issue → 小 PR → CI 与独立审查 → 合并、高风险清单、开发版自动安装、教训循环、NuGet 7 天冷却盘点 |
| [Git 工作流规范](git-workflow.md) | 创建分支、提交、校验 DCO、push 与 PR、改仓库设置 | 不可变第一提交、`main` 保护规则集、远端授权表、Actions 白名单与正式版每周发布流水线 |
| [本地文件与参考资料](local-files-and-references.md) | 查阅本机外部参考、存放测试报告/夹具、处理未跟踪文件或清理构建缓存 | `.local/` 忽略工作区、`SPAWN_REFERENCE_DIR` 与上级 `reference/spawn/` 只读索引规则、公开内容卫生边界 |
| [质量门禁规范](quality-gates.md) | 执行文档检查、编写单元测试、`PEReader` 架构门禁或阶段交付自检 | `check_docs.py` 门禁、两级门禁（`quick_gate.py` vs 发布前整套门禁 + IAT 审计）、测试静音无弹窗低优先级约束 |

所有工程实现与文档修改均须遵守本目录规则；局部目录若未来确需特殊补充约束，须经 [文档治理政策](../governance/documentation-policy.md) 登记且不得削弱上述全局安全底线。
