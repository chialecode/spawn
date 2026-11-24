# SPAWN 开发与贡献指南

开始任何文档修订或代码开发前，请先阅读 [AGENTS.md](AGENTS.md)、[项目关键事实](docs/project-baseline.md) 与 [当前执行状态](docs/delivery/status.md)。项目已完成 `M1`（[PR #1](https://github.com/chialecode/spawn/pull/1)）治理契约基线与 `M2`（[PR #2](https://github.com/chialecode/spawn/pull/2)）原生启动器、游戏数据清理及单机成就 Overlay 闭环，当前沿 `M3` ~ `M6` 路线推进（各阶段进展与验证证据以 [当前执行状态](docs/delivery/status.md) 为准）。

---

## 1. 开发环境要求

SPAWN 面向 **Windows 10 (21H1, Build 19043+) / Windows 11 x64** 开发与运行。完整工具链、版本矩阵与可选验证外设详见 [技术选型与开发环境清单](docs/engineering/technology-selection.md) 及 [USER-ACTIONS.md](USER-ACTIONS.md) 中的 `ACT-001`。

| 组件类别 | 版本要求 | 用途说明 | 快速安装命令（`winget`） |
| :--- | :--- | :--- | :--- |
| **Python 3** | `3.12+`（仅标准库） | 运行 `scripts/check_docs.py`、`scripts/quick_gate.py` 等自动化工程门禁脚本 | `winget install --id Python.Python.3.12 --source winget` |
| **.NET 10 SDK (LTS)** | `10.0.x`（本机实测 `10.0.404` 已就绪） | 编译 `Spawn.sln`（`net10.0-windows`、WPF、`NamedPipeJsonRpc`、CsWin32 Source Generator、xUnit 门禁） | `winget install --id Microsoft.DotNet.SDK.10 --source winget` |
| **Windows SDK & C++ Build Tools** | `10.0.26100.0+` / VS 2022 `17.12+`（或 VS Build Tools） | Win32 / DirectComposition / WASAPI 元数据、原生互操作调试与打包支持 | 通过 Visual Studio Installer 勾选“.NET 桌面开发”或安装 Build Tools |
| **Git & GitHub CLI** | Git `2.45+` / `gh 2.60+` | 本地单阶段提交、DCO 签名校验与经用户明确授权后的低频 PR 管理 | `winget install --id Git.Git` / `winget install --id GitHub.cli` |

> [!NOTE]
> 源码运行与测试默认走开发通道（数据根目录 `%LOCALAPPDATA%\spawn-dev` 或显式设置环境变量 `$env:SPAWN_DATA_DIR = ".local/dev-data"`），与正式版 `%LOCALAPPDATA%\spawn` 完全物理隔离。

---

## 2. 最小开发与交付闭环

1. **对齐契约**：确认目标所属里程碑（`M1`~`M6`）、需求编号（`REQ-*`）、架构决策（`D-*` / `ADR-*`）与模块规格（`docs/modules/*`）。
2. **开源先行**：新增通用能力前按 [开源与架构规则](docs/dev-rules/architecture-and-open-source.md) 核查成熟活跃且兼容 `Apache-2.0` 的开源方案；上游限制记入 [集中工程事项台账](docs/decisions/open-questions.md)。
3. **边界内实现**：严守 `Spawn.sln` 项目引用方向（见 [仓库地图](docs/dev-rules/repo-map.md)），绝不将内存修改、输入注入或机器视觉依赖引入 `Spawn.Host` 或 `Spawn.OverlayHost`。公共契约、SQLite Schema 或行为变更必须同轮同步对应文档与 `document-registry.json`。
4. **本地门禁与自检**：运行下述质量门禁并对照 [REVIEW.md](REVIEW.md) 逐项自查；未执行的实机或外部环境检查诚实记录为 `not-run`。
5. **集中审查与授权集成**：按 [开发协作流程](docs/dev-rules/development-workflow.md) 提交阶段报告，经 Agent A / Verifier 集中技术审查收敛后，在 [USER-ACTIONS.md](USER-ACTIONS.md) 登记具体 `ACT-*`，获用户明确授权后执行推送与创建 PR。

---

## 3. 分支、DCO 签名与提交规范

完整规则见 [Git 工作流规范](docs/dev-rules/git-workflow.md)：

- **不可变根提交**：`main` 分支第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e` 为不可变锚点，严禁改写。
- **单阶段单提交**：每个里程碑阶段切出独立分支（命名形如 `feat/m1-foundation`、`feat/m2-launcher-achievement`）。在获准 push 之前，同一阶段内的规划、编码、审查修复与返工统一使用 `git commit --amend -s` 收束进该阶段的唯一提交，不保留琐碎中间提交。
- **强制 DCO 签名**：所有提交必须包含与提交者身份一致的 `Signed-off-by` 尾注（使用 `-s` 参数）；`main-protection` 规则集要求 PR 仅允许 Squash 合并（使用 `PR_TITLE` 与 `PR_BODY`），因此 PR 正文末尾同样须保留有效的 `Signed-off-by` 行（见 [PR 模板](.github/PULL_REQUEST_TEMPLATE.md)）。
- **精确暂存**：一律使用 `git add -u` 或显式文件相对路径暂存，严禁使用 `git add .`。

---

## 4. 验证门禁命令

| 适用时机 | 执行命令 | 门禁职责说明 |
| :--- | :--- | :--- |
| **文档与卫生门禁（M1 起每次必跑）** | `python scripts/check_docs.py`<br/>`git diff --check` | 校验必需治理文件、`document-registry.json` 全覆盖、相对链接与锚点有效性、拦截本机绝对路径与外部兄弟项目名泄露、校验空白与行尾规范 |
| **日常快速门禁（M2 起增量修改使用）** | `python -B scripts/quick_gate.py` | 5 分钟内静默低优先级串联文档治理检查、Release 全量编译、`Spawn.sln` 全量测试（含 `PEReader` IAT 审计、离线网络绊线与 `spawn-dev-test` 打包隔离演练）与 `git diff --check` |
| **整阶段交付门禁（M2 起阶段收尾跑一次）** | `dotnet build Spawn.sln -c Release`<br/>`dotnet test Spawn.sln -c Release --no-build`<br/>`dotnet list Spawn.sln package --vulnerable --include-transitive` | 全量构建零警告、`Spawn.Architecture.GateTests` Clean Host / Worker `PEReader` 静态审计、全量单元与集成测试及零已知漏洞 / 零 Copyleft 传递依赖核查 |

---

## 5. 变更材料模板与开源许可

- 按变更规模按需选用 [文档模板](docs/templates/README.md)：跨模块方案使用 [变更提案](docs/templates/change-proposal.md)，长期架构取舍使用 [ADR 模板](docs/templates/decision-record.md)，新增业务子域使用 [模块规格模板](docs/templates/module-spec.md)，阶段交接使用 [A/B 交接模板](docs/templates/agent-handoff.md)。
- 本仓库自有代码与文档采用 [Apache License 2.0](LICENSE) 授权。提交代码或文档即代表贡献者确认其原创内容符合 DCO 声明并同意按 Apache-2.0 协议分发。
- 仓库为防范垃圾信息已关闭公开 Issues、Wiki 与 Discussions，仅允许协作者创建 PR；安全漏洞请按 [SECURITY.md](SECURITY.md) 通过 GitHub 私密漏洞报告通道提交。
