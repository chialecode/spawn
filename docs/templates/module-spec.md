# 模块规格书标准模板

新增或重构 SPAWN 业务模块时按本结构编写正式规格书（存放于 `docs/modules/`），直接向编码阶段提供无歧义的工程契约，无需堆砌与实现无关的背景套话。

---

## 1. 模块定位与进程隔离边界

- **所属核心域与子工程**：如 `src/Spawn.Modules.<Name>` 及协同的 `src/Spawn.<Name>Worker`。
- **运行完整性级别（IL）**：`Medium IL`（常驻纯净宿主）/ `Medium + UIAccess IL`（置顶覆盖层）/ `High IL`（按需即焚隔离 Worker）。
- **关联需求与决策**：列出覆盖的 `REQ-*` 编号与 `ADR-*` / `D-*` 依据。
- **严禁触碰的旁路红线**：明确本模块禁止直接引用的高敏 API、禁止跨层访问的进程或目录。

---

## 2. 核心接口契约与状态机

定义关键应用服务接口（C# `interface` / `record` 签名草图）、跨进程 Protobuf RPC 消息，以及使用 Mermaid `stateDiagram-v2` 或 `sequenceDiagram` 表达的核心生命周期状态机（含超时取消与 Worker 代次校验）。

---

## 3. 数据存储与文件系统布局

列出本模块读写的 `spawn.db` 数据表（统一 `snake_case`）、`<应用数据根>\vault\`（及分卷 `<目标盘根>\SPAWN_Vault\`）子目录、外部配置文件路径解析规则（严守 `SHGetKnownFolderPath` 等可移植变量，禁用硬编码个人路径）及 WAL 日志格式。

---

## 4. 关键算法、协议细节与反作弊联锁行为

1. **核心算法/协议规约**：精确到字节序、正则匹配、分块参数、退出码映射或命令行调用矩阵。
2. **四级 `Anti-Cheat Guard` 联锁表现**：逐级说明本模块在 `Tier 0`、`Tier 1`、`Tier 2`、`Tier 3` 及 **15 秒退出冷却期** 内的开放、降级或物理熔断锁死行为。
3. **异常自愈与防丢档避坑**：断电重启恢复、文件锁冲突退避、非递归卸载重解析点或二进制 SHA-256 备份还原机制。

---

## 5. 门禁测试与验收准则

区分确定性单元测试（`Spawn.Core.Tests` / `Spawn.Achievement.Tests` / `Spawn.Integration.Tests` 合成夹具）、`Spawn.Architecture.GateTests` IAT 静态断言与需实机硬件/游戏配合的人工体验检查（关联 `USER-ACTIONS.md` `H-*`）。
