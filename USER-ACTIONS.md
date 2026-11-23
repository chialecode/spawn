# 用户待办（USER-ACTIONS）

核对日期：2026-11-22。本文件是需要你亲自处理的**环境准备、产品取舍、人工实机体验与远端操作授权**的全仓唯一入口。你可以随时按编号分次回复或按需顺延；未回复不等于默认授权，未执行的外部/实机验证在报告中如实保持 `not-run`。Agent 侧的工程实现与开源适配事项统一维护在 [集中工程事项台账](docs/decisions/open-questions.md)，不会把编码任务推给你处理。

---

## 当前基线进展摘要（2026-11-22 · M3 单机修改器安全管家 + 每周发布与签名自动更新本地交付已就绪）

1. **`ACT-007` 已执行完毕（`M2` 合入 `main` 并清理分支）**：按你 2026-11-21 的授权推送 `feat/m2-launcher-achievement`、创建 [PR #2](https://github.com/chialecode/spawn/pull/2)，`docs`、`DCO`、`dotnet-windows` 三项检查通过后 Squash 合并为 `main` **`5bcd04c26372602d89d5d4e9eb541cec8b526193`**，远端与本地阶段分支均已清理。
2. **`M3`（`M3-W00` ~ `M3-W12`）全部工作包已本地实现并通过全量自动化门禁（`321 / 321` .NET 测试 + `10 / 10` 发布脚本单测）**：除前期已获 Agent A `reviewed` 的单机修改器安全管家、启动前轻量快照与独立 `修改器` 侧栏体验增强外，2026-11-22 新增的 `M3-W10`（正式版 `win-x64` 自包含打包、全通道剥离原生 `*.pdb`、正式版符号包、版本号规划、纯 BCL ECDSA P-256 DER 签名清单与 `spawn-test` 隔离安装演练）、`M3-W11`（`.github/workflows/release.yml` 四任务每周发布流水线与 `ReleaseWorkflowGateTests` 门禁）与 `M3-W12`（正式版签名应用内自动更新、TOCTOU 锁控、空闲托盘静默安装、设置页 `更新` 分类与托盘/提示条）均已集成并实跑全绿，零新增 NuGet 包、零数据库 Schema 升级；Agent A 整阶段审查提出的 `F-01`（更新器超时卡死）~ `F-03` 已全部返工修复，Agent A 已于 2026-11-22 复审通过（`reviewed`，0 遗留项），现请你按 `ACT-008` 授权远端动作。详细验证证据与 `not-run` 边界见 [M3 阶段执行报告](docs/delivery/stage-report.md)。
3. **成就音效已改为按 Steam 原始音量播放（`D-24`）**：Steam 原版音效不再做峰值提振，音量 `100%` 即与 Steam 客户端一致，默认音量改为 `100%`。
4. **严格遵守保守远端纪律，`feat/m3-trainer` 仅保存在本地**，绝不在未获你针对具体 `ACT-*` 明确授权前执行 `git push` 或创建/更新 PR；本机 `SPAWN Dev` 在收束阶段提交后自动静默更新至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`，随时可按下方 `H-001` / `H-002` 免命令行实机体验。

---

## 一、阶段远端授权与已完成环境记录（当前优先关注）

### `ACT-009` / 待你处理（`M3-W10` 已生成密钥）：备份更新签名私钥、配置 GitHub `release` 环境 Secret 并放行 Artifact Actions

- **为什么需要**：正式版应用内更新只接受用内置公钥能验证的更新清单（[ADR-0007](docs/decisions/0007-weekly-release-and-signed-in-app-update.md)、`D-31`）。私钥已在本机生成，只放在受 `.gitignore` 严格忽略的相对路径 `.local/secrets/update-signing/update-signing-private.pem`（严禁入库）；**私钥一旦丢失，已安装的正式版将无法再自动更新**，只能让用户手动重装。
- **已随仓锁定的公钥与指纹（用于核对）**：
  - 仓库公钥路径：`installer/update-signing-public.pem`（已编译进 `Spawn.Core` 并由 `UpdatePublicKeyParityGateTests` 逐字节校验）
  - 算法：ECDSA P-256（`prime256v1` / `nistP256`）+ SHA-256（ASN.1 DER 签名）
  - **DER SPKI SHA-256 指纹**：`98ada0750903e1ab461d64fc7a9715e2c3fbce8bcb84e4c868474c9e16dc68f6`
  - **PEM 文件字节 SHA-256 指纹**：`72fe38c35d5c1d7d016addad2c5579e4fe34ea40050bcd6649bd2cd946694c1e`
- **需要你做的**：
  1. 把本机 `.local/secrets/update-signing/update-signing-private.pem` 离线备份到你自己的安全位置（如密码管理器或离线 U 盘）；
  2. 在 GitHub 仓库创建名为 `release` 的 Environment，并添加 Secret `SPAWN_UPDATE_SIGNING_KEY_PEM`（内容为私钥 PEM 全文）；
  3. 在 GitHub 仓库 Settings → Environments → `release` → **Deployment branches and tags** 中选择 **“Selected branches”**，只添加 `main` 一条规则（与工作流 `plan` / `publish` 任务的 `refs/heads/main` 守卫形成双保险，防止从其它分支手动触发时取用签名私钥）；
  4. 在 GitHub 仓库 Settings → Actions → General → Selected actions 白名单中补充允许 `actions/upload-artifact@*` 与 `actions/download-artifact@*`（与仓库镜像 `.github/actions-policy.json` 对齐，供 `.github/workflows/release.yml` 上传/下载构建产物使用）。
  5. 你也可以授权 Agent 用 `gh` 代为配置远端环境、部署分支限制、Secret 与 Actions 白名单：回复 **“授权 ACT-009：创建 release 环境并限定仅 main 分支可部署、上传签名私钥 Secret 并同步 Actions 白名单”**（该授权口径包含把 `release` 环境的 Deployment branches and tags 设为 Selected branches 且仅允许 `main`）。
- **不处理的影响**：每周工作流的 `build` 任务会因缺少 Secret 或 Actions 白名单拦截而失败，不会发布任何东西；不影响本地开发。

### `ACT-010` / 待你授权（合入 `main` 后可随时开启）：打开正式版每周自动发布开关

- **是什么**：在仓库变量中设置 `SPAWN_RELEASE_PUBLISH=1`。开启后，每周一 08:00（北京时间）若 `main` 自上次发布以来有新合入，工作流会自动打 `v<版本>` 标签并发布 GitHub Release；没有新合入则跳过。**这是一份长期发布授权**，随时把变量删掉或改成其他值即可停发。
- **建议顺序**：`M3` 合入 `main` → 手动运行一次工作流（`publish=false`，只构建演练，对应 `M3-M-03`）→ 确认无误后开启本开关。
- **如何回复**：**“授权 ACT-010：设置 SPAWN_RELEASE_PUBLISH=1 开启每周自动发布”**（也可以自己在仓库 Settings → Variables 中设置）。

### `ACT-008` / 待你授权（Agent A 已复审通过）：`M3` 阶段分支推送与创建 PR #3

- **当前本地候选状态**：
  - 本地阶段分支：`feat/m3-trainer`（唯一父提交为 `origin/main` `5bcd04c26372602d89d5d4e9eb541cec8b526193`），已完整包含 `M3-W00` ~ `M3-W12`（单机外部修改器安全管家、启动前轻量存档快照、独立 `修改器` 侧栏与 FLiNG 下载误报修复、Agent A 审查修复 `F-01` ~ `F-04`，以及正式版自包含打包与签名清单、每周发布工作流与签名应用内自动更新），收束为单一带有效 DCO `Signed-off-by` 尾注的阶段提交；最终 40 位 SHA 以 `git rev-parse HEAD` 为准。
  - 本地已通过全量集成门禁：`python -B scripts/quick_gate.py`（`321 / 321` .NET 测试 + `10 / 10` 发布脚本测试，`0` 警告 `0` 错误）、`check_docs.py`、`git diff --check`、零漏洞/零 Copyleft 审计与 `spawn-test` 隔离安装演练，详见 [M3 阶段执行报告](docs/delivery/stage-report.md)。
- **请求你明确授权的具体远端动作**：
  1. 执行 `git push -u origin feat/m3-trainer`；
  2. 创建 Pull Request（`feat/m3-trainer` → `main`），标题拟为 `feat(m3): single-player trainer manager, weekly release pipeline and signed in-app auto-updater`，正文附变更清单、门禁证据、`not-run` 边界（`H-001`、`H-002`/`M3-M-01`、`M3-M-02`、`M3-M-03`/`H-005`）与 DCO 尾注。
- **如何回复授权**：
  - 仅推送与建 PR：**“授权 ACT-008：推送 feat/m3-trainer 并创建 PR”**；
  - CI（`docs`、`DCO`、`dotnet-windows`）全绿后直接 Squash 合并并清理分支：**“授权 ACT-008：推送 feat/m3-trainer、创建 PR，CI 通过后 squash 合并入 main 并清理分支”**。
- **说明**：`H-001` / `H-002` / `H-005` 人工体验与远端集成相互独立，可顺延，不阻塞 `ACT-008`；未获你明确回复前 Agent 绝不执行 `git push` 或建 PR。
- **当前状态**：待你授权。Agent A 已于 2026-11-22 复审通过 `F-01` ~ `F-03` 返工（`reviewed`，0 遗留项，见 [阶段报告 §4.2](docs/delivery/stage-report.md)），当前最终提交 SHA：`5490c03abc18a42133fc1eed4e95be4375a9a77d`。

### `ACT-007` / done（2026-11-21 已执行）：`M2` 阶段分支推送、PR #2 合并与分支清理

- 已按你 2026-11-21 的授权完成：推送 `feat/m2-launcher-achievement`（`6bb7b941cbee17f58f548a1735d4190db0165894`）→ 创建 [PR #2](https://github.com/chialecode/spawn/pull/2) → 必过检查 `docs`（7s）、`DCO`、`dotnet-windows`（1m20s）一次通过 → Squash 合并入 `main` **`5bcd04c26372602d89d5d4e9eb541cec8b526193`** → 远端分支由仓库“合并后自动删除”设置清理，本地分支已删除并切入 `M3`。

### `ACT-002` / done（2026-11-19 已执行）：`M1` 阶段分支推送、PR #1 合并与分支清理

- 已按你 2026-11-19 的授权完成：推送 `feat/m1-foundation`（`3aa68c40d60bee815722dc9ddecec70f69a36203`）→ 创建 [PR #1](https://github.com/chialecode/spawn/pull/1) → 必过检查 `docs`（5s）与 `DCO` 一次通过 → Squash 合并入 `main` **`196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`** → 自动清理远端与本地 `feat/m1-foundation` 分支并切入 `M2`。

### `ACT-001` / done（2026-11-15 已完成）：安装并锁定 `.NET 10 SDK (LTS)`

- 本机已核验就绪 `.NET 10 SDK 10.0.404`（含 `Microsoft.NETCore.App 10.0.15` 与 `Microsoft.WindowsDesktop.App 10.0.15`）、`Git 2.56.0.windows.4`、`gh 2.103.3`、`Python 3.12.16`、`Windows SDK 10.0.26100.0`（含 `signtool.exe`）、`Visual Studio Build Tools 2026 (18.11.3)` 与 `Inno Setup 7`。

---

## 二、外部配置与本地测试样本台账

| 编号 / 状态 | 适用阶段与配置说明 | 当前状态 / 默认可逆行为 | 真正受阻范围 |
| :--- | :--- | :--- | :--- |
| `ACT-006` / done（2026-11-20 已就位） | Steam Web API Key 与 `.local/games/` 三款单机测试样本 | **已就位**：`SPAWN Dev` 通过 `%LOCALAPPDATA%\spawn-dev\dev-workspace.json` 自动桥接仓库 `.local/secrets/steam-web-api.json` 与 `.local/games/`（含 `LIMBO`、`The Stanley Parable Ultra Deluxe` CODEX、`Vampire Survivors` RUNE），你后续随时往 `.local/games/` 放入新游戏均可一键扫描识别 | 已关闭；无阻塞 |
| `ACT-003` / open（`M2+` 可选） | 在线多源扩展海报凭据：IGDB（Twitch Client ID / Secret）与 SteamGridDB API Key | 本地离线包资源（GOG `webcache.zip`）与免 Key Steam Store / 公开接口开箱直连可用；提供后仅在设置页录入经 DPAPI 加密存储 | 仅影响 IGDB 深度元数据与 SteamGridDB 社区定制透明 Logo 在线拉取 |
| `ACT-004` / open（`M4+` 可选） | PC ↔ Windows 掌机存档云同步远端存储：Cloudflare R2 / MinIO S3 桶凭据，或坚果云 / 群晖 NAS WebDAV 应用专用密码 | 存档时间机器与云同步已按 `D-25` 顺延至 `M4`；`M3` 修改器管家与开修改器前本地存档备份 100% 离线可用 | 仅影响 `M4` 真实公有云 S3 / WebDAV 端点的在线跨设备同步实测 |
| `ACT-005` / open（`M5+` 可选） | 二次元夜间无人值守战报推送通道：Bark Key、Telegram Bot Token 或飞书 / 企业微信 / OneBot Webhook 地址 | 每日代肝排班结束后在本地 `%LOCALAPPDATA%\spawn[-dev]\vault\Reports\<yyyyMM>\` 生成 `SkiaSharp` 暗黑毛玻璃战报长图并可在主界面直接查阅 | 仅影响真实外部 IM 通道的手机端推送到达实测 |
| `Q-001` / open（发布阶段可选） | 未来打包分发时是否采购商业 Windows 代码签名证书（EV/OV Authenticode） | **默认不采购**：安装器对 `Spawn.OverlayHost.exe` 采用本机原地生成一次性 4096-bit 自签证书并即毁私钥激活 `ZBID_UIACCESS`（见 [SECURITY.md](SECURITY.md)） | 不阻塞任何功能开发与小圈子极客自用分发 |
| `Q-002` / open（`M3-W12` 默认值，可随时改） | 正式版下载好新版本后什么时候安装 | **默认**：自动下载；SPAWN 空闲（无游戏运行、无修改器会话、无存档快照/回滚/清理）且只在托盘时自动静默安装并恢复原状态；主窗口开着时只显示“新版本已就绪，重启更新”提示，等你点击。设置页可改为“仅提醒” | 不阻塞开发；如需改默认（例如永远只提醒，或游戏运行时也允许安装）直接告诉 Agent |

---

## 三、人工实机体验指南（本机 `SPAWN Dev` 已自动安装就绪，随时双击体验）

人工实机体验不阻塞技术审查收敛或后续远端授权：

| 编号 / 状态 | 触发里程碑 | 实机体验关注重点 |
| :--- | :--- | :--- |
| **`H-001` / pending（本机 `SPAWN Dev` 已装妥运行中）** | `M2` 三栏启动器、单机成就与游戏数据清理闭环 | 桌面/开始菜单/系统托盘直接唤醒 **`SPAWN Dev`**、在 **`设置 → 常规与游戏库`** 一键扫入 `.local\games` 下全部 3 款游戏（`LIMBO` / `The Stanley Parable Ultra Deluxe` / `Vampire Survivors` 220+ 虚拟化成就）、体验 **`游戏数据`** PCGamingWiki/Unity/模拟器路径探测与一键可逆清理恢复、以及试听 `D-24` Steam 原版原始响度成就音效与全屏零抢焦点实时跳杯体验 |
| **`H-002` / pending（`M3` 交付可体验）** | `M3` 单机外部修改器安全管家与启动前轻量快照 | 验证最左侧独立 **`修改器`** 侧栏图标与 **`全部修改器总览`**（含运行中绿点、搜索与同步单游戏工作台）；在 **`修改器`** 视图或单机游戏详情页匹配并直接下载 FLiNG 修改器（确认不再误报 403/Challenge）或拖入本地文件、确认首帧零刺耳背景音乐、无小键盘笔记本（`NumLock` 关闭）点击开关触发修改项、观察管理员修改器每会话仅弹 1 次 UAC、验证随游戏退出自动停机、测试启动修改器前自动轻量存档快照与一键回滚；可选：Defender 单目录排除助手、Smart App Control 提示与混合单机 `VerifiedOfflineBypass` 实机（反馈 `OSS-07` ①④⑤） |
| `H-005` / pending（首个正式版发布后） | `M3` 正式版每周发布与应用内更新 | 从 GitHub Release 下载正式版安装包，在没装 `.NET` 的电脑或虚拟机上安装后直接能打开；下一次每周发布后 24 小时内，已安装的正式版在空闲时自动更新并恢复原来的窗口或托盘状态；设置 → `更新` 页能看到版本、检查时间和更新记录 |
| `H-003` / pending（待 `M4` 交付） | `M4` Nexus Mod 快照、存档时间机器与 `Alt+Z` 面板 | Hardlink + `XxHash3` 配置回收与多维 Profile `<100ms` 切换体感、Ludusavi + FastCDC 秒级增量快照与防覆盖回滚、Pre/Post-Launch HDR/超分环境管线自愈、以及 `Alt+Z` 呼出游戏内交互大面板时 FPS 镜头不乱转与 `ClipCursor` 锁鼠手感 |
| `H-004` / pending（待 `M5/M6` 交付） | `M5/M6` 二游并发代肝与 UE 蓝图 | 夜间多开模拟器与前台端游并发 DAG 排班、睡眠唤醒与防窥黑屏稳定性、`Nodify` 蓝图画布拖拽连线与中文输入法组合态输入、断点单步发光调试及 6 大极客工具箱体验 |

### `H-001` 本机免命令行体验指引（仅读写 `%LOCALAPPDATA%\spawn-dev`）

1. **直接打开已安装的 `SPAWN Dev`**：无需运行任何终端命令——直接**双击右下角系统托盘中的琥珀色 `DEV` 角标 `S` 图标**，或双击桌面 / 开始菜单中的 **`"SPAWN Dev"`** 快捷方式（可在左下角 **设置 → 关于与版本** 核对 `dev` 通道版本号与 Commit 短哈希）。
2. **在 `设置 → 常规与游戏库` 一键扫入全部 3 款真实游戏**：顶栏右侧已按你的指示保持极简清爽（移除原有导入/扫描胶囊）；点击左下角 **设置 → 常规与游戏库**，查看已自动桥接的 `.local\games` 扫描目录（也可在此点击 **`添加目录`** 配置其他游戏盘或用下方手动框导入单款游戏，或直接将文件夹拖入主窗口），点击 **`立即扫描游戏库`**，确认一次性精准识别出 `LIMBO`（GOG 原生存档）、`The Stanley Parable Ultra Deluxe`（CODEX `1703340`）与 `Vampire Survivors`（RUNE `1794680`），自动提取高清封面并拉取官方简中成就（在《Vampire Survivors》滚动测试 220+ 项带图标成就的流畅虚拟化体验）。
3. **体验 `游戏数据` 管理、一键清理重置与可逆恢复**：在任意游戏详情页切换到 **`游戏数据`** 标签页（或点右上 `⋯ -> 管理 / 清理游戏数据`），查看自动探测出的 `存档数据`（PCGamingWiki + Unity `LocalLow` + 模拟器 `remote`）、`离线成就文件`（`CODEX`/`RUNE` `achievements.ini` 或 `savegame.txt`）、`配置与日志` 与 `HKCU 注册表项`；勾选 **`同时重置 SPAWN 成就进度与游玩时长`** 后点击 **`清理选中数据`** 将游戏一键归零为初见状态，再试一次 **`恢复上次清理`** 验证文件、注册表与进度无损复原。
4. **体验 Steam 原版同款音效、系统默认音频路由与三主题实时跳杯**：在 **设置 → 弹窗外观与音效** 页确认默认已开启 **`Steam 原版同款音效（推荐）`**（使用本机 Steam 桌面客户端弹成就时实际播放的 `desktop_toast_default.wav`）与 **`跟随系统默认输出设备`**，音量 `100%` 即与 Steam 原始响度一致；点 **“立即弹出测试成就”** 试听，确认与 Steam 原版一致。

### `H-002` 本机免命令行体验指引（`M3` 单机修改器安全管家 · 仅读写 `%LOCALAPPDATA%\spawn-dev`）

1. **打开已更新的 `SPAWN Dev` 并体验独立 `修改器` 侧栏入口与总览中心**：双击托盘琥珀色 `DEV` 图标或桌面 **`"SPAWN Dev"`** 快捷方式，先在 **设置 → 关于与版本** 核对 `dev` 版本号与最新 Commit 短哈希；随后点击最左侧 `52px` 图标栏新增的第 3 枚 **`修改器`** 独立图标，查看 **`全部修改器总览`**（统计摘要、沙箱快捷栏、虚拟化已纳管列表与按优先级排序的游戏侧栏，搜索框支持中文 IME 选词回车不误触发），点击任意 `Tier 0` 单机游戏（如 `Vampire Survivors`、`LIMBO` 或你本地任意单机大作）或从左侧游戏库进入该游戏详情页的 **`修改器`** 子页签，确认两处工作台实时同步且头部安全徽标按 `Tier` 显示翡翠绿 `Tier 0`。
2. **匹配、直下 FLiNG 或本地拖入外部修改器（顺便观察 `OSS-07` ⑤）**：
   - **在线洁净室发现与下载（已修复被动 JSD 误报 403/Challenge）**：点击主按钮 **`匹配修改器`**，按英文标准名检索 FLiNG 主站与归档站候选列表（合法标题如 *Wandering Sword* 均正常保留）；点击用户显式下载按钮，确认普通 FLiNG 修改器与归档站直链 `.rar` 均可正常走通重定向、校验魔数、解压并钉扎本地 SHA-256，不再出现 HTTP 200 误报 403/Challenge（仅当站方确有真实 Cloudflare 人机验证或 403/503 时，界面才会立即零重试中止并提供在浏览器打开页面 + 拖入本地文件的引导，绝不做绕过）；
   - **本地拖放 / 手动导入**：点击 **`导入本地文件`** 或直接将已下载的修改器 `.exe` / `.zip` / `.rar` / `.7z` 拖入 **`修改器`** 页签；若压缩包内含多个 EXE，按界面提示选择主修改器；导入后查看卡片显示的 SHA-256 短哈希、版本匹配置信度与 **数字签名（Authenticode）状态**（供记录 `OSS-07` ⑤：FLiNG 独立 EXE 是未签名还是已签名）。
3. **验证首帧零注入静音与实验性保留提示音（顺便观察 `OSS-07` ①）**：
   - 先到 **设置 → 修改器与安全沙箱** 确认默认音频策略为 **`完全静音（A0 预写 + WASAPI 静音）`** 且已勾选 **`启用 A0 启动前预写（INI 关音乐 + 27B 纯静音 MIDI，自动 .spawn_bak 备份）`**；
   - 回到游戏详情页点击 **`启动修改器`**，确认启动首帧及运行期间**无任何刺耳背景电音**；
   - *(可选核验 `OSS-07` ①)* 停止修改器后在设置中临时切到 **`仅保留提示音（实验性）`**，再次启动同一新版/经典 FLiNG 修改器，观察冷启动时是否已无背景音乐而按键提示音保留；如需彻底撤销 SPAWN 预写的 INI，可点击 **`还原 A0 备份`**。
4. **测试无小键盘 `NumLock OFF` 热键代理（顺便观察 `OSS-07` ④）**：
   - 启动单机游戏并关闭键盘 **`NumLock`**（或使用无小键盘笔记本），在 SPAWN 的 **`修改器`** 子页签中直接点击结构化热键列表（或自由托管模式手动录入的快捷键）对应开关；
   - 确认修改器能够稳定识别 `Num 1`、`Num /`、`Ctrl+Num` 等按键并生效（可随时点击 **`显示原窗口`** 核对修改器本体开关是否同步变绿；如个别老修改器偶发漏键，可在 **设置 → 修改器与安全沙箱** 调整 **`无小键盘热键代理按压时长（50–120 ms）`**，默认 `75 ms`）。
5. **观察分级按需 UAC 与随游戏退出自动停机**：
   - 若导入的修改器未声明 `requireAdministrator` 且目标游戏为普通权限，确认直接以 Level 1（0 次 UAC）启动；若修改器要求管理员权限，确认**每次修改器会话仅弹 1 次 Windows UAC**（Level 2），会话内切换热键不再重复弹窗；
   - 正常退出单机游戏，确认 SPAWN 自动停止修改器并彻底销毁 `Workers\Trainer\Spawn.TrainerWorker.exe` 与沙箱内全部子进程树（无僵尸进程残留）。
6. **测试启动修改器前轻量存档快照与一键回滚**：
   - 确认在 **设置 → 修改器与安全沙箱** 默认已开启 **`启动修改器前自动备份当前存档（轻量快照）`**；
   - 启动修改器后（或点击 **`立即备份当前存档`**），在下方 **`启动修改器前轻量存档快照`** 区域确认已生成带时间戳与体量的快照记录；
   - 进游戏改动存档后退出游戏（游戏运行中回滚按钮会自动禁用防损坏），点击对应快照右侧的回滚按钮，验证 SPAWN 自动先建一份 `PreRestoreSafety` 安全备份再无损回滚存档。
7. **（可选 · `M3-M-02`）安全助手与混合单机实机核验（不具备条件可直接跳过并标 `not-run`）**：
   - **Defender 单目录排除助手**：在 **设置 → 修改器与安全沙箱** 点击 **`检测状态`**，按需点击 **`添加沙箱目录排除（单次 UAC · WMI）`**，确认仅排除唯一沙箱根且强制回读生效（若开启篡改保护或使用第三方杀软，观察是否准确弹出对应手动操作引导与 **`复制沙箱路径`** / **`打开 Defender 排除设置`** 按钮）；
   - **Windows 11 Smart App Control**：若本机开启 SAC 强制模式，观察设置页是否透明提示“Defender 排除项无法绕过 SAC，SPAWN 绝不自动关闭系统保护”；
   - **混合单机 `VerifiedOfflineBypass`**：若本机装有带单人模式的 `Tier 2` 混合游戏（如《艾尔登法环》），确认静态徽标保持警戒红 `Tier 2`、反作弊存活时硬拦、离线关闭反作弊后在可展开面板点击 **`重新探测 AC 状态`** → **`确认已处于离线无 AC 模式（启用放行）`** 出现 **`离线无 AC 就绪`** 徽标后方可启动修改器。

> **反馈方式**：体验后直接回复如“`H-002` 体验通过”即可；若顺手记录了 **`OSS-07` ①④⑤**（① 新版/经典 FLiNG 冷启动读预写 INI 是否去音乐；④ 热键代理 `75ms` 默认触发是否稳定及目标修改器版本；⑤ 修改器 Authenticode 签名状态），一起发回即可，Agent 将同步回写 [open-questions.md](docs/decisions/open-questions.md) 与 [stage-report.md](docs/delivery/stage-report.md)。

---

## 回写约定

你可以随时直接按编号简短回复（例如：“授权 `ACT-007` 推送、创建 PR，CI 通过后 squash 合并”、“`H-001` 体验通过”）。Agent 收到后会在同轮同步更新本文件状态、[已确认决策](docs/decisions/confirmed-decisions.md) 与 [当前执行状态](docs/delivery/status.md)，完整协作规则见 [沟通协议](docs/dev-rules/communication-protocol.md)。
