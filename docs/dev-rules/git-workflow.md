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
| **Squash 合并 PR、删除远端分支、打 Tag / 创建 Release** | **必须分别获得用户明确授权**（用户可在单条回复中一并写明“授权推送、建 PR，CI 通过后 squash 合并”，未写明合并则建完 PR 核对 CI 后停下等待合并指示） |
| **修改仓库设置、Ruleset、Actions 白名单或安全开关** | 仅在用户明确指示时通过 API 执行并同步回写 `.github/*.json` 声明镜像（镜像文件见 [`.github/repository-settings.json`](../../.github/repository-settings.json) 与 [`.github/actions-policy.json`](../../.github/actions-policy.json)） |

- **Actions 白名单与 SHA 固定约束**：远端仅允许运行 `actions/checkout@*` 与 `actions/setup-dotnet@*`，且所有 workflow 引用 Action 必须固定为**完整 40 位 commit SHA** 并附版本号注释（由 [`.github/dependabot.yml`](../../.github/dependabot.yml) 按月带 7 天冷却期巡检更新）；`scripts/check_docs.py` 与 `scripts/quick_gate.py` 坚持纯 Python 3.12+ 标准库零依赖。
- **GitHub API / CLI 低频调用准则**：执行已获授权的远端查询或操作时，使用精准的单次 `gh api` / `gh pr checks` 调用获取所需字段，严禁编写死循环高频轮询脚本、重复提交相同 API 写请求或在遇 `403`/`429` 限流时暴力重试。
