# 文档治理政策与单一正本规范

生效日期：2026-11-15；维护责任：维护者。本政策管辖仓库内全部自有 Markdown 与 JSON 治理/交付文档，确保架构契约清晰、跨阶段演进无冗余腐化、自动化门禁可机械校验。

---

## 1. 单一事实正本（Single Source of Truth）分层

同一工程事实在全仓有且仅有一个权威正本，其他文档通过相对路径链接引用，严禁多处复制粘贴导致后续失步：

| 治理层级 | 唯一事实正本 | 管辖事实范围 |
| :--- | :--- | :--- |
| **工作指令入口** | [AGENTS.md](../../AGENTS.md)（`CLAUDE.md` 仅单行 `@AGENTS.md` 引用） | 启动检查、工作流程要点、必读触发、安全与远端边界 |
| **需求与用户事项** | [GitHub Issues](https://github.com/chialecode/spawn/issues)（用户事项带 `needs-user` 标签） | 需求、缺陷、后续迭代，以及用户决定、人工实机检查、外部配置与远端操作授权 |
| **治理与注册表** | 本文档、[document-registry.json](document-registry.json) | 文档元数据状态、生命周期、阶段清理与机械门禁范围 |
| **基线与架构决策** | [项目基线](../project-baseline.md)、[已确认决策](../decisions/confirmed-decisions.md)、`docs/decisions/000*.md` | 核心技术选型裁决、用户拍板记录、长期架构取舍及替代关系 |
| **上游事项台账** | [open-questions.md](../decisions/open-questions.md) | 上游开源组件适配与合规事项（`OSS-*`），以及原 `LOOP-*` 与用户事项的去向 |
| **产品与需求** | [核心产品原则](../product-rules/core-product-principles.md)、[产品需求正本](../requirements/product-requirements.md) | 五大产品不变量、`REQ-*` 功能/非功能需求定义与优先级 |
| **系统与模块设计** | `docs/design/*.md`、`docs/modules/*.md` | 多进程架构、SQLite/Vault 数据契约、IPC/反作弊契约及五大模块实现规格 |
| **工程与视觉规范** | `docs/dev-rules/*.md`、`docs/design-rules/DESIGN.md`、[REVIEW.md](../../REVIEW.md) | `Spawn.sln` 依赖边界、数据安全、协作协议、Git/门禁规范、WPF/Nodify/Overlay 视觉标准、审查口径与开发教训（[LESSONS.md](../../LESSONS.md)） |
| **选型、交付与追溯** | `docs/engineering/*.md`、`docs/delivery/*.json`、`docs/evidence/*.md`、[traceability.json](../delivery/traceability.json) | 开源与 NuGet 矩阵、本机环境清单、`M1`~`M6` 验收标准、阶段用例清单、双向追踪与阶段验证报告 |
| **调研档案与模板** | `docs/research/*.md`、`docs/templates/*.md` | 前期源码级深度调研推导与第三方协议逆向证据（`reference` 状态）、标准写作模板 |

---

## 2. 文档注册表与生命周期状态

全仓所有自有 Markdown 文档（根级、`.github/`、`docs/`）以及结构化治理/交付 JSON 清单（`.github/*.json`、`docs/governance/document-registry.json`、`docs/delivery/*.json`）均必须在 [document-registry.json](document-registry.json) 中唯一登记，包含 `path`、`kind`、`status`、`owner`、`scope`、`reviewed`、`basis` 七个必填字段。

| `status` 值 | 规范含义与约束 |
| :--- | :--- |
| `active` | 当前有效并直接约束开发与审查（模板 `active` 表示新建同类材料应采用该结构；设计/规格 `active` 表示契约已定案，代码是否落地看 `main` 上的代码与 `delivery/traceability.json`） |
| `draft` | 尚待原型实测或关键前置条件锁定的草案契约，允许在授权范围内做隔离验证 |
| `reference` | 前期技术调研档案（`docs/research/*`）或历史阶段验证证据，保留推导过程与协议逆向细节供查阅；当与 `active` 设计/模块正本存在细节演进差异时，一律以 `active` 正本为准 |
| `superseded` | 已被新文档或新 ADR 正式替代（必须同时填写合法有效的 `supersededBy` 目标路径），不再指导新代码实现 |

---

## 3. ADR（架构决策记录）触发条件

普通局部修复、明确契约内的代码实现或文案勘误直接更新对应正本，**无需新建 ADR**。出现以下任一重大决策时，必须按 [ADR 模板](../templates/decision-record.md) 在 `docs/decisions/` 下新增或修订编号 ADR，并同步更新 [confirmed-decisions.md](../decisions/confirmed-decisions.md)：

1. 调整桌面 UI 框架、目标 `.NET` 主版本或核心渲染交换链方案；
2. 变更进程物理隔离边界、新增跨完整性级别 IPC 通道或调整 `Anti-Cheat Guard` 四级风险联锁矩阵；
3. 变更 `spawn.db` 核心存储引擎、`SPAWN_Vault` 内容寻址/FastCDC 切块算法、`SPPK` 云同步包格式或 NTFS Hardlink/Junction 部署机制；
4. 引入或替换关键第三方开源基座（如节点画布、内存反汇编引擎、二次元核心编排框架）或涉及许可证兼容性边界变化；
5. 调整仓库级文档治理模型、开发流程（issue、PR、审查与合并规则）或 Git 提交规范。

---

## 4. 每周无用文件整理与去阶段标签规则

<a id="stage-cleanup"></a>
<a id="destaging"></a>

为防止仓库堆积过期的临时汇总稿、`*-old.md` 或混乱的历史阶段标签，[每周维护](../dev-rules/development-workflow.md#weekly)时执行以下清理，结果写在维护 PR 里：

1. **全盘四分类文件清理**：
   - 盘点 `git ls-files`、`git status --short` 及本地未跟踪材料，对遗留的临时汇总报告、过渡草稿或验证副产物逐一执行四分类裁决：**① 原样保留**（长期有效正本与必需本地配置） / **② 原地精炼**（剔除冗余过程叙述） / **③ 提炼后删除**（将仍有价值的架构结论、避坑规则与验证事实回写至正式设计/模块正本，更新全部入链并通过门禁后删除原临时文件） / **④ 直接删除**（可再生的过期缓存或废弃草稿）。
   - 进行中的计划写在 issue 与 PR 里，不在仓库建阶段计划文件；需要长期留存的阶段或专项验证报告放 `docs/evidence/<日期>-<主题>.md`（`reference` 状态），其余依托 Git 提交历史追溯（用固定提交链接回看），严禁建立杂乱的 `docs/archive/` 或 `*-backup.md`。
2. **长期正本去旧阶段标签化**：
   - 全仓统一使用标准里程碑代号 **`M1`、`M2`、`M3`、`M4`、`M5`、`M6`**（禁止混用未定义的 `Phase 0`、`Sprint 1`、`S0`~`S4` 等异构代号作为工程阶段标识）。
   - 除 [交付路线图](../engineering/delivery-plan.md) 与 `docs/evidence/` 下的验证报告外，长期产品原则、系统设计、模块规格与工程规范应直接陈述**当前有效契约**，并在实现落地后将“某阶段待建”表述更新为真实运行事实，不堆叠各轮修改流水账。

---

## 5. 冲突处理与公开卫生门禁

<a id="conflicts"></a>

1. **权威优先级顺序**：
   - 用户在当前会话或 GitHub issue 中给出的最新明确指示 > [AGENTS.md](../../AGENTS.md) 与 [已确认决策](../decisions/confirmed-decisions.md) > `active` 状态的需求/设计/模块正本 > `reference` 状态的前期调研档案 > 外部参考仓库或模型默认推断。
   - 当用户新决定推翻既有正本设计时，执行者必须在**同一轮次**同步修订 `confirmed-decisions.md`、受影响的设计/模块正本及 `traceability.json`，消除新旧文档打架。
2. **公开自洽与零泄露底线**：
   - 仓库内所有受管文本必须对公开克隆者完全自洽可读，严禁出现任何个人机器绝对路径（如用户主目录路径或个人代码盘路径）、外部兄弟仓库名称泄露、真实密钥/Token 或 `.db`/`.pfx`/`.key` 二进制敏感文件。
   - 机械校验统一由 `python scripts/check_docs.py` 执行（详见 [质量门禁](../dev-rules/quality-gates.md)）；逻辑一致性、反作弊边界严密性与运行证据真实性由非作者的独立审查者按 [REVIEW.md](../../REVIEW.md) 审查把关。
