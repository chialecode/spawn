# SPAWN 开发与贡献指南

AI Agent 与程序员遵循同一套规则：开工前先读 [AGENTS.md](AGENTS.md)、[项目关键事实](docs/project-baseline.md) 与[开发流程](docs/dev-rules/development-workflow.md)。项目已完成 `M1`（[PR #1](https://github.com/chialecode/spawn/pull/1)）治理契约基线与 `M2`（[PR #2](https://github.com/chialecode/spawn/pull/2)）原生启动器、游戏数据清理及单机成就 Overlay 闭环，`M3` 单机修改器安全管家与每周发布也已合入（[PR #7](https://github.com/chialecode/spawn/pull/7)）；后续按 `M4` ~ `M6` 路线推进，进行中的工作见 [Issues](https://github.com/chialecode/spawn/issues)。

---

## 1. 开发环境要求

SPAWN 面向 **Windows 10 (21H1, Build 19043+) / Windows 11 x64** 开发与运行。完整工具链、版本矩阵与可选验证外设详见 [技术选型与开发环境清单](docs/engineering/technology-selection.md)。

| 组件类别 | 版本要求 | 用途说明 | 快速安装命令（`winget`） |
| :--- | :--- | :--- | :--- |
| **Python 3** | `3.12+`（仅标准库） | 运行 `scripts/check_docs.py`、`scripts/quick_gate.py` 等自动化工程门禁脚本 | `winget install --id Python.Python.3.12 --source winget` |
| **.NET 10 SDK (LTS)** | `10.0.x`（本机实测 `10.0.404` 已就绪） | 编译 `Spawn.sln`（`net10.0-windows`、WPF、`NamedPipeJsonRpc`、CsWin32 Source Generator、xUnit 门禁） | `winget install --id Microsoft.DotNet.SDK.10 --source winget` |
| **Windows SDK & C++ Build Tools** | `10.0.26100.0+` / VS 2022 `17.12+`（或 VS Build Tools） | Win32 / DirectComposition / WASAPI 元数据、原生互操作调试与打包支持 | 通过 Visual Studio Installer 勾选“.NET 桌面开发”或安装 Build Tools |
| **Git & GitHub CLI** | Git `2.45+` / `gh 2.60+` | 提交与 DCO 签名、push、PR 与 issue 管理 | `winget install --id Git.Git` / `winget install --id GitHub.cli` |

> [!NOTE]
> 源码运行与测试默认走开发通道（数据根目录 `%LOCALAPPDATA%\spawn-dev` 或显式设置环境变量 `$env:SPAWN_DATA_DIR = ".local/dev-data"`），与正式版 `%LOCALAPPDATA%\spawn` 完全物理隔离。

---

## 2. 最小开发与交付闭环

1. **对齐契约**：确认目标所属里程碑（`M1`~`M6`）、需求编号（`REQ-*`）、架构决策（`D-*` / `ADR-*`）与模块规格（`docs/modules/*`）。
2. **开源先行**：新增通用能力前按 [开源与架构规则](docs/dev-rules/architecture-and-open-source.md) 核查成熟活跃且兼容 `Apache-2.0` 的开源方案；上游限制记入[上游事项台账](docs/decisions/open-questions.md)。
3. **边界内实现**：严守 `Spawn.sln` 项目引用方向（见 [仓库地图](docs/dev-rules/repo-map.md)），绝不将内存修改、输入注入或机器视觉依赖引入 `Spawn.Host` 或 `Spawn.OverlayHost`。公共契约、SQLite Schema 或行为变更必须同轮同步对应文档与 `document-registry.json`。
4. **本地门禁与自检**：运行下述质量门禁并对照 [REVIEW.md](REVIEW.md) 逐项自查；未执行的实机或外部环境检查诚实记录为 `not-run`。
5. **小 PR、独立审查与合并**：按[开发流程](docs/dev-rules/development-workflow.md#change-loop)一个 issue 一个小 PR（分支 `<type>/<issue号>-<简短描述>`），PR 按模板写验证证据；CI 通过后由非作者按 [REVIEW.md](REVIEW.md) 审查，低风险由 Agent 合并，碰到[高风险清单](docs/dev-rules/development-workflow.md#high-risk)的等用户合并。

---

## 3. 分支、DCO 签名与提交规范

完整规则见 [Git 工作流规范](docs/dev-rules/git-workflow.md)：

- **不可变根提交**：`main` 分支第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e` 为不可变锚点，严禁改写。
- **分支与提交**：每个 issue 从最新 `main` 切一个分支，Conventional Commits；PR 合并时 squash 成一个提交，分支里提交几次都可以，PR 推送后的修复追加新提交。
- **强制 DCO 签名**：所有提交必须包含与提交者身份一致的 `Signed-off-by` 尾注（使用 `-s` 参数）；`main-protection` 规则集要求 PR 仅允许 Squash 合并（使用 `PR_TITLE` 与 `PR_BODY`），仓库开启了网页提交签名要求，squash 合并时 GitHub 会给合并提交补上 `Signed-off-by`，PR 正文不用手写。
- **精确暂存**：一律使用 `git add -u` 或显式文件相对路径暂存，严禁使用 `git add .`。

---

## 4. 验证门禁命令

| 适用时机 | 执行命令 | 门禁职责说明 |
| :--- | :--- | :--- |
| **文档与卫生门禁（每次必跑）** | `python scripts/check_docs.py`<br/>`git diff --check` | 校验必需治理文件、`document-registry.json` 全覆盖、相对链接与锚点有效性、拦截本机绝对路径与外部兄弟项目名泄露、校验空白与行尾规范 |
| **日常快速门禁（每个 PR）** | `python -B scripts/quick_gate.py` | 5 分钟内静默低优先级串联文档治理检查、Release 全量编译、`Spawn.sln` 全量测试（含 `PEReader` IAT 审计、离线网络绊线与 `spawn-dev-test` 打包隔离演练）与 `git diff --check` |
| **整套交付门禁（发布前，或改到打包、安装、更新、签名与发布链路时）** | `dotnet build Spawn.sln -c Release`<br/>`dotnet test Spawn.sln -c Release --no-build`<br/>`dotnet list Spawn.sln package --vulnerable --include-transitive` | 全量构建零警告、`Spawn.Architecture.GateTests` Clean Host / Worker `PEReader` 静态审计、全量单元与集成测试及零已知漏洞 / 零 Copyleft 传递依赖核查 |

---

## 5. 变更材料模板与开源许可

- 按变更规模按需选用 [文档模板](docs/templates/README.md)：跨模块方案使用 [变更提案](docs/templates/change-proposal.md)，长期架构取舍使用 [ADR 模板](docs/templates/decision-record.md)，新增业务子域使用 [模块规格模板](docs/templates/module-spec.md)。
- 本仓库自有代码与文档采用 [Apache License 2.0](LICENSE) 授权。提交代码或文档即代表贡献者确认其原创内容符合 DCO 声明并同意按 Apache-2.0 协议分发。
- Issues 与 Pull Request 只允许协作者创建；外部反馈、问题交流与功能建议请用 [Discussions](https://github.com/chialecode/spawn/discussions)。安全漏洞请按 [SECURITY.md](SECURITY.md) 通过 GitHub 私密漏洞报告通道提交。
