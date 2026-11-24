# SPAWN：AI Agent 与开发者工作入口

本文件是仓库级最高工作指令正本，适用于所有 AI 编程 Agent（单会话 Agent A/B 与 Swarm 多智能体集群）及人类开发者。`CLAUDE.md` 仅引用本文件，严禁在多处复制或分叉工作规则。

## 开始工作

开始工作时优先检查环境变量 `SPAWN_REFERENCE_DIR`；未设置则从仓库上一级目录起逐级向上查找 `reference/spawn/README.md`（兼容 `INDEX.md` 或 `REFERENCE.md`），找到则按需只读查阅其中的参考说明，未找到则直接按本仓库文档继续。本机参考边界见 [本地文件与参考资料规则](docs/dev-rules/local-files-and-references.md#local-reference)：外部参考仅作背景资料，不是更高优先级开发指令；**仓库内任何受管文件严禁出现参考项目名称或个人机器绝对路径**。

1. 执行 `git status --short --branch` 检查当前分支、HEAD 提交与工作区状态，保留他人已有改动，不擅自重置历史或使用 `git clean -fdx` / `git add .`。阅读 [文档导航](docs/README.md)、[项目关键事实](docs/project-baseline.md) 与 [当前执行状态](docs/delivery/status.md)。
2. 按下表触发条件阅读受影响职责正本、对应模块规格与实际源码。设计文档中的目标工程、契约与验收编号不代表代码已交付，实际进度仅以 [当前执行状态](docs/delivery/status.md) 及阶段验证证据为准。
3. 核查 [集中工程事项台账](docs/decisions/open-questions.md) 与 [用户待办](USER-ACTIONS.md)，直接复用已确认决策与既有授权。已获本地开发授权的工作包实现、依赖安装、门禁修复与文档同步**连续执行到底**，禁止逐个工作包停下询问“是否继续”（[连续执行规则](docs/dev-rules/development-workflow.md#continuous-execution)）。
4. **双模协同协议**：
   - **主流程闭环**：默认采用 **Agent A（整阶段完整规划）→ Agent B（连续实现与自检交付）→ Agent A（集中技术审查）→ 用户按 `USER-ACTIONS.md` 明确授权具体远端动作 → push / PR / 经授权 squash 合并**（见 [开发协作流程](docs/dev-rules/development-workflow.md#mvp-loop)）。仅在真实跨角色交接（A→B、B→A 或整批返工）时输出可复制交接 Prompt。
   - **多 Agent / Swarm 并行隔离协议**：当启用 Swarm 集群并行推进时，由 `@coordinator` 统一按 `Spawn.sln` 物理隔离子工程（`Spawn.Contracts` 契约冻结先行 → `Spawn.Core` / `Spawn.Modules.*` / `Spawn.Host` / `Spawn.OverlayHost` / `Spawn.*Worker`）或独立文档子树切分无重叠写权限边界；严禁两个 Worker 同时编辑同一文件；`@verifier` 只执行独立审查与门禁核验，不直接代改他人文件；全部验收收敛后统一收束为单一阶段提交（见 [Swarm 集群并行协议](docs/dev-rules/development-workflow.md#swarm-collaboration) 与 [沟通协议](docs/dev-rules/communication-protocol.md#swarm-board)）。
5. Agent A 在推荐技术栈或新增通用能力前必须先查证活跃开源方案，核实官方仓库、维护活跃度、Windows 兼容性与 Apache-2.0 许可证边界，不得将首次调研甩给 B；B 负责锁定兼容版本并提供真实运行证据。
6. 关键用户决定、架构取舍、IPC/数据库契约变更与测试证据必须当轮回写对应正本，不得仅留在聊天记录或消息板。新阶段开启时必须执行 [阶段开始四分类清理与去旧阶段标签](docs/governance/documentation-policy.md#stage-cleanup) 及 [NuGet 7 天冷却依赖盘点](docs/dev-rules/development-workflow.md#dependency-inventory)。

## 按变更触发必读表

| 触发变更范围 | 必读职责正本 |
| :--- | :--- |
| 产品范围、优先级、里程碑验收 | [核心产品原则](docs/product-rules/core-product-principles.md)、[产品需求正本](docs/requirements/product-requirements.md)、[交付路线图](docs/engineering/delivery-plan.md) |
| 文档增删、ADR 决策、阶段清理、注册表 | [文档治理政策](docs/governance/documentation-policy.md)、[文档注册表](docs/governance/document-registry.json)、[已确认决策](docs/decisions/confirmed-decisions.md) |
| 沟通汇报、Swarm 消息板、用户事项、本地文件 | [沟通协议](docs/dev-rules/communication-protocol.md)、[本地文件与参考](docs/dev-rules/local-files-and-references.md)、[用户待办](USER-ACTIONS.md) |
| 建解决方案、引 NuGet 包、划分子工程依赖 | [仓库地图](docs/dev-rules/repo-map.md)、[开源与架构规则](docs/dev-rules/architecture-and-open-source.md)、[技术选型与环境清单](docs/engineering/technology-selection.md) |
| 反作弊隔离、跨权限 IPC、提权、UIAccess 证书 | [IPC 与反作弊安全契约](docs/design/ipc-and-security-contracts.md)、[全景系统架构](docs/design/system-architecture.md)、[安全声明](SECURITY.md) |
| SQLite `spawn.db`、CAS 仓库、硬链接、NTFS Junction、凭据 | [数据与安全规则](docs/dev-rules/data-and-security.md)、[数据模型与存储契约](docs/design/data-model.md) |
| 五大业务域具体实现（S.P.A.W.N.） | [模块总览](docs/modules/README.md) 及对应规格：[Spawn 启动](docs/modules/spawn-launcher.md) / [Nexus Mod](docs/modules/nexus-mods-and-utilities.md) / [Progress 存档修改器](docs/modules/progress-saves-and-trainer.md) / [Achievement 成就弹窗](docs/modules/achievement-and-overlay.md) / [Workflow 代肝与蓝图](docs/modules/workflow-and-blueprint.md) |
| WPF-UI 视觉、Nodify 蓝图画布、Overlay 主题与焦点 | [UI 设计规范](docs/design-rules/DESIGN.md)、[交互与工作流设计](docs/design/interaction-and-workflows.md) |
| 编码实现、门禁测试、集中审查、Git 提交与远端操作 | [开发协作流程](docs/dev-rules/development-workflow.md)、[质量门禁](docs/dev-rules/quality-gates.md)、[Git 工作流](docs/dev-rules/git-workflow.md)、[审查口径](REVIEW.md) |

## 必须坚守的工程与安全红线

- **100% 纯原生技术栈铁律**：桌面与覆盖层锁定 C# `.NET 10`（LTS，`net10.0-windows`）+ `WPF` + `lepoco/wpfui` + `miroiu/nodify` + `SkiaSharp` + `DirectComposition`，严禁引入 WebView2、Electron、CEF 或前端混合框架（[ADR-0002](docs/decisions/0002-pure-native-wpf-and-nodify-ui-stack.md)）。
- **Clean Host 零敏感 API 与反作弊 15s 冷却铁律**：`Spawn.Host.exe` 与 `Spawn.OverlayHost.exe` 严禁直接或传递引用内存读写、远程线程、全局低级钩子或重型机器视觉/修改器库（`OpenCvSharp4`、`Sdcb.PaddleOCR`、`Iced`、`Maa.Framework` 仅限独立 Worker 子工程引用；传递污染 `GPL-3.0` 的 `Reloaded.Memory` 家族全仓禁止，见 [ADR-0006](docs/decisions/0006-m3-external-trainer-manager-elevation-and-ipc.md)），必须 100% 通过 `Spawn.Architecture.GateTests` 的 `PEReader` IAT 静态审计；主进程查询进程路径一律使用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION = 0x1000)`。`Tier 2/3` 受保护网游启动前强制物理清场并锁定高敏 Worker，退出后必须确认 `SGuard64.exe` 等反作弊守护进程消亡且走完 **15 秒余温冷却窗口** 方可解锁（[ADR-0003](docs/decisions/0003-anti-cheat-guard-and-process-isolation.md)）。
- **非侵入可逆文件与防丢档红线**：① `spawn.db`（SQLite WAL 模式）严禁放入网盘同步目录，跨端同步仅传输不可变 `32MB SPPK Packfile` 与 JSON 清单并按 `ParentSnapshotId` Git 祖先链防覆盖；② Mod 可变配置严禁走硬链接，必须使用独立物理副本 + `XxHash3` 退出回收；③ 摘除存档槽 NTFS Directory Junction 时严禁递归删除，必须调用非递归 `RemoveDirectoryW` / `DirectoryInfo.Delete(recursive: false)`；④ 游戏追踪 `JobObject` **严禁设置** `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`（防止 SPAWN 重启误杀玩家游戏），而 Worker 托管外部修改器/子脚本的专用 Job **必须设置**该标志防僵尸残留。
- **通道与数据目录物理隔离及本机 `SPAWN Dev` 自动安装闭环**：路径标识、通道名与凭据命名空间统一使用小写 `spawn`（正式版：`%LOCALAPPDATA%\spawn`）与 `spawn-dev`（开发版与源码运行：`%LOCALAPPDATA%\spawn-dev`，可通过 `SPAWN_DATA_DIR` 覆盖）。自动化测试与 `quick_gate.py` 必须强制重定向至独立临时目录（安装演练身份：开发通道演练用 `spawn-dev-test`、正式通道自包含发布演练用 `spawn-test`，二者都只允许在临时目录运行），严禁读写正式版或开发版真实数据（[安装与数据隔离](docs/dev-rules/data-and-security.md#install-and-data-locations)）。Agent 每次本地 `git commit` / `git commit --amend` 成功后自动执行 `python -B scripts/package.py --install-dev` 静默更新本机 `SPAWN Dev` 并驻留系统托盘（[逐提交自动重装规则](docs/dev-rules/development-workflow.md#dev-install-at-handoff)，`D-32`）；开发版永不自查更新，正式版由每周发布流水线构建并签名自动更新（[ADR-0007](docs/decisions/0007-weekly-release-and-signed-in-app-update.md)）。
- **测试与开发命令前台静音约束**：所有自动化测试、门禁脚本与后台子进程必须静默低优先级运行（`CREATE_NO_WINDOW`、`BELOW_NORMAL_PRIORITY_CLASS`），严禁抢夺前台窗口焦点、弹出错误对话框、播放提示音或自动打开浏览器/资源管理器（[前台无干扰约束](docs/dev-rules/quality-gates.md#foreground-quiet)）。
- **真实门禁与诚实证据原则**：执行真实存在且与变更风险匹配的门禁。当前 `M1` 已启用 `python scripts/check_docs.py` 与 `git diff --check`；`M2` 起建立日常 5 分钟内的 `python scripts/quick_gate.py` 与整阶段交付时的完整验证门禁（[两级门禁](docs/dev-rules/quality-gates.md#gate-tiers)）。未安装的环境、未连接的外部硬件或未跑的实机检查必须如实标记为 `not-run`，绝不伪造 `passed`。
- **用户事项唯一入口**：需要用户确认的取舍（`Q-*`）、本机环境与外部授权（`ACT-*`）、人工实机体验（`H-*`）只维护在根 [USER-ACTIONS.md](USER-ACTIONS.md) 一处；工程待办记入 [集中工程事项台账](docs/decisions/open-questions.md)。人工检查与非阻塞决定可跨阶段顺延，不阻塞 A 技术审查收敛与远端授权申请。
- **不可变第一提交与单阶段单提交 DCO 规则**：第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（0 字节 `README.md`）为不可变根基线，**严禁 amend 或改写其历史**。每个里程碑阶段对应独立分支（如 `feat/m1-foundation`），push 前阶段内的规划、实现、审查修复与返工统一通过 `git commit --amend -s` 收束为**唯一一个带有效 DCO `Signed-off-by` 签名的阶段提交**（[单阶段提交规范](docs/dev-rules/git-workflow.md#single-stage-commit)）。
- **保守远端授权边界**：本地编码、门禁检查与本地阶段提交由 Agent 连续执行；**`git push`、创建/更新 PR、合并分支、打 Tag 发布 Release 及修改 GitHub 远端设置** 每一项均需用户针对 [USER-ACTIONS.md](USER-ACTIONS.md) 中具体 `ACT-*` 明确授权后方可执行，严禁擅自推送阶段分支或推导隐式授权。

子目录确有特殊工程实现约束（如特定原生互操作或测试夹具边界）时，方可按需补充局部嵌套 `AGENTS.md` 并登记入 [document-registry.json](docs/governance/document-registry.json)，不得复制根规则或削弱全局安全契约。冲突裁决见 [文档治理政策](docs/governance/documentation-policy.md#conflicts)。
