# Git 工作流与远端规则

本文件约束 SPAWN 仓库的分支与提交、DCO、远端授权与仓库设置。流程（issue → 小 PR → CI 与独立审查 → 合并）见[开发流程](development-workflow.md#change-loop)。远端仓库为 [`chialecode/spawn`](https://github.com/chialecode/spawn)。

## 1. 第一提交与 `main` 保护

- **第一提交**：`ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（`chore: initialize repository with empty README`，0 字节 `README.md`，带 DCO 签名）永久保留，不改写；已推送到 `main` 的历史一律不 `amend`、`rebase` 或强推。
- **规则集 `main-protection`**（镜像 [`.github/rulesets/main.json`](../../.github/rulesets/main.json)，无绕过名单）：禁止删除与非快进推送，线性历史，`main` 只经 PR 写入，只允许 squash 合并，所有讨论必须解决；必需检查目前是 `docs`（[`.github/workflows/docs.yml`](../../.github/workflows/docs.yml)），另有 Windows `.NET 10` 构建与测试（[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)）。统一为单个必需检查 `repository-quality` 见 [#12](https://github.com/chialecode/spawn/issues/12)。
- **仓库入口**：开启 [Issues](https://github.com/chialecode/spawn/issues)（只允许协作者创建）与 [Discussions](https://github.com/chialecode/spawn/discussions)，关闭 Wiki 与 Projects；PR 仅协作者可创建；允许自动合并与更新分支，合并后删除分支；网页提交要求 DCO 签名；来自 fork 的 PR 运行 workflow 前须维护者批准；安全问题走私密漏洞报告。远端设置的镜像是 [repository-settings.json](../../.github/repository-settings.json)、[rulesets/main.json](../../.github/rulesets/main.json) 与 [actions-policy.json](../../.github/actions-policy.json)，以 GitHub 实际回读为准，改远端设置时同步更新镜像；其中 `issue_creation_policy` 与 `pull_request_creation_policy` 的 REST 接口不返回，用 GraphQL 仓库字段 `issueCreationPolicy` / `pullRequestCreationPolicy` 核对。

<a id="single-stage-commit"></a>
<a id="dco"></a>
## 2. 分支、提交与 DCO

1. **分支**：从最新 `main` 切 `<type>/<issue号>-<简短描述>`（如 `fix/42-tray-restore`）；一个分支只做一个 issue。
2. **提交**：Conventional Commits，每个提交带与作者身份一致的 `Signed-off-by`（`git commit -s`）；推送前可用下面的命令核对。PR 合并时 squash 成一个提交，分支里提交几次都可以；PR 推送后的修复追加新提交，不强推改写。

```powershell
git log --format="%H | %s | %(trailers:key=Signed-off-by,valueonly)" origin/main..HEAD
```

3. **暂存**：先跑 `python -B scripts/quick_gate.py`；用 `git status --short` 核对，再用 `git add -u` 或显式路径暂存，不用 `git add .`、`git add -A` 或 `git clean -fdx`，防止误吞 `.local/`、`bin/`、`obj/`、数据库或他人临时文件。

<a id="remote-auth"></a>
<a id="github-hygiene"></a>
## 3. 远端授权与 GitHub 使用

| 动作 | 规则 |
| :--- | :--- |
| push 分支、建 PR、更新 PR | 长期授权，Agent 直接做 |
| 合并低风险 PR | CI 与独立审查通过后由 Agent 用 `gh pr merge <号> --squash --match-head-commit <头提交>` 合并 |
| 合并[高风险](development-workflow.md#high-risk) PR | 用户合并 |
| 发布正式版、改远端设置与规则集、签名密钥、改写已推送历史 | 用户对具体动作授权（改写时用指定旧 SHA 的 `--force-with-lease`） |

- **Actions**：只允许镜像 [`.github/actions-policy.json`](../../.github/actions-policy.json) 列出的官方 action（`checkout`、`setup-dotnet`、`upload-artifact`、`download-artifact`），远端强制固定完整 40 位 SHA 并注明版本；token 默认只读。
- **Dependabot**（[`.github/dependabot.yml`](../../.github/dependabot.yml)）：版本更新 PR 在[每周维护](development-workflow.md#weekly)时处理，NuGet 兼容更新按低风险，主版本与 Actions 更新按高风险；安全修复随时尽快处理。
- **低频调用**：优先单次 `gh api` / `gh pr view` 获取所需字段，不写高频轮询，遇 `403` / `429` 限流不暴力重试。

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
3. **发布开关与远端配置**：
   - `release` 环境、签名 Secret 与发布开关的配置（与同一作者其他桌面应用统一为 Secret `UPDATE_SIGNING_KEY_PEM`、变量 `RELEASE_PUBLISH=1`，私钥移到仓库外）见 [#9](https://github.com/chialecode/spawn/issues/9)；移除或修改发布开关变量即可随时停止自动发布，不影响手动 dry-run 演练与 `check` / `build` 校验。
   - Agent 不手动打 `v*` 标签、不手动调用 `gh release` 发布正式版；正式版只经本工作流发布。
