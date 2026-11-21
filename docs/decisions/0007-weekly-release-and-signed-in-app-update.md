# ADR-0007：正式版每周发布流水线、签名应用内更新与开发版逐提交重装

- **状态**：Accepted
- **日期**：2026-11-22
- **维护角色**：Agent A / 系统架构师
- **关联决策与需求**：`D-15`、`D-23`、`D-30` ~ `D-33`；`NFR-05`、`NFR-06`、`NFR-07`、`NFR-09`、`REQ-SEC-01`；验收 `AC-25`
- **部分替代**：[集中工程事项台账](open-questions.md) `LOOP-05` 中“`M6` 收官阶段补齐 Ed25519 离线公钥验签在线更新引擎”的承接阶段与签名算法；`%ProgramFiles%\spawn\Overlay\` 自签名证书部署仍留在 `LOOP-05`

---

## 1. 背景与问题约束

2026-11-22 `M3` 集中审查时核查现有打包链路（`scripts/package.py` + `installer/spawn.iss`），确认它只适合本机开发版：

1. **正式版换机不可用**：默认依赖框架发布，安装器不检测 `.NET 10` 桌面运行时，缺运行时的电脑装完无法启动。
2. **调试符号随包分发**：`app\` 共 `107MB`，其中 `*.pdb` 占 `82MB`（仅 `libSkiaSharp.pdb` 一个文件就有 `84MB`）。
3. **正式版版本号固定**：`NEXT_BASE_VERSION = "0.1.1"` 直接作为正式版版本，每周构建无法区分新旧，安装器也无法判断升级方向。
4. **没有发布与更新通道**：没有正式版构建工作流，也没有应用内更新；开发版只在交接节点才重装。

用户 2026-11-22 要求在 `M3` 内一并完成“正式版每周构建 + 应用内自动更新 + 开发版自动重装”，并指定按本机参考项目的做法执行（参考项目名称不入库）。

## 2. 方案查证（核验日 2026-11-22）

| 候选 | 许可证 / 活跃度 | 结论 |
| :--- | :--- | :--- |
| `velopack/velopack` | MIT，`1.2.161`（2026-09-29），2026-11-16 仍有提交 | **否决**（用户 2026-11-22 对照后确认）：优点是自带差分包与 GitHub Releases 集成；但默认安装到 `%LocalAppData%\{packId}`，与正式版数据根 `%LOCALAPPDATA%\spawn` 冲突，必须改名并迁移现有安装；`Main()` 须接入其启动钩子，Clean Host 新增 NuGet 依赖；更新包仅以发布源内 SHA1/SHA256 校验，无独立发布者签名，在不采购证书（`Q-001`）时真实性只靠 HTTPS；一键式安装器几乎不可定制 |
| MSIX + App Installer | 系统组件 | **否决**：必须有受信任证书（自签名需用户手动导入），容器化影响 AppData、外部修改器拉起与 UIAccess 覆盖层 |
| WiX（MSI）/ NSIS / ClickOnce | WiX MS-RL（官方二进制另有 OSMF）；NSIS zlib；ClickOnce 随 SDK | **否决**：均无内置签名更新；WiX 按用户安装编写繁重，NSIS 与 Inno 同类无收益，ClickOnce 定制能力弱且与 `Workers\Trainer\` 多进程布局不匹配 |
| `NetSparkleUpdater/NetSparkle` | MIT，`3.1.0`（2026-05-05） | **否决**：Ed25519 验签需引入 BouncyCastle 等第三方加密库进 Clean Host，appcast 体系与现有 Inno 安装器重叠 |
| **自研最小更新器**（Inno Setup 安装包 + 签名清单 + 现有重启契约） | 零新增 NuGet 包 | **采纳**：复用 `RequestQuitForUpdateAsync`、`UpdateRestartReason.Update` 与 5 分钟重启标记；验签使用 BCL `ECDsa` |
| Ed25519（原 `LOOP-05` 设想） | `.NET 10` BCL 仅在 `CompositeMLDsa` 组合算法中出现，无独立 Ed25519 API | **改用 ECDSA P-256 / SHA-256**（BCL + Windows CNG，零依赖；签名采用 DER 编码，CI 端可用 `openssl` 独立复核） |
| GitHub `windows-2025` runner | 预装 Inno Setup `6.7.1`，不是本地使用的 `7.x` | 发布工作流固定下载官方 Inno Setup `7.1.1`（2026-10-28，已过 7 天冷却）并钉扎 SHA-256 |

## 3. 决策

### 3.1 通道职责

| 通道 | 构建方式 | 如何更新 |
| :--- | :--- | :--- |
| 正式版 `spawn` | GitHub Actions 每周构建，**自包含** `win-x64`，剥离全部 `*.pdb`（另出符号包） | 应用内每 24 小时检查一次，ECDSA 验签 + SHA-256 校验后静默安装并恢复窗口/托盘状态 |
| 开发版 `spawn-dev` | 本机 `package.py --install-dev`，依赖框架发布，保留托管 `*.pdb`，剥离原生 `*.pdb` | **永不自查更新**；Agent 每次本地提交或 `amend` 后自动重装（`D-32`） |
| 测试身份 `spawn-test` / `spawn-dev-test` | 仅自动化演练 | 不更新 |

### 3.2 每周发布流水线（`D-30`）

- **触发**：`schedule: "0 0 * * 1"`（北京时间每周一 08:00）+ 手动 `workflow_dispatch`（输入 `publish` 默认 `false`）；**绝不**由 `push` / `pull_request` 触发。
- **仅限 `main`**：`plan` 任务带 `if: github.ref == 'refs/heads/main'`（`check` / `build` / `publish` 随之跳过），`publish` 的 `if` 再显式前置 `github.ref == 'refs/heads/main' &&`；GitHub `release` 环境的 Deployment branches and tags 设为 Selected branches 且只允许 `main`（`USER-ACTIONS` `ACT-009`），从其它分支手动触发既不会构建也无法取用签名私钥。由 `ReleaseWorkflowGateTests` 断言。
- **是否构建**：`plan` 任务计算自上一个 `v<主>.<次>.*` 标签以来 `main` 的 first-parent 提交数（每个 Squash 合入的 PR 对应一个），为 `0` 则本周跳过；首次发布没有标签，直接构建。
- **版本号**：版本线取 `NEXT_BASE_VERSION` 的 `主.次`，新版本为已有最高补丁号 `+1`，首个版本为 `NEXT_BASE_VERSION` 本身；开发版为 `<下一正式版本>-dev.<UTC 时间戳>`。
- **任务链**：`plan`（ubuntu，只读）→ `check`（windows-2025，等价 `quick_gate`：构建、全量测试、`check_docs`、`git diff --check`）→ `build`（windows-2025，`environment: release`，自包含打包、签名更新清单、隔离安装演练、上传 7 天构建产物）→ `publish`（ubuntu，**唯一**拥有 `contents: write` 的任务）。
- **发布开关**：`publish` 仅在 `main` 分支、`vars.SPAWN_RELEASE_PUBLISH == '1'` 且（定时触发或手动输入 `publish=true`）时运行。该仓库变量即用户一次性给出的长期发布授权（`USER-ACTIONS` `ACT-010`），关闭变量即可随时停发。
- **发布步骤**：`sha256sum -c SHA256SUMS` → 核对 `VERSION.txt` 通道与版本 → `openssl` 以仓库内公钥复核清单签名 → `gh release create v<版本> --draft` 上传全部文件 → 逐项核对资产齐全 → 转为正式版并标记 `--latest`。
- **供应链**：所有 `uses:` 以 40 位提交 SHA 钉扎并遵守 7 天冷却；顶层 `permissions: {}`；签名私钥仅存在于 `release` 环境 Secret `SPAWN_UPDATE_SIGNING_KEY_PEM`，在 `build` 任务内写入 `RUNNER_TEMP` 临时文件并在 `finally` 中删除，严禁回显。

### 3.3 签名应用内更新（`D-31`）

- **只有正式版检查**：`SpawnChannel == release` 才启用；开发版与源码运行时设置页显示“开发版由本机打包自动重装”。
- **频率**：启动后 60 秒检查一次，之后每 24 小时一次；设置页可手动检查，也可关闭自动检查。离线时静默顺延，不弹窗。
- **来源与网络边界**：`https://github.com/chialecode/spawn/releases/latest/download/update-windows-x64.json` 及 `.sig`；仅 HTTPS，主机白名单 `github.com`、`objects.githubusercontent.com`、`release-assets.githubusercontent.com`，手动重定向最多 5 跳，清单上限 `64KB`、安装包上限 `300MB`；User-Agent 如实标为 `SPAWN-Updater/<版本>`，**不携带任何设备或用户标识**（与 `NFR-06` 零遥测兼容）。
- **校验链**：清单先用编译进 `Spawn.Core` 的 ECDSA P-256 公钥验签（与 `installer/update-signing-public.pem` 逐字节一致，由门禁测试断言）→ 通道必须为 `release` → 版本必须**严格高于**当前版本（拒绝降级与同版本）→ 安装包 URL 必须位于 `https://github.com/chialecode/spawn/releases/download/v<版本>/` 之下 → 下载到 `<DataRoot>\cache\updates\<版本>\*.partial`，核对大小与 SHA-256 后原子重命名，只保留最新一份。
- **安装时机（默认值见 `Q-002`）**：自动下载；SPAWN **空闲**时（无受追踪游戏运行、无修改器会话、无存档快照/回滚/数据清理进行中）且仅驻留托盘才自动安装；主窗口打开时只显示“新版本已就绪，重启更新”提示条与托盘提示，由用户点击。设置可改为“仅提醒”。
- **安装动作**：启动前以禁止写入的句柄重新核对 SHA-256，并持有到安装器进程创建完成（同 `F-02` 的 TOCTOU 防护）→ `RequestQuitForUpdateAsync(UpdateRestartReason.Update)`（先停修改器会话、写重启标记）→ `ShellExecute` 启动按用户安装包 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /SPAWNRELAUNCH`（零 UAC）→ 安装器检测到 `/SPAWNRELAUNCH` 后拉起新版本，并按重启标记恢复窗口或托盘状态。
- **记录**：`app_settings` 中保存最近一次/下一次检查时间与结果，以及最近 20 条更新记录（时间、旧版本 → 新版本、自动/手动、结果）；本阶段不升级 Schema。
- **Clean Host**：更新器只用 `HttpClient`、BCL `ECDsa`、`SHA256` 与 `ShellExecute`，`Spawn.Architecture.GateTests` 必须保持全绿。

### 3.4 打包修正（`D-33`）

- 正式版 `Spawn.Host`、`Spawn.OverlayHost`、`Workers\Trainer\Spawn.TrainerWorker` 全部自包含发布，Worker 仍独立于 `Workers\Trainer\`（ADR-0006），不得为节省体积平铺或共享目录；报告中如实记录安装包体积。
- 所有通道剥离原生 `*.pdb`；正式版剥离全部 `*.pdb`，另生成 `spawn-symbols-<版本>-x64.zip` 随 Release 上传（不安装）。
- 三次 `dotnet publish` 允许合并为先 `build` 后 `--no-build` 发布以减少重复编译，但不得改变产物目录结构。

### 3.5 开发版逐提交重装（`D-32`）

Agent 每次本地 `git commit` 或 `git commit --amend` 成功后，立即执行 `python -B scripts/package.py --install-dev`，使本机 `SPAWN Dev` 与 `HEAD` 保持一致；`SPAWN_DEV_AUTO_INSTALL=0` 或 `CI=true` 时跳过；`quick_gate.py`、单元测试与 CI 严禁调用。这只改协作规则，不改产品代码。

## 4. 后果

- **正向**：新电脑装完即用；安装包不再携带 `82MB` 调试符号；正式版每周有改动才发、无改动不发，发布可随时由仓库变量停止；更新链条全程验签且零新增依赖。
- **代价**：正式版安装包因自带运行时变大（Worker 再带一份运行时，依赖 LZMA2 固实压缩去重，实际体积由 `M3-W10` 实测记录）；签名私钥丢失将导致已安装正式版无法再自动更新，需用户按 `ACT-009` 离线备份。
- **未覆盖**：差分更新包、正式版 Authenticode 签名（`Q-001` 默认不采购）、`%ProgramFiles%` Overlay 自签名证书部署（仍属 `LOOP-05`）。
