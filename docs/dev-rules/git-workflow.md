# Git 工作流、不可变基线与远端授权规范

本文件约束 SPAWN 仓库的分支拓扑、不可变第一提交保护、单阶段单提交 DCO 签名纪律、远端明确授权边界与 GitHub API 低频操作准则。远端仓库地址为 [`https://github.com/chialecode/spawn`](https://github.com/chialecode/spawn)。

---

## 1. 不可变第一提交锚点与主干保护基线

1. **不可变根提交（严禁改写）**：
   - 仓库第一提交完整 40 位 SHA 为 **`ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`**（提交说明：`chore: initialize repository with empty README`，仅含 0 字节 `README.md`，带有效 DCO `Signed-off-by` 尾注）。
   - 该提交作为全仓历史根锚点永久冻结；`M1` 架构与治理基线已按 `ACT-002` 授权经 [PR #1](https://github.com/chialecode/spawn/pull/1) Squash 合入 `main`（`196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`）。**在任何情况下严禁对已推送至 `origin/main` 的历史提交执行 `git commit --amend`、`git rebase` 改写或强制推送覆盖！**
2. **`main` 分支保护契约（`main-protection` Ruleset）**：
   - 远端 `refs/heads/main` 已激活零豁免（`bypass_actors: []`）保护规则集（声明镜像见 [`.github/rulesets/main.json`](../../.github/rulesets/main.json)）：禁止删除分支、禁止非快进强推（`non_fast_forward`）、强制线性提交历史（`required_linear_history`）、禁止直接 push 到 `main`。
   - 所有变更必须通过 Pull Request 合入，仅允许 **Squash Merge**（合并标题取 `PR_TITLE`，合并正文取 `PR_BODY`，合并后自动删除远端源分支），且必须解决全部审查评论线程并通过必选状态检查 **`docs`**（对应 [`.github/workflows/docs.yml`](../../.github/workflows/docs.yml)）及 Windows `.NET 10` 构建门禁工作流（[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)）。

---

## 2. 单阶段单提交与强制 DCO 签名规范

<a id="single-stage-commit"></a>
<a id="dco"></a>

1. **一个里程碑阶段 = 一个分支 = 一个提交**：
   - 每个里程碑（`M1`~`M6`）从最新 `main` 切出独立阶段分支，命名格式统一为 `feat/m<序号>-<简短英文主题>`（例如当前 `M2` 阶段分支为 `feat/m2-launcher-achievement`，唯一父提交固定为 `main` `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`）。
   - **获准推送（push）之前**：同一阶段内的规划文档、模块代码实现、门禁测试修复、集中审查微调与返工修正，**一律通过 `git commit --amend -s` 收束进该分支的唯一一个阶段提交中**，不在历史中保留琐碎的过程提交或半成品试错记录。
   - **每次 `amend` 前后必检**：执行 `git rev-parse HEAD^` 确认阶段提交的唯一父提交 SHA 与阶段计划记录完全一致，确保绝未误改写父提交或 `main` 历史；若该阶段分支此前已获用户授权推送至远端 PR，则后续修复默认追加集中修复提交（除非用户另行明确授权强推更新分支）。
2. **强制 DCO（Developer Certificate of Origin）签名**：
   - 本仓所有提交（含本地阶段提交与 Squash 合并提交）均强制要求包含与 Git 作者身份一致的 `Signed-off-by: Name <email>` 尾注，提交命令统一携带 `-s` 参数（`git commit -s` / `git commit --amend -s`）。
   - 由于仓库配置了 `squash_merge_commit_message = "PR_BODY"`，创建 PR 时必须在正文末尾保留有效的 `Signed-off-by` 行（见 [PR 模板](../../.github/PULL_REQUEST_TEMPLATE.md)）。
   - 提交后与推送前统一运行以下校验命令确认范围内每个提交均带合法 DCO 签名：

```powershell
git log --format="%H | %s | %(trailers:key=Signed-off-by,valueonly)" origin/main..HEAD
```

3. **安全暂存纪律**：
   - 暂存前先运行 `python -B scripts/quick_gate.py`（内含 `check_docs.py`、Release 编译、全量测试与 `git diff --check`）；
   - 一律使用 `git status --short` 核对变更列表，通过显式相对路径或 `git add -u` 暂存受管文件，**严禁盲目运行 `git add .`、`git add -A` 或 `git clean -fdx`**，防止误吞 `.local/`、`bin/`、`obj/`、数据库或他人临时文件。

---

## 3. 远端动作明确授权表与 GitHub 低频操作原则

<a id="remote-auth"></a>
<a id="github-hygiene"></a>

本地开发连续自主推进，但**任何触及远端 GitHub 仓库的写操作均实行严格逐项显式授权制**：

| Git / GitHub 操作类别 | 授权与执行边界 |
| :--- | :--- |
| **本地分支创建、暂存、`commit -s` / `commit --amend -s`** | 属于本地已授权常规开发动作，完成门禁校验与父提交核对后由 Agent 连续执行，交付时报告最新完整 40 位 SHA |
| **`git push` 推送阶段分支至 `origin`** | **必须先在 [USER-ACTIONS.md](../../USER-ACTIONS.md) 登记具体 `ACT-*` 并获用户明确文字授权后方可执行**；严禁在技术审查刚完成时未经授权抢先推送 |
| **创建、重开或更新远端 Pull Request** | **必须获用户对具体 `ACT-*` 明确授权**；PR 正文须如实列明阶段范围、已跑门禁结果、`not-run` 边界与 DCO 签名 |
| **Squash 合并 PR、删除远端分支、打 Tag / 创建 Release** | **必须分别获得用户明确授权**（用户可在单条回复中一并写明“授权推送、建 PR，CI 通过后 squash 合并”，未写明合并则建完 PR 核对 CI 后停下等待合并指示；正式版每周自动打 Tag 与发布 Release 须通过仓库变量 `SPAWN_RELEASE_PUBLISH=1` 显式开启，见 [第 4 节](#weekly-release) 与 `ACT-010`） |
| **修改仓库设置、Ruleset、Actions 白名单或安全开关** | 仅在用户明确指示时通过 API 执行并同步回写 `.github/*.json` 声明镜像（镜像文件见 [`.github/repository-settings.json`](../../.github/repository-settings.json) 与 [`.github/actions-policy.json`](../../.github/actions-policy.json)） |

- **Actions 白名单与 SHA 固定约束**：仓内白名单声明镜像（[`.github/actions-policy.json`](../../.github/actions-policy.json)）涵盖 `actions/checkout@*`、`actions/setup-dotnet@*`、`actions/upload-artifact@*` 与 `actions/download-artifact@*`（首次启用远端 [`.github/workflows/release.yml`](../../.github/workflows/release.yml) 前需按用户授权同步更新 GitHub 远端 Actions 白名单）；所有 workflow 引用 Action 必须固定为**完整 40 位 commit SHA** 并附版本号注释（由 [`.github/dependabot.yml`](../../.github/dependabot.yml) 按月带 7 天冷却期巡检更新）；`scripts/check_docs.py` 与 `scripts/quick_gate.py` 坚持纯 Python 3.12+ 标准库零依赖。
- **GitHub API / CLI 低频调用准则**：执行已获授权的远端查询或操作时，使用精准的单次 `gh api` / `gh pr checks` 调用获取所需字段，严禁编写死循环高频轮询脚本、重复提交相同 API 写请求或在遇 `403`/`429` 限流时暴力重试。

---

## 4. 正式版每周发布流水线（`release.yml`）与发布开关

<a id="weekly-release"></a>

依据 [ADR-0007](../decisions/0007-weekly-release-and-signed-in-app-update.md) §3.2（`D-30`），SPAWN 正式版（`spawn` 通道）通过 [`.github/workflows/release.yml`](../../.github/workflows/release.yml) 每周自动构建并在获得明确仓库开关授权后发布 GitHub Release：

1. **严格触发与并发边界**：
   - 仅允许 `schedule`（`0 0 * * 1`，每周一 00:00 UTC / 北京时间 08:00）与手动 `workflow_dispatch`（布尔输入 `publish`，默认 `false`，便于随时 dry-run 演练）触发；**严禁**配置 `push` 或 `pull_request` 触发。
   - 顶层 `permissions: {}` 显式置空；顶层 `concurrency` 固定为 `group: release` 且 `cancel-in-progress: false`，避免正在构建或发布的正式版流程被并发运行打断。
2. **四任务流水线与最小权限**：
   - **`plan`（`ubuntu-24.04`，`contents: read`）**：拉取完整历史与标签（`fetch-depth: 0`、`persist-credentials: false`），执行 `python3 -B scripts/release.py --plan`；基于 `NEXT_BASE_VERSION` 主次版本线检索 `v<主>.<次>.*` 标签，无标签时首发取 `NEXT_BASE_VERSION`，否则统计上一标签以来 `main` 的 first-parent 提交数，为 `0` 则跳过本周发布，大于 `0` 则补丁号 `+1` 输出 `release=true` 与 `version`。
   - **`check`（`windows-2025`，`contents: read`，`CI=true`）**：在 `release == 'true'` 时执行 `dotnet restore`、`dotnet build Spawn.sln -c Release`、带临时数据目录隔离及离线网络绊线（`http://127.0.0.1:9`）的全量 `dotnet test Spawn.sln -c Release --no-build`，并依次运行 `python -B scripts/test_release.py`、`python -B scripts/check_docs.py` 与 `git diff --check`。
   - **`build`（`windows-2025`，`environment: release`，`contents: read`，`CI=true`）**：下载官方 Inno Setup `7.1.1`（`innosetup-7.1.1-x64.exe`）并严格校验钉扎 SHA-256 后静默安装到 `RUNNER_TEMP\inno7`，通过 `SPAWN_ISCC_PATH` 覆盖 runner 预装旧版；将 `release` 环境 Secret `SPAWN_UPDATE_SIGNING_KEY_PEM` 写入 `RUNNER_TEMP` 随机临时文件（禁止回显、执行 `scripts/release.py --build --version <X>` 后在 `finally` 中强制删除），运行隔离 `spawn-test` 安装演练（`scripts/release.py --drill-install --version <X>`），并将 `dist/spawn/` 完整 8 件产物以 7 天留存期上传（`if-no-files-found: error`）。
   - **`publish`（`ubuntu-24.04`，全文件唯一 `contents: write` 任务）**：仅在 `vars.SPAWN_RELEASE_PUBLISH == '1'` 且（定时触发或手动勾选 `inputs.publish == true`）时执行；下载产物后先完成 `sha256sum -c SHA256SUMS`、`VERSION.txt`（`channel=release` 与版本号匹配）校验及 `openssl dgst -sha256 -verify`（使用仓内 `installer/update-signing-public.pem` 复核 `update-windows-x64.json.sig`），再以 `gh release create v<X> ... --draft --target <github.sha>` 创建草稿 Release（上传 6 个可下载资产 + `SHA256SUMS`，以 `notes.md` 生成发布说明），核验草稿全部 7 个资产非空存在后调用 `gh release edit v<X> --draft=false --latest` 转正为最新正式版。
3. **与保守远端授权纪律的衔接**：
   - `SPAWN_UPDATE_SIGNING_KEY_PEM`（`ACT-009`）与 `SPAWN_RELEASE_PUBLISH=1`（`ACT-010`）统一在 [USER-ACTIONS.md](../../USER-ACTIONS.md) 登记并由仓库所有者决定何时启用；移除或修改 `SPAWN_RELEASE_PUBLISH` 即可随时停止自动发布，而不影响手动 dry-run 演练与 `check` / `build` 校验。
   - 任何 Agent 在本地或阶段分支开发期间**严禁**执行 `git push`、打 `v*` 标签、调用 `gh release` 或修改远端 `release` 环境/Secret/仓库变量/Actions 白名单。
