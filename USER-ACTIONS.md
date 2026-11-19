# 用户待办（USER-ACTIONS）

核对日期：2026-11-15。本文件是需要你亲自处理的**环境安装、产品取舍、人工实机检查与远端操作授权**的全仓唯一入口。你可以随时按编号分次回复或按需顺延；未回复不等于默认授权，未执行的外部/实机验证在报告中如实保持 `not-run`。Agent 侧的工程实现与开源适配事项统一维护在 [集中工程事项台账](docs/decisions/open-questions.md)，不会把编码任务推给你处理。

---

## 当前基线进展摘要（2026-11-15 · M1 基础建设阶段）

1. **Git 不可变第一提交与远端仓库配置已就绪**：
   - 本地与远端 `origin/main`（`https://github.com/chialecode/spawn.git`）已完成不可变第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`（仅含 0 字节 `README.md`，带有效 DCO `Signed-off-by` 尾注）。
   - 远端仓库设置已通过 GitHub API 全量对齐并回读确认：关闭公开 Issues / Wiki / Projects / Discussions，仅协作者可建 PR，仅允许 Squash 合并（`PR_TITLE` + `PR_BODY`）且合并后自动删分支；**Private Vulnerability Reporting、Dependabot alerts、Dependabot security updates、Secret scanning 与 push protection 已全部开启生效**（无需你在网页端额外手动勾选安全开关）；Actions 白名单已锁定为 `actions/checkout@*` 与 `actions/setup-dotnet@*`（强制完整 SHA 固定）；`main-protection` 规则集已激活（强制线性历史、PR 解决评论线程、必过状态检查 `docs`）。
2. **`M1` 全套治理、架构与模块规格正本已在本地分支就绪（已锁定 `.NET 10 LTS` 与 `M2` 首发范围）**：
   - 全套治理规则、ADR 0001~0005、顶层架构/数据/IPC 安全契约、S.P.A.W.N. 五大领域模块正式规格书、开发环境清单及 `scripts/check_docs.py` 自动化门禁已在本地分支 `feat/m1-foundation` 完成撰写与审查；目标框架已全面升级锁定为 **`.NET 10 (LTS, net10.0-windows)`**，且 **`M2` 首发编码里程碑** 已按你的优先级定案为 **“桌面主壳 + 游戏库与启动接管 + Steamrip/GOG 单机成就自动捕获与 Overlay 管理闭环”**。
   - 按保守远端推进纪律，**`feat/m1-foundation` 分支当前仅保存在本地，尚未推送至远端，也未创建 PR**，等待你审阅后通过下方 `ACT-002` 授权。

---

## 一、本机开发环境与阶段远端授权（当前优先关注）

### `ACT-001` / done（2026-11-15 已完成）：安装并锁定 `.NET 10 SDK (LTS)`

- **本机环境实测快照（2026-11-15 已核验通过）**：
  - **`.NET 10 SDK` 已就绪**：运行 `dotnet --info` 实测已安装 **`.NET 10 SDK 10.0.404`**（附带 `Microsoft.NETCore.App 10.0.15` 与 `Microsoft.WindowsDesktop.App 10.0.15` 运行时），完全满足 `M2`~`M6` 全工程 `net10.0` / `net10.0-windows10.0.26100.0` 编译、WPF-UI、gRPC Named Pipes、CsWin32 Source Generator 与 xUnit 门禁需求。
  - **其余核心工具链均已就绪**：`Git 2.56.0.windows.4`、`GitHub CLI (gh) 2.103.3`、`Python 3.12.16`、`winget v1.29.380`、`Windows SDK 10.0.26100.0`（含 x64 `signtool.exe`）、`Visual Studio Build Tools 18 (2026)`、`Inno Setup 7`。
  - **后续远期可选驱动说明**：`M5/M6` 阶段涉及的可选虚拟外设驱动（如 `ViGEmBus` 虚拟手柄驱动、`Virtual-Display-Driver` 虚拟显示器）详见 [技术选型与环境清单](docs/engineering/technology-selection.md)，无需提前安装，不影响 `M2`~`M4` 开发验证。

### `ACT-002` / 待你授权（`M1` 阶段分支推送与创建 PR #1）

- **当前本地候选状态**：
  - 本地阶段分支：`feat/m1-foundation`（唯一父提交为 `origin/main` 不可变第一提交 `ad7454c1ab58bb2019e0f0ba6810c5928f8a335e`，单一阶段提交带有效 DCO `Signed-off-by` 尾注；最终完整 40 位提交 SHA 以交付报告与本地 `git rev-parse HEAD` 为准）。
  - 本地已通过检查：`python scripts/check_docs.py` 与 `git diff main..HEAD --check` 零报错通过；本机 `.NET 10 SDK 10.0.404` 已验证就绪，因 `M1` 定位为仓库治理、架构契约与模块规格基线阶段（`Spawn.sln` 于 `M2` 初始化），C# 编译与单元测试在 `M1` 报告中如实标记为 `not-run`。
- **请求你明确授权的具体远端动作**：
  1. 执行 `git push -u origin feat/m1-foundation` 将本地 `M1` 阶段分支推送至远端 `chialecode/spawn`；
  2. 创建 Pull Request（来源 `feat/m1-foundation` → 目标 `main`），标题拟为 `feat(m1): establish governance, architecture, module specs and quality gates`，正文附完整变更清单、门禁结果、`not-run` 边界与 DCO `Signed-off-by` 尾注。
- **如何回复授权**：
  - 若仅授权推送与建 PR，回复：**“授权 ACT-002：推送 feat/m1-foundation 并创建 PR”**；
  - 若希望 GitHub Actions 必过检查（`docs` 与 DCO）绿灯后一并完成合并与清理，可明确回复：**“授权 ACT-002：推送 feat/m1-foundation、创建 PR，CI 通过后 squash 合并入 main 并清理分支”**。
- **未获授权前**：Agent 保持本地分支不动，绝不执行 `git push` 或创建 PR。

---

## 二、后续阶段非阻塞外部可选配置（均带离线默认方案，不阻塞开发）

| 编号 / 状态 | 适用阶段与可选配置说明 | 默认可逆行为（不提供时的工作方式） | 真正受阻范围 |
| :--- | :--- | :--- | :--- |
| `ACT-006` / open（`M2` 可选） | Steamrip / GOG 单机成就增强配置与样本：① 个人免费 **Steam Web API Key**（用于 `GetSchemaForGame` 高配额拉取官方简中成就名称、图标与进度定义）；② 可选准备本地 Steamrip（含常见 Steam 模拟器或无成就/FLT 补丁游戏）与 GOG 离线版单机测试目录 | **不配置亦 100% 可用**：系统默认走内置离线 Schema 缓存、Steam 商店/社区公开成就页与 SteamHunters / Exophase 瀑布流补全隐藏成就中文描述（密钥仅在客户端设置页录入并经 DPAPI 加密存于 `%LOCALAPPDATA%\spawn[-dev]\secrets\`）；自动化门禁使用仓库内置合成黄金样本集测试 15+ 模拟器与 `127.0.0.1:9977` Comet 回环 | 仅影响官方 `GetSchemaForGame` API 直连通道与真实本地游戏目录的一键 `gbe_fork` 激活/弹窗人工联调 |
| `ACT-003` / open（`M2+` 可选） | 在线高清海报与欧美元数据刮削凭据：IGDB（Twitch Developer Client ID / Secret）与 SteamGridDB API Key | 本地确定性指纹识别（`steam_appid.txt`、`goggame-*.info`、DLsite `RJ` 号）及免 Key 公开接口（Bangumi v0、VNDB Kana v2、Steam 商店公开详情）开箱直连可用；提供后仅在客户端设置页录入并经本机 DPAPI 加密存储，切勿贴入聊天或提交 Git | 仅影响 IGDB 深度元数据与 SteamGridDB 透明 Logo / 多尺寸英雄海报的真实在线拉取验收 |
| `ACT-004` / open（`M3+` 可选） | PC ↔ Windows 掌机存档云同步远端存储：Cloudflare R2 / MinIO S3 桶凭据，或坚果云 / 群晖 NAS WebDAV 应用专用密码 | 本地 FastCDC 增量时间机器、外部 FLiNG 修改器 `A0+A` 零报毒静音托管及本地文件系统回环同步测试 100% 离线可用 | 仅影响真实公有云 S3 / WebDAV 端点的在线跨设备同步实测（保持 `not-run` 直至配置） |
| `ACT-005` / open（`M5+` 可选） | 二次元夜间无人值守战报推送通道：Bark Key、Telegram Bot Token 或飞书 / 企业微信 / OneBot Webhook 地址 | 每日代肝排班结束后在本地 `%LOCALAPPDATA%\spawn[-dev]\vault\Reports\<yyyyMM>\` 生成 `SkiaSharp` 暗黑毛玻璃战报长图并可在主界面直接查阅 | 仅影响真实外部 IM 通道的手机端推送到达实测 |
| `Q-001` / open（发布阶段可选） | 未来打包分发时是否采购商业 Windows 代码签名证书（EV/OV Authenticode） | **默认不采购**：安装器对 `Spawn.OverlayHost.exe` 采用本机原地生成一次性 4096-bit 自签证书并即毁私钥激活 `ZBID_UIACCESS`（见 [SECURITY.md](SECURITY.md)）；安装包首次在陌生机器运行若遇 SmartScreen 提示点“更多信息 → 仍要运行”即可 | 不阻塞任何功能开发与小圈子极客自用分发 |

---

## 三、人工实机体验检查预告（`M2+` 交付开发版后按需执行，可顺延）

人工检查仅在对应里程碑交付可运行的 `spawn-dev` 开发版后触发，**不作为集中技术审查收敛或远端 PR 授权的前置条件**：

| 编号 / 状态 | 触发里程碑 | 实机体验关注重点 |
| :--- | :--- | :--- |
| `H-001` / pending（待 `M2` 交付） | `M2` 启动器与单机成就闭环 | Windows 11 DWM Mica 深色视觉与多屏异构 DPI 缩放清晰度、Steamrip/GOG 单机导入刮削与破壳启动时长统计、FLT/无成就游戏一键无损 `gbe_fork` 激活、GOG `9977` 免改 DLL 捕获、以及 144Hz/240Hz 全屏游戏下多主题成就弹窗（PS5 白金、原神卷轴等）零掉帧表现 |
| `H-002` / pending（待 `M3` 交付） | `M3` 存档时间机器与修改器 | Ludusavi + FastCDC 秒级增量快照与防覆盖回滚体验、FLiNG 修改器一键检索下载、启动第一声零电音泄漏与按键提示音音量滑块独立调节体验 |
| `H-003` / pending（待 `M4` 交付） | `M4` Nexus Mod 快照与 `Alt+Z` 面板 | Hardlink + `XxHash3` 配置回收与多维 Profile `<100ms` 切换体感、Pre/Post-Launch HDR/超分环境管线自愈、以及 `Alt+Z` 呼出游戏内交互大面板时 FPS 镜头不乱转与 `ClipCursor` 锁鼠手感 |
| `H-004` / pending（待 `M5/M6` 交付） | `M5/M6` 二游并发代肝与 UE 蓝图 | 夜间多开模拟器与前台端游并发 DAG 排班、睡眠唤醒与防窥黑屏稳定性、`Nodify` 蓝图画布拖拽连线与中文输入法组合态输入、断点单步发光调试及 6 大极客工具箱体验 |

---

## 回写约定

你可以随时直接按编号简短回复（例如：“授权 `ACT-002` 推送并建 PR”、“`ACT-006` 已准备好本地测试游戏目录”）。Agent 收到后会在同轮同步更新本文件状态、[已确认决策](docs/decisions/confirmed-decisions.md) 与 [当前执行状态](docs/delivery/status.md)，完整协作规则见 [沟通协议](docs/dev-rules/communication-protocol.md)。
