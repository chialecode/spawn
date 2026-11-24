# SPAWN 架构决策记录（ADR）与工程台账索引

本目录维护 SPAWN 项目的全部顶层架构决策记录（Architecture Decision Records, ADR）、已锁定决策总表及待后续里程碑实机收敛的工程事项台账。

## 1. 台账总表

| 文档 | 定位与职责 |
| :--- | :--- |
| [confirmed-decisions.md](confirmed-decisions.md) | 汇总 `D-01` ~ `D-34` 全部已拍板锁定的产品、技术栈、里程碑范围、安全隔离与协作治理决策，以及 `A-01` ~ `A-06` 可逆工程默认项。任何偏离已确认决策的改动必须先提交新 ADR 审阅。 |
| [open-questions.md](open-questions.md) | 上游事项台账：`OSS-*` 外部组件适配与合规核验；原 `LOOP-*` 与用户事项已转为 GitHub issue，台账列出去向。 |

> **协作分工约束**：本目录仅记录**系统架构与工程技术决策**；凡需仓库所有者手工执行的本机工具确认、证书信任或远端发布审批动作，统一建为带 `needs-user` 标签的 [GitHub issue](https://github.com/chialecode/spawn/issues?q=is%3Aissue+is%3Aopen+label%3Aneeds-user)，不在本目录重复建账。

## 2. 架构决策记录（ADR）清单

| 编号 | 标题 | 状态 | 核心决策摘要 |
| :--- | :--- | :--- | :--- |
| [ADR-0001](0001-document-governance-and-collaboration.md) | 文档即契约治理、双 Agent / Swarm 协作与单阶段单提交规则 | Accepted（协作、远端授权与提交规则部分被 [D-34](confirmed-decisions.md#d-34) 替代） | 确立不可变空 `README.md` 第一提交基线、阶段分支单提交 DCO `amend` 纪律、`document-registry.json` + `check_docs.py` 机械门禁及保守远端授权边界。 |
| [ADR-0002](0002-pure-native-wpf-and-nodify-ui-stack.md) | 纯原生 `.NET 10 (LTS)` + WPF（`WPF-UI` + `Nodify`）桌面与蓝图技术栈 | Accepted | 否决 Electron/Tauri/`WebView2` 混合套壳与 WinUI 3 多窗口缺陷，锁定纯原生 C# 14 + `.NET 10 (LTS)` + `lepoco/wpfui` + `miroiu/nodify` 实现毫秒级响应与 60FPS 千节点蓝图。 |
| [ADR-0003](0003-anti-cheat-guard-and-process-isolation.md) | Clean Host 多进程完整性物理隔离与四级反作弊联锁护栏 | Accepted | 将敏感能力剥离至按需启停的独立 Worker 进程，主宿主维持 Medium IL 纯净导入表（CI `PEReader` 门禁），建立 `Tier 0~3` 联锁、Explorer 血缘脱钩与 15 秒 `SGuard64` 退出冷却状态机。 |
| [ADR-0004](0004-cas-fastcdc-storage-and-mod-junction-topology.md) | `FastCDC` 内容寻址存档库、`32MB SPPK` 云同步与 NTFS Junction Mod 拓扑 | Accepted | 采用确定性 Gear-Hash `FastCDC` + `BLAKE3` + `Zstd` 构建存档时间机器与 `32MB SPPK` 自建云同步；采用 CAS 硬链接 + `MutableCopy` `XxHash3` 回收 + 顶层 NTFS Junction WAL 实现 `<100ms` 多维 Profile 原子切换。 |
| [ADR-0005](0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) | 二次元外部生态顶层编排、零报毒外部修改器管家与 `ZBID_UIACCESS` 高刷图层 | Accepted | 坚持顶层元调度定位（内嵌 `Maa.Framework` C# + `_ctypes.pyd` 桥接 `MaaFgo/BBChannel` + 统管 CLI 生态）；一期采用 `A0+A` 零注入静音管理外部 FLiNG 并预留二期 `.CT` 接口；采用 `DirectComposition` + `SkiaSharp` + `ZBID_UIACCESS` 实现 144Hz 零侵入 Overlay。（修改器静音参数与二期后端表述由 ADR-0006 细化修订） |
| [ADR-0006](0006-m3-external-trainer-manager-elevation-and-ipc.md) | `M3` 外部修改器安全管家：分级按需提权、倒置跨 IL 管道与洁净室依赖边界 | Accepted | 废止指向按用户安装目录的免 UAC 计划任务（LPE 风险），改为无管理员清单直启 Medium IL / 每会话 1 次 `runas` UAC；Medium IL `Spawn.Host` 作单实例 `NamedPipeJsonRpc` 服务端、Worker 作客户端并双向互验；`M3` Worker 零跨进程内存 API；全仓移除 `Reloaded.Memory` 家族与捆绑 `unRAR` 限制的解压库，洁净室实现 FLiNG 发现/下载。 |
| [ADR-0007](0007-weekly-release-and-signed-in-app-update.md) | 正式版每周发布流水线、签名应用内更新与开发版逐提交重装 | Accepted | 正式版自包含 + Actions 每周有合入才发、仓库变量控制发布；应用内更新改用 BCL ECDSA P-256 验签并提前至 `M3`；开发版永不自查、每次本地提交后重装 |

## 3. 新增或变更 ADR 流程

1. 当出现推翻 [confirmed-decisions.md](confirmed-decisions.md) 既有结论或将 [open-questions.md](open-questions.md) 中事项正式收敛为长期契约的需求时，按四位递增序号新建 `000X-<kebab-case-title>.md`。
2. 新 ADR 必须包含：**背景与问题陈述**、**备选方案对比**、**最终决策**、**架构与安全影响**及**验证方式**。
3. 同步更新本索引、[confirmed-decisions.md](confirmed-decisions.md) 与 [document-registry.json](../governance/document-registry.json)，并通过 `python scripts/check_docs.py` 门禁检查。
