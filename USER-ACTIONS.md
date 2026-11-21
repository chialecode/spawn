# 用户待办（USER-ACTIONS）

核对日期：2026-11-21。本文件是需要你亲自处理的**环境准备、产品取舍、人工实机体验与远端操作授权**的全仓唯一入口。你可以随时按编号分次回复或按需顺延；未回复不等于默认授权，未执行的外部/实机验证在报告中如实保持 `not-run`。Agent 侧的工程实现与开源适配事项统一维护在 [集中工程事项台账](docs/decisions/open-questions.md)，不会把编码任务推给你处理。

---

## 当前基线进展摘要（2026-11-20 · M2 首发编码交付与阶段内增强）

1. **`ACT-002` 已执行完毕（`M1` 合入 `main` 并清理分支）**：
   - 按你 2026-11-19 的明确授权，已推送 `feat/m1-foundation` 创建 [PR #1](https://github.com/chialecode/spawn/pull/1)，GitHub Actions `docs` 与 `DCO` 必过检查一次绿灯通过，已 Squash 合并为 `main` **`196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`**，远端与本地 `feat/m1-foundation` 分支均已清理。
2. **`ACT-006` 本地凭据与 3 款真实测试游戏样本已全部就位并适配通过**：
   - Steam Web API Key 存放于忽略路径 `.local/secrets/steam-web-api.json`（开发版与已安装的 `SPAWN Dev` 均自动通过 `dev-workspace.json` 桥接只读脱敏加载，全仓代码与日志零明文泄露）；
   - `.local/games/` 下现已就位 **3 款真实单机测试样本** 并 100% 离线/在线全链路打通：
     - **`LIMBO`**（纯 DRM-Free GOG `1724299977` ↔ Steam `48000`，`webcache.zip` 封面 + 原生 `savegame.txt` 13 项成就）；
     - **`The Stanley Parable Ultra Deluxe`**（Unity Mono + `Plugins\x86_64\steam_api64.cdx` **CODEX `1703340`**）；
     - **`Vampire Survivors`**（Unity IL2CPP + `Plugins\x86_64\steam_api64.rne` **RUNE `1794680`**，220+ 项带图标成就虚拟化列表）。
3. **`M2` 阶段内增强与本机 `SPAWN Dev` 自动安装上机已完成**：
   - 新增 **PCGamingWiki + Unity/Unreal 引擎推断 + 模拟器目录全纳管的 `游戏数据` 管理与可逆回收站清理面板**（`D-22`，支持一键清理存档/离线成就/`HKCU` 注册表并重置 SPAWN 进度回归初见态，支持一键完整恢复，且修复了清理后目录尚未创建时首次重开游戏实时跳杯的监听边界）；
   - 新增 **Inno Setup 7 免 UAC 按用户打包与 `install-dev` 自动安装体系**（`D-23`）：本机 **`SPAWN Dev`**（带醒目琥珀色 `DEV` 角标）已自动静默安装至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`，生成桌面与开始菜单 `"SPAWN Dev"` 快捷方式，并以 `--background` 静默常驻在右下角系统托盘！
   - **按保守远端推进纪律，`feat/m2-launcher-achievement` 唯一阶段提交仅保存在本地，尚未推送至远端，也未创建 PR**，等待你体验或审阅后通过下方 `ACT-007` 授权。
4. **2026-11-21 Agent A 复审通过（`reviewed`）并修复你本轮的两条反馈**：
   - 首轮审查 `F-01`~`F-08` 全部关闭，`P0`/`P1` = 0（明细见 [M2 阶段执行报告](docs/delivery/stage-report.md)）；
   - **奖杯图标只显示一半**（侧边栏“成就中心”、设置页“成就与 Steam API”）：图标路径只画了杯身右半边，已补全；
   - **成就音效不是原版**：Steam 桌面客户端弹成就时播放的是 `desktop_toast_default.wav`，此前 SPAWN 用的是大屏幕/Steam Deck 模式的 `deck_ui_achievement_toast.wav`；现已改为优先桌面版文件，本机 `SPAWN Dev` 已更新。

---

## 一、阶段远端授权与已完成环境记录（当前优先关注）

### `ACT-007` / 待你授权（`M2` 阶段分支推送与创建 PR #2 — A 复审已通过）

- **当前本地候选状态**：
  - 本地阶段分支：`feat/m2-launcher-achievement`（唯一父提交为 `origin/main` `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`，包含三栏启动器、四通道全流派成就引擎、PCGamingWiki 游戏数据管理/可逆清理、3 款真实游戏深度适配及 Inno Setup 7 `SPAWN Dev` 打包体系，收束为单一带有效 DCO `Signed-off-by` 尾注的阶段提交；最终完整 40 位 SHA 以交付报告与 `git rev-parse HEAD` 为准）。
  - 本地已通过门禁：`python -B scripts/quick_gate.py`（串联 `check_docs.py`、`dotnet build Spawn.sln -c Release`、`Spawn.Architecture.GateTests` `PEReader` 零敏感 API 审计、`Spawn.Core.UnitTests` 全流派模拟器 + 3 款真实游戏只读扫描 + 数据清理恢复 + 单实例与 `spawn-dev-test` 隔离安装演练、`git diff --check`）全部静默绿灯通过，详见 [M2 阶段执行报告](docs/delivery/stage-report.md)。
- **请求你明确授权的具体远端动作**：
  1. 执行 `git push -u origin feat/m2-launcher-achievement` 将本地 `M2` 阶段分支推送至远端 `chialecode/spawn`；
  2. 创建 Pull Request #2（来源 `feat/m2-launcher-achievement` → 目标 `main`），标题拟为 `feat(m2): native three-column launcher, anti-cheat guard, offline/GOG achievement engine and overlay`，正文附完整变更清单、门禁测试证据、`not-run` 边界（`H-001` 人工实机体验）与 DCO `Signed-off-by` 尾注。
- **如何回复授权**：
  - 若仅授权推送与建 PR，回复：**“授权 ACT-007：推送 feat/m2-launcher-achievement 并创建 PR”**；
  - 若希望 GitHub Actions 检查（`docs`、`DCO` 与 `dotnet-windows`）全部绿灯后直接完成 Squash 合并与分支清理，可回复：**“授权 ACT-007：推送 feat/m2-launcher-achievement、创建 PR，CI 通过后 squash 合并入 main 并清理分支”**。
- **说明**：下方人工实机体验 `H-001` 与远端集成相互独立，可随时顺延，不阻塞 `ACT-007`；未获你明确回复前 Agent 绝不执行 `git push` 或建 PR。

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
| `ACT-004` / open（`M3+` 可选） | PC ↔ Windows 掌机存档云同步远端存储：Cloudflare R2 / MinIO S3 桶凭据，或坚果云 / 群晖 NAS WebDAV 应用专用密码 | 本地 FastCDC 增量时间机器、外部 FLiNG 修改器 `A0+A` 零报毒静音托管及本地文件系统回环同步测试 100% 离线可用 | 仅影响真实公有云 S3 / WebDAV 端点的在线跨设备同步实测 |
| `ACT-005` / open（`M5+` 可选） | 二次元夜间无人值守战报推送通道：Bark Key、Telegram Bot Token 或飞书 / 企业微信 / OneBot Webhook 地址 | 每日代肝排班结束后在本地 `%LOCALAPPDATA%\spawn[-dev]\vault\Reports\<yyyyMM>\` 生成 `SkiaSharp` 暗黑毛玻璃战报长图并可在主界面直接查阅 | 仅影响真实外部 IM 通道的手机端推送到达实测 |
| `Q-001` / open（发布阶段可选） | 未来打包分发时是否采购商业 Windows 代码签名证书（EV/OV Authenticode） | **默认不采购**：安装器对 `Spawn.OverlayHost.exe` 采用本机原地生成一次性 4096-bit 自签证书并即毁私钥激活 `ZBID_UIACCESS`（见 [SECURITY.md](SECURITY.md)） | 不阻塞任何功能开发与小圈子极客自用分发 |

---

## 三、人工实机体验指南（本机 `SPAWN Dev` 已自动安装就绪，随时双击体验）

人工实机体验不阻塞技术审查收敛或 `ACT-007` 远端授权：

| 编号 / 状态 | 触发里程碑 | 实机体验关注重点 |
| :--- | :--- | :--- |
| **`H-001` / pending（本机 `SPAWN Dev` 已装妥运行中）** | `M2` 三栏启动器、单机成就与游戏数据清理闭环 | 桌面/开始菜单/系统托盘直接唤醒 **`SPAWN Dev`**、一键扫入 `.local\games` 下全部 3 款游戏（`LIMBO` / `The Stanley Parable Ultra Deluxe` / `Vampire Survivors` 220+ 虚拟化成就）、体验 **`游戏数据`** PCGamingWiki/Unity/模拟器路径探测与一键可逆清理恢复、以及实机全屏零抢焦点实时跳杯体验 |
| `H-002` / pending（待 `M3` 交付） | `M3` 存档时间机器与修改器 | Ludusavi + FastCDC 秒级增量快照与防覆盖回滚体验、FLiNG 修改器一键检索下载、启动第一声零电音泄漏与按键提示音音量滑块独立调节体验 |
| `H-003` / pending（待 `M4` 交付） | `M4` Nexus Mod 快照与 `Alt+Z` 面板 | Hardlink + `XxHash3` 配置回收与多维 Profile `<100ms` 切换体感、Pre/Post-Launch HDR/超分环境管线自愈、以及 `Alt+Z` 呼出游戏内交互大面板时 FPS 镜头不乱转与 `ClipCursor` 锁鼠手感 |
| `H-004` / pending（待 `M5/M6` 交付） | `M5/M6` 二游并发代肝与 UE 蓝图 | 夜间多开模拟器与前台端游并发 DAG 排班、睡眠唤醒与防窥黑屏稳定性、`Nodify` 蓝图画布拖拽连线与中文输入法组合态输入、断点单步发光调试及 6 大极客工具箱体验 |

### `H-001` 本机免命令行体验指引（仅读写 `%LOCALAPPDATA%\spawn-dev`）

1. **直接打开已安装的 `SPAWN Dev`**：无需运行任何终端命令——直接**双击右下角系统托盘中的琥珀色 `DEV` 角标 `S` 图标**，或双击桌面 / 开始菜单中的 **`"SPAWN Dev"`** 快捷方式（可在左下角 **设置 → 关于与版本** 核对 `dev` 通道版本号与 Commit 短哈希）。
2. **一键扫入全部 3 款真实游戏**：点击右上角胶囊导入栏的 **`添加 / 扫描`**，确认一次性精准识别出 `LIMBO`（GOG 原生存档）、`The Stanley Parable Ultra Deluxe`（CODEX `1703340`）与 `Vampire Survivors`（RUNE `1794680`），自动提取高清封面并拉取官方简中成就（在《Vampire Survivors》滚动测试 220+ 项带图标成就的流畅虚拟化体验）。
3. **体验 `游戏数据` 管理、一键清理重置与可逆恢复**：在任意游戏详情页切换到 **`游戏数据`** 标签页（或点右上 `⋯ -> 管理 / 清理游戏数据`），查看自动探测出的 `存档数据`（PCGamingWiki + Unity `LocalLow` + 模拟器 `remote`）、`离线成就文件`（`CODEX`/`RUNE` `achievements.ini` 或 `savegame.txt`）、`配置与日志` 与 `HKCU 注册表项`；勾选 **`同时重置 SPAWN 成就进度与游玩时长`** 后点击 **`清理选中数据`** 将游戏一键归零为初见状态，再试一次 **`恢复上次清理`** 验证文件、注册表与进度无损复原。
4. **体验 Steam 原版同款音效、系统默认音频路由与三主题实时跳杯**：在 **设置 → 弹窗外观与音效** 页确认默认已开启 **`Steam 原版同款音效（推荐）`** 与 **`跟随系统默认输出设备`**（原生兼容显示器外接音响与系统声卡无缝切换，亦支持手动下拉指定具体耳机/声卡或导入自定义 `.wav`），点 **“立即弹出测试成就”** 聆听响度提振且零吞音的原版跳杯音效与丝滑动画。

---

## 回写约定

你可以随时直接按编号简短回复（例如：“授权 `ACT-007` 推送、创建 PR，CI 通过后 squash 合并”、“`H-001` 体验通过”）。Agent 收到后会在同轮同步更新本文件状态、[已确认决策](docs/decisions/confirmed-decisions.md) 与 [当前执行状态](docs/delivery/status.md)，完整协作规则见 [沟通协议](docs/dev-rules/communication-protocol.md)。
