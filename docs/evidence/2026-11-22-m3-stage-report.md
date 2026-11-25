# SPAWN M3 阶段执行与验证报告（Stage Report — M3 Single-Player Trainer）

> **阶段代号**：`M3`（计划版本 `1.1` — 单机外部修改器安全管家 + 启动前轻量存档快照 + 正式版每周发布与签名自动更新）
> **执行日期**：2026-11-21 ~ 2026-11-22（阶段启动、调研回收、整阶段定稿、`M3-W00` ~ `M3-W12` 完整交付与集成验证）
> **所在分支**：`feat/m3-trainer`（唯一父提交锁定 `main` @ `5bcd04c26372602d89d5d4e9eb541cec8b526193`，对应已合并 [PR #2](https://github.com/chialecode/spawn/pull/2)）
> **三维阶段结论**（按 [REVIEW.md §3](../../REVIEW.md) 分离报告）：
> 1. **实现与自检状态**：`M3-W00` ~ `M3-W12` 全部工作包（含 `M3-W00` ~ `M3-W09` 单机修改器与启动前快照、`#16` 顶栏精简、独立 `修改器` 侧栏、FLiNG 403/Challenge 误报修复，以及 `1.1` 增补的 `M3-W10` 正式版自包含打包与签名清单、`M3-W11` 每周发布工作流与结构门禁、`M3-W12` 正式版签名应用内自动更新与设置页 `更新` 分类）均已本地集成交付，`M3-A-00` ~ `M3-A-22` 全部自动化验收用例实跑全绿（.NET **`321 / 321`** + Python 发布单测 **`10 / 10`**，0 警告 0 错误，0 新增 NuGet 包，0 已知漏洞，0 Copyleft 传递依赖）。
> 2. **集中技术审查结论**：前期 `M3-W00` ~ `M3-W09` 及 2026-11-22 体验增强已获 Agent A 集中审查 **`reviewed`**（`F-01` ~ `F-04` 已修复，见 §4）；`1.1` 增补的 `M3-W10` / `M3-W11` / `M3-W12` 已全部通过 `@verifier` 独立分包审查（W12 发现的 2 项生命周期缺陷已闭环复验通过，0 遗留项）；Agent A 对 `W10` ~ `W12` 整阶段审查结论为**需返工**（`F-01` 更新器超时/取消状态卡死与命令异常外泄、`F-02` 发布工作流未限定 `main`、`F-03` `spawn-test` 身份文档未同步），Agent B 已全部修复并收束回唯一阶段提交，经 Agent A 2026-11-22 复审通过，整阶段技术审查结论 **`reviewed`**（0 遗留项，见 §4.2）；每次本地 `amend` 提交后按 `D-32` 自动静默更新本机 `SPAWN Dev` 驻留系统托盘。
> 3. **产品实机验收状态**：`M3-M-01`（`H-002`）、`M3-M-02`（可选安全助手/混合单机实机）、`M3-M-03`（合入 `main` 后 GitHub Actions dry-run 与 `H-005` 正式版应用内更新端到端体验）与 `H-001` 音效试听仍为 **`pending` / `not-run`**（详见 §3 未测边界表）。
> **关联文档**：[项目交付总状态](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/docs/delivery/status.md) · [M3 阶段计划](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/docs/delivery/stage-plan.md) · [M3 阶段验收用例集](../delivery/m3-stage-cases.json) · [需求追踪矩阵](../delivery/traceability.json) · [USER-ACTIONS.md](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md)

---

## 1. 阶段交付成果摘要（`M3-W00` ~ `M3-W12`）

1. **`M2` 远端集成与阶段启动卫生（`ACT-007` & `M3-W00`）**：
   - `M2` 经 [PR #2](https://github.com/chialecode/spawn/pull/2) Squash 合并为 `main` `5bcd04c26372602d89d5d4e9eb541cec8b526193`；完成四分类阶段清理（移除 `m2-stage-cases.json`，推进 `currentStage = M3`）与 7 天冷却依赖盘点（升级 `Google.Protobuf 3.36.2`、`NAudio.Wasapi 2.4.0`、`Microsoft.NET.Test.Sdk 17.14.2`；远端 Dependabot 开放告警 `0`）。
2. **`D-24` 成就音效原始响度与 Schema `v3`（`M3-W01`）**：
   - `Steam 原版同款音效` 读取本机 Steam `desktop_toast_default.wav`（缺失时回退 `deck_ui_achievement_toast.wav`），不做峰值提振，默认音量 `1.0`；数据库迁移 `v3` 一次性幂等提升未改动旧默认 `0.75 → 1.0`。
3. **调研回收与整阶段定稿（`M3-R01` → `D-25` ~ `D-29` / `ADR-0006`）**：
   - 回收 `.local/research/` 调研结论与用户确认的 `M3-Q1` ~ `M3-Q5`，落盘 `D-25` ~ `D-29`、修订 `D-04` / `D-11` 与新增 [ADR-0006](../decisions/0006-m3-external-trainer-manager-elevation-and-ipc.md)，全仓移除 `Reloaded.Memory` 家族与 `SevenZipExtractor` / `SevenZipSharp` / `Keystone`。
4. **契约冻结、Schema `v4`、路径隔离与 Clean Host / Worker 门禁升级（`M3-W02` · `M3-A-01` / `M3-A-02`）**：
   - 锁定 `SharpCompress 0.50.9`、`AngleSharp 1.10.3`、`Microsoft.Windows.CsWin32 0.3.371`（全 MIT，均满 7 天冷却）；新增 `Spawn.Modules.Progress`、`Spawn.TrainerWorker`（`WinExe`、`asInvoker`）、`Spawn.Modules.Progress.UnitTests`、`Spawn.TrainerWorker.UnitTests` 与无窗口进程桩 `Spawn.TrainerWorker.TestStub`。
   - 冻结 `ITrainerBackend`、`ITrainerCatalogService`、`ITrainerRuntimeService`、`ITrainerAudioController`、`IPreTrainerSaveSnapshotService`、`ISensitiveWorkerShutdownHook` 及 `trainer.*` JSON-RPC 契约；落地 SQLite Schema `v3 → v4`（四张修改器/快照表与 `games` 四新列）与 `SpawnPaths` 沙箱/暂存/快照/缓存/管道隔离。
   - 升级 `Spawn.Architecture.GateTests`：`Spawn.Modules.Progress` 纳入 Clean Host `PEReader` 零敏感导入审计；`SharpCompress`/`AngleSharp` 限仅被 `Spawn.Modules.Progress` 引用；`Spawn.TrainerWorker` 强制零跨进程内存读写/远程线程/调试挂起导入且源码零 `PROCESS_VM_*`；全仓 `obj/project.assets.json` 零禁用包。
5. **四层 `Tier 0`、`VerifiedOfflineBypass`、2 秒急停看门狗与 `LOOP-06` 闭环（`M3-W03` · `M3-A-03` ~ `M3-A-05`）**：
   - 扩展 6 层目录/驱动特征、嵌入 MIT `AreWeAntiCheatYet` 离线投影（`src/Spawn.Core/AntiCheat/areweanticheatyet-index.json`）与 Steam `categories` 分类核验（CS2/Valorant 绝不误判 `Tier 0`）；静态 `anti_cheat_tier` 保持不变，仅具备单人模式的 `Tier 2` 混合游戏支持受控 `VerifiedOfflineBypass` 实时零 AC 硬验票。
   - 落地仅在活跃会话期间开启的 2 秒只读急停看门狗、`vgk.sys` 分级/严格模式策略、会话内核驱动卸载确认后的 15 秒冷却；完整闭环 `LOOP-06`（`ISensitiveWorkerShutdownHook` 自动清场 + 沙箱孤儿探针二次复检 + 只读 PE `FileVersionInfo` 改名 CE 驱动与 `QueryDosDeviceW` `CEDRIVER*` 检测，绝不强杀用户自启第三方工具）。
6. **英文标准名/版本指纹、洁净室 FLiNG 发现、安全下载与本地导入（`M3-W04` · `M3-A-06` / `M3-A-07`）**：
   - 解析 Steam ACF `buildid` / `LastUpdated` 与英文标准名瀑布；严格遵守 `robots.txt`（零 `/?s=` 与 `/attachments/` 请求、并发 1、24h/7d 缓存）；`AngleSharp` 原创 DOM 解析热键/附件表/推广过滤与三级版本置信度。
   - 手动 10 跳重定向、禁 HTTPS 降级、同源 `Referer`、`100MB` 截断、Challenge/HTML 即时中止退回浏览器引导；魔数分流、Zip-Slip 拦截、解压前后字节校验、`225/226` 杀软隔离分类、多 EXE 用户自选、原子落盘与 SHA-256 TOCTOU 复核；非 ASCII 沙箱 `GetShortPathNameW` 兜底且保留 `Zone.Identifier`。
7. **`Spawn.TrainerWorker`、倒置跨 IL `NamedPipeJsonRpc`、分级提权与热键代理（`M3-W05` · `M3-A-08` ~ `M3-A-10`）**：
   - Medium IL `Spawn.Host` 托管单实例会话管道服务端（`FirstPipeInstance` + 128-bit 随机后缀 + 同用户 SID Allow / `NetworkSid` Deny），Worker 作为客户端反向连入并双向校验 PID、`0x1000` 路径与 256-bit `LaunchNonce`；`SYNCHRONIZE` 监视父 Host 断连即自毁。
   - 依据修改器 PE 清单与游戏完整性级别判定 Level 1（`CreateProcessW` Medium IL）或 Level 2（每会话 1 次 `ShellExecuteExW runas` UAC）；`TrainerSandboxJob`（`KILL_ON_JOB_CLOSE` ON、禁 BREAKAWAY）+ `PROC_THREAD_ATTRIBUTE_JOB_LIST` 原子入组 + IOCP 进程树清零 + 命令白名单；`MAPVK_VK_TO_VSC_EX` + `KEYEVENTF_EXTENDEDKEY`（填 `wVk`+`wScan`、默认不设 `KEYEVENTF_SCANCODE`）交错 `20 / HoldMs(75) / 20ms` 热键代理。
8. **`A0 + A` 零注入静音（`M3-W06` · `M3-A-11` / `M3-A-12`）**：
   - 27 字节 5.0 秒纯静音 SMF 常量、`SHGetKnownFolderPath(FOLDERID_Documents)` 与 `%LOCALAPPDATA%\FLiNGTrainer` 双目录 INI 幂等 `.spawn_bak` 预写/还原；专用 `ApartmentState.MTA` 线程 Core Audio 严格按 `RegisterSessionNotification → GetSessionEnumerator → GetCount()` 顺序初始化、监听端点热插拔、匹配主/子 PID 与沙箱前缀，默认 `FullMute`，遇 `E_ACCESSDENIED` 经管道发 `trainer.setAudioFallback` 回退 Worker 执行。
   - **本机已实测项**：常规单元测试内已完成真实 `mciSendStringW` 27 字节静音 SMF 播放与重播验证（`initialMode=playing, replayMode=playing`，零可闻输出）；另在 `M3-W06` 手动显式开启 `SPAWN_RUN_UNV02_AUDIO_PROBE=1` 运行 `UNV-02` 诊断探针，实机 COM 互操作通过，观测到 4 个渲染端点且 Microsoft GS Wavetable Synth 音频会话归属当前进程 PID。
9. **启动修改器前轻量存档快照与 PCGamingWiki 规则修复（`M3-W07` · `M3-A-13` / `M3-A-14`）**：
   - 以 `action=parse&redirects=1&prop=wikitext` + `list=search` 兜底修复失效的 `cargoquery` 并补齐 Steam `userdata\<uid>\<AppId>\remote` 规则；`PreTrainerSaveSnapshotService` 支持共享只读前后一致性复检、跳过 ReparsePoint、`HKCU` 确定性 JSON、原子 `manifest.json`、超 `256MB`/排他锁非阻塞跳过、滚动保留、`PreRestoreSafety` 安全备份与游戏运行中禁回滚。
10. **Defender 单目录排除助手、WPF 三栏 UI 集成与 `#16` 顶栏精简（`M3-W08` · `M3-A-15` / `M3-A-16`）**：
    - `DefenderExclusionAssistant` 支持篡改保护预读、Level 2 Worker WMI `MSFT_MpPreference.Add`（零 `powershell.exe`）、强制回读校验与分类降级引导，以及 Smart App Control 透明说明卡。
    - 落地 `成就 | 游戏数据 | 修改器` 三子页签、`修改器与安全沙箱` 第 6 设置分类、`PlacementTarget.Tag` 桥接 `⋯` 菜单、虚拟化热键列表、四态安全围栏、VirusTotal 哈希外链与 `AntiCheatBadgeText` 按 `Tier` 着色（`#10B981` / `#F59E0B` / `#DC2626`）+ `离线无 AC 就绪` 徽标；**同步落实用户指示 `#16`**：移除主面板顶栏右侧导入/扫描胶囊栏，将持久化扫描目录管理、单游戏导入与“立即扫描游戏库”统一收口至 **`设置 → 常规与游戏库`**（保留窗口任意区域拖放导入）。
11. **打包目录隔离、第三方合规物料与文档同步（`M3-W09` · `M3-A-17` / `M3-A-18`）**：
    - `scripts/package.py` 将 `Spawn.TrainerWorker` 独立发布至 `stage_app\Workers\Trainer\`（严禁平铺根目录，内置泄漏断言与 `SHA256SUMS.txt` 登记），复制仓库根目录完整 [THIRD-PARTY-NOTICES.txt](../../THIRD-PARTY-NOTICES.txt) 至打包与安装根目录，`--quit-for-update` 优雅等待 Host/`OverlayHost`/`Workers\Trainer\Spawn.TrainerWorker.exe` 全部退出（依托 `App.xaml.cs` 调用 `ITrainerRuntimeService.StopAllAsync(TrainerShutdownReason.HostShutdownOrUpdate)`，绝不强杀游戏）；`installer/spawn.iss` 显式纳入 `Workers\Trainer\*` 与 `THIRD-PARTY-NOTICES.txt`、保持零 `[UninstallDelete]`，并对原生第三方库（`e_sqlite3.dll`、`libSkiaSharp.dll`）实施编译期/安装期 SHA-256 校验跳过（当文件字节一致且被外部反作弊/安全软件只读占锁时免触发 `ERROR_SHARING_VIOLATION`）。
    - 完成 SPEC-18 全仓长期正本措辞收尾（残留 `gRPC over Named Pipes` → `NamedPipeJsonRpc`、Worker 计划任务静默提权 → 按 `D-11` 分级按需提权且免 UAC 仅限 `LOOP-07`），并同步 `quality-gates.md` §3、`open-questions.md`（`OSS-06`/`OSS-07`/`LOOP-06`）、`USER-ACTIONS.md`（`H-001` 修订 + `H-002` 完整指引）、`m3-stage-cases.json`、`traceability.json` 与 `document-registry.json`。
12. **`M3` 阶段内反馈增强（2026-11-22：独立 `修改器` 侧栏入口与 FLiNG 下载 403/Challenge 误报修复）**：
    - **独立 `修改器` 一级入口与总览中心（`Spawn.Host` + `Spawn.Architecture.GateTests`）**：Column 1 新增第 3 枚已落地非空壳一级入口 **`修改器`**（`PrimaryNavSection.Trainers`，`1.6px`/`2.0px` 线性滑块图标，运行中同步点亮 `8px` 翡翠绿 `#10B981` 圆点）；Column 2 提供刷新按钮、固定 **`全部修改器总览`**、中文 IME 组合态 `Enter` 防误触检索框及按优先级排序的游戏列表（运行中 → 已就绪已绑定 → 可用单机 → 受保护屏蔽）；Column 3 提供 **`全部修改器总览`**（统计摘要、沙箱与 Defender 快捷栏、活跃横幅、`Recycling` 虚拟化已纳管修改器列表与经 `PlacementTarget.Tag` 桥接的 `⋯` 菜单）及复用共享 `TrainerWorkbenchTemplate`、与 `游戏库 → 游戏详情 → 修改器` 实时双向同步的单游戏工作台；`OverlayAndShellHeadlessGateTests` 增补端到端无头门禁测试（`36 / 36`）。
    - **洁净室 FLiNG 发现与下载 403/Challenge 误报修复与链路加固（`Spawn.Modules.Progress/Catalog` + `Spawn.Modules.Progress.UnitTests`）**：移除裸子串 `challenge-platform` 匹配以消除 Cloudflare 被动 JS Detection 遥测脚本（`/cdn-cgi/challenge-platform/scripts/jsd/main.js`）在正常 HTTP `200` 网页触发的 100% 误报，同时保留严格零重试真实挑战/拦截判定（`cf-mitigated: challenge`、`403`/`503`、`<title>` 与主动挑战标记、二进制流返回 HTML）并区分 `Challenge` / `403` / `503` / `HTML` 回退提示；新增标准 RFC `Accept`/`Accept-Language` 请求头（保持诚实非伪装 `User-Agent`）、单次下载作用域隔离 `CookieContainer` 跨手动重定向传递 Cookie、未转义原始 `Location` 头解析、白名单校验后的规范同源 `Referer` 捕获、`archive.flingtrainer.com/files/*` 直接附件合成（不把二进制压缩包当 HTML 拉取），以及词边界限定的 `wand` 推广词过滤（放行 *Wandering Sword* 等合法游戏名）；增补 8 项 100% 离线单测（`96 / 96`）。
    - **静默重装与托盘重启状态契约（`Spawn.Core` + `Spawn.Host` + `scripts/package.py`）**：同步落盘 `<DataRoot>\config\update-restart-state.json`（5 分钟 TTL、`dev-install`/`update` 区分与 `windowOpen` 还原策略）、`TaskbarCreated` 托盘自愈与外壳启动父进程核验（`Spawn.Core.UnitTests` `93 / 93`）。
13. **正式版自包含打包、符号剥离、版本号规划与签名清单（`M3-W10` · `M3-A-19` / `M3-A-20` · `D-30`/`D-32`/`D-33`）**：
    - `scripts/package.py`：`--channel release`（`spawn`/`spawn-test`）默认自包含 `win-x64`（`Spawn.Host`、`Spawn.OverlayHost` 与 `Workers\Trainer\Spawn.TrainerWorker` 三端全部自包含，校验 `coreclr.dll` 且保持 `Workers\Trainer\` 目录物理隔离）；开发版仍依赖框架发布；所有通道递归剥离原生 `*.pdb`（如 `84MB` `libSkiaSharp.pdb`）且开发版保留托管 `Spawn.*.pdb`；正式版剥离全部 `*.pdb` 并另打包 `spawn-symbols-<版本>-x64.zip`（不进安装器）；开发版版本升级为 `<下一正式版本>-dev.<UTC 时间戳>`；`--install-dev` 落地 `SPAWN_DEV_AUTO_INSTALL=0` / `CI=true` 前置跳过守卫且不影响 `spawn-test`/`spawn-dev-test` 隔离演练。
    - `scripts/sign_update.cs`（`.NET 10` 纯 BCL 文件式程序，零外部包）与 `scripts/release.py`：跨平台 `--plan`（按 `NEXT_BASE_VERSION` 主次线读取 `v<主>.<次>.*` 整数补丁号 `+1`，零新 first-parent 提交返回 `release=false`，同步输出标准输出与 `GITHUB_OUTPUT`）；`--build --version <X>` 在 `dist/.release-*` 暂存区生成精确 8 文件（`spawn-setup-<X>-x64.exe`、`spawn-symbols-<X>-x64.zip`、`update-windows-x64.json`、`update-windows-x64.json.sig`、`VERSION.txt`、`THIRD-PARTY-NOTICES.txt`、`notes.md`、LF 行尾 `SHA256SUMS`）并原子替换 `dist/spawn/`，私钥路径从环境变量 `SPAWN_UPDATE_SIGNING_KEY` 读取且全程零回显零日志；`--drill-install --version <X>` 在临时目录执行 `CI=true` 兼容的 `spawn-test` 静默安装/校验/卸载演练。
    - **签名公钥锁定（[ADR-0007](../decisions/0007-weekly-release-and-signed-in-app-update.md)）**：本机生成 ECDSA P-256 密钥对，私钥仅存 `.local/secrets/update-signing/update-signing-private.pem`（严格 `.gitignore` 忽略），公钥入库 `installer/update-signing-public.pem`（后迁至 [release-keys/98ada0750903e1ab.pub.pem](../../release-keys/98ada0750903e1ab.pub.pem)，UTF-8 无 BOM、LF），`DER SPKI SHA-256` = `98ada0750903e1ab461d64fc7a9715e2c3fbce8bcb84e4c868474c9e16dc68f6`，`PEM 字节 SHA-256` = `72fe38c35d5c1d7d016addad2c5579e4fe34ea40050bcd6649bd2cd946694c1e`；`scripts/check_docs.py` 仅放行该公钥 PEM 并校验零 `PRIVATE KEY`，其余 `*.pem` 继续全仓禁止。
    - `installer/spawn.iss` 识别静默安装参数 `/SPAWNRELAUNCH`，仅在静默安装成功且携带该参数时以 `--after-update` 拉起 `{app}\{#DesktopExe}`，不带参数时行为零变化；`scripts/test_release.py`（10 项全离线单测，使用测试内临时密钥）接入 `scripts/quick_gate.py`。
14. **GitHub Actions 每周发布工作流与结构门禁（`M3-W11` · `M3-A-21` · `D-30`）**：
    - 新增 `.github/workflows/release.yml`：仅 `schedule: "0 0 * * 1"` 与 `workflow_dispatch`（`publish` 默认 `false`）触发、顶层 `permissions: {}`、`concurrency: { group: release, cancel-in-progress: false }`；四任务链路 `plan`（`ubuntu-24.04`）→ `check`（`windows-2025`，`CI=true`）→ `build`（`windows-2025`，`environment: release`，钉扎 SHA-256 下载安装 Inno Setup `7.1.1` 至 `RUNNER_TEMP`、`try/finally` 清理临时私钥、`release.py --build` + `--drill-install`、7 天 artifact）→ `publish`（`ubuntu-24.04`，唯一 `contents: write`，受 `vars.SPAWN_RELEASE_PUBLISH == '1'` 门控，`sha256sum -c` + `VERSION.txt` + `openssl` 公钥复核签名 + `--draft` 建稿验齐后转 `--draft=false --latest`）。
    - 新增 `tests/Spawn.Architecture.GateTests/ReleaseWorkflowGateTests.cs`（纯 BCL YAML 解析 + 7 项结构断言）并同步 `.github/actions-policy.json`、`docs/dev-rules/git-workflow.md` 与 `docs/dev-rules/quality-gates.md`。
15. **正式版签名应用内自动更新与设置页 `更新` 分类（`M3-W12` · `M3-A-22` · `D-31`）**：
    - 零新增 NuGet 包、零 SQLite Schema 升级（持久化至 `app_settings`，保留最近 20 条记录与单版本失败去重）：新增 `src/Spawn.Contracts/UpdateContracts.cs` 与 `src/Spawn.Core/Update/**`（编译内嵌 ECDSA P-256 公钥并由 `UpdatePublicKeyParityGateTests` 与 `installer/update-signing-public.pem` 逐字节校验；仅 `SpawnChannel == release` 启用，开发版/源码零网络请求；60s + 24h 周期检查；仅 HTTPS + 白名单主机 + 最多 5 跳手动重定向 + `64KB`/`300MB` 流式上限 + 诚实 `SPAWN-Updater/<版本>` UA；先验 DER 签名再解析 JSON、严禁降级/同版本、校验 `releases/download/v<版本>/` URL 前缀、`.partial` 落盘核对大小与 SHA-256 后原子重命名并裁剪旧缓存）。
    - `Q-002` 空闲安装与 TOCTOU 防护：仅在无受追踪游戏、无活跃修改器会话、无存档快照/回滚/数据清理且主窗口关闭（仅托盘）时自动静默安装；主窗口开启或“仅提醒”模式展示非打扰提示条（`新版本已就绪，重启更新`）与托盘通知；安装前以 `FileShare.Read` 持锁复算 SHA-256 并保持句柄跨越 `RequestQuitForUpdateAsync(UpdateRestartReason.Update)` 与 `ShellExecute`（`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /SPAWNRELAUNCH`，零 UAC）；`App.xaml.cs` 补齐 `--after-update` 窗口/托盘恢复；设置页新增第 7 分类 **`更新`**（开发版显示“开发版由本机打包自动重装”）；`UpdateServiceTests`（31 项全离线单测，含重入自动安装防死锁回归）与 `OverlayAndShellHeadlessGateTests` 全绿。

---

## 2. 质量门禁与供应链核验实跑记录（`stage-plan.md §6`）

| 检查项 | 执行命令 | 状态 | 实跑退出码与精确结果 |
| :--- | :--- | :---: | :--- |
| 文档治理与路径卫生 | `python -B scripts/check_docs.py` | **`passed`** | 退出码 `0`：`61` 篇 Markdown、`6` 份受管 JSON、`49` 项需求、`287` 份文本文件全部通过，零告警；已验证仅放行公钥 `installer/update-signing-public.pem` 且拦截其他 `*.pem` |
| 空白与行尾卫生 | `git diff --check`（含 `--cached`） | **`passed`** | 退出码 `0`：无输出 |
| Release 全量编译 | `dotnet build Spawn.sln -c Release` | **`passed`** | 退出码 `0`：`0 Warning(s)`，`0 Error(s)`（13 个工程全部成功） |
| 一键快速门禁（含离线发布测试与网络绊线） | `python -B scripts/quick_gate.py` | **`passed`** | 退出码 `0`：集成全量跑通（预算 `300s`，含 `check_docs`、`test_release` **`10 / 10`**、`dotnet build` 0 警告、`dotnet test` **`321 / 321`**、`git diff --check`；`F-01` ~ `F-03` 返工后复跑 `42.5s`），离线网络绊线 `http://127.0.0.1:9` 零触发，含 `spawn-dev-test --no-shortcuts --no-relaunch` 隔离安装/卸载、原生 `*.pdb` 剥离与零通道污染校验 |
| 全量测试（分工程明细） | `dotnet test Spawn.sln -c Release --no-build` | **`passed`** | 退出码 `0`：合计 **`321 / 321`** —— `Spawn.Architecture.GateTests` **`46/46`**（Clean Host IAT + 无头 UI（含 `F-01` 更新命令异常不外泄用例）+ `ReleaseWorkflowGateTests` 7 项（含 `F-02` `main` 守卫断言）+ `UpdatePublicKeyParityGateTests`）、`Spawn.Core.UnitTests` **`128/128`**（含 `UpdateServiceTests` 34 项（其中 `F-01` 超时/取消回归 3 项）与 `SingleInstanceAndPackageInstallerTests`）、`Spawn.Modules.Progress.UnitTests` **`98/98`**、`Spawn.TrainerWorker.UnitTests` **`49/49`**；Python `scripts/test_release.py` **`10/10`** |
| NuGet 传递依赖漏洞审计 | `dotnet list Spawn.sln package --vulnerable --include-transitive` | **`passed`** | 退出码 `0`：`Directory.Packages.props` 零新增包；全解决方案 `13` 个项目均为 **`0` 已知漏洞** |
| 传递依赖许可证洁净审计 | 扫描全仓 `13` 份 `obj/project.assets.json` + NuGet `.nuspec` | **`passed`** | 退出码 `0`：共审计 `49` 个唯一 NuGet 传递包（`MIT` 33、`Apache-2.0` 11、`BSD-3-Clause` 1、Microsoft SDK/Win32Metadata 3、`xunit` Apache-2.0 1），**`0` `GPL` / `AGPL` / `LGPL` 包**，零禁用包 |
| 正式版发布规划、临时密钥构建与双端验签 | `release.py --plan` + 临时密钥 `release.py --build --version 0.1.1` + `.NET`/`openssl` 验签 | **`passed`** | `--plan` 返回 `release=true`、`version=0.1.1`、`previous_tag=`；临时密钥构建生成精确 8 文件（LF-only `SHA256SUMS` 6 项资产与 `VERSION.txt`），`.NET` (`Verified OK`, 70B DER) 与 `openssl dgst -sha256 -verify` (`Verified OK`) 双端通过，日志与产物零私钥路径/内容泄漏，演练后已清除临时私钥与临时 `dist/spawn` |
| 正式版 `spawn-test` 隔离安装演练（`CI=true`） | `python -B scripts/release.py --drill-install --version 0.1.1` | **`passed`** | 退出码 `0`（在 `CI=true` 下实跑通过）：临时目录静默安装/校验/卸载全绿，`coreclr.dll` 在根目录与 `Workers\Trainer\` 均就位、`pdbCountInstalled=0`、`symbolsCount=10`、`workerIsolatedVerified=true`、`userDataPreserved=true` |
| 集成实测安装包与符号包体积 | 集成构建实测记录（`D-33`） | **`passed`** | 正式版自包含安装器 `spawn-setup-0.1.1-x64.exe`：**`80,133,293` 字节（`76.42 MiB`）**（`lzma2/max`，三可执行文件全自包含、零 `*.pdb`，远低于更新器 `300MB` 上限）；正式版符号包 `spawn-symbols-0.1.1-x64.zip`：**`680,431` 字节（`664.5 KiB`）**（含 10 份托管 `*.pdb`，零原生 `*.pdb`）；开发版依赖框架安装器 `spawn-dev-setup-0.1.1-dev.*-x64.exe`：**`12,578,074` 字节（`12.00 MiB`）**（已剥离 `84MB` `libSkiaSharp.pdb`，保留 10 份托管 `*.pdb`） |
| 27B 静音 SMF 与 `UNV-02` 端点诊断探针 | `Audio` 单测 + `SPAWN_RUN_UNV02_AUDIO_PROBE=1`（`M3-W06` 实测） | **`passed`** | 真实 `mciSendStringW` 播放/重播 27B 静音 SMF 通过（`initialMode=playing, replayMode=playing`，无声）；`UNV-02` 本机实机探针观测到 `4` 个渲染端点且 GS Wavetable Synth 会话归属当前 PID |
| 本机 `SPAWN Dev` 静默更新（`D-32`） | `python -B scripts/package.py --install-dev` | **`passed`** | 最终单阶段 `amend` 提交后执行，静默升级 `%LOCALAPPDATA%\Programs\spawn-dev`（`installed=true`、`relaunched=true`、`background=true`、`viaShell=true`、`inJob=false`），校验 `Workers\Trainer\Spawn.TrainerWorker.exe` 与 `THIRD-PARTY-NOTICES.txt` 就位且内嵌 Commit SHA 与最终 `HEAD` 一致；回执落盘 `.local/reports/m3/install-dev.json` |

### 2.1 发布流水线工具链与 GitHub Actions 7 天冷却核验（核验日 `2026-11-22`，截止线 `2026-11-15`）

- **Inno Setup `7.1.1`（`jrsoftware/issrc` `is-7_1_1`）**：官方直连 `https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe`（无自托管镜像），发布时间 `2026-10-28T19:27:03Z`（满 25 天冷却），大小 `14,336,568` 字节，SHA-256 `8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0`（经 `gh api` 与官方源交叉核对）。

| GitHub Action | 版本标签 | 40 位钉扎 Commit SHA | 发布时间（UTC） | 7 天冷却结论 |
| :--- | :---: | :--- | :---: | :---: |
| `actions/checkout` | `v7.0.1` | `3d3c42e5aac5ba805825da76410c181273ba90b1` | `2026-07-20T15:10:05Z` | 通过（125 天，与 `ci.yml` 一致） |
| `actions/setup-dotnet` | `v5.4.0` | `26b0ec14cb23fa6904739307f278c14f94c95bf1` | `2026-06-26T02:40:37Z` | 通过（149 天，与 `ci.yml` 一致） |
| `actions/upload-artifact` | `v7.0.2` | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` | `2026-10-07T10:08:14Z` | 通过（46 天） |
| `actions/download-artifact` | `v8.0.2` | `9000827ccba6bdab643e8b6fd33ac0654aef8333` | `2026-10-07T10:09:51Z` | 通过（46 天） |

---

## 3. 诚实 `not-run` 边界清单

严格遵循诚实证据原则，以下依赖真实外部修改器二进制、高完整性级别交互式 UAC、真实 Defender 策略写入、Smart App Control 系统配置或人工听觉/操作体验的项目**不在自动化门禁中伪造通过，统一如实标记为 `not-run`**：

| 项目 / 编号 | 当前状态 | 未自动化原因与收敛途径 |
| :--- | :---: | :--- |
| **`OSS-07` ① (`UNV-01`)**：新版/经典 FLiNG 冷启动读预写 `OnLoadMusic` INI 生效与 `=0` 兼容性 | **`not-run`** | 仓库零真实修改器二进制；待用户按 [USER-ACTIONS.md](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md) `H-002` 第 3 步实机反馈 |
| **`OSS-07` ③ (`UNV-03`)**：Medium IL `Spawn.Host` 对真实 High IL 修改器会话调用 `ISimpleAudioVolume::SetMute` 返回值 | **`not-run`** | 需真实 High IL 修改器会话（自动化门禁不触发 UAC）；代码已实装遇 `E_ACCESSDENIED` 经管道回退 Worker 执行兜底并由 `M3-A-12` 验证状态机 |
| **`OSS-07` ④ (`UNV-04`)**：闭源 FLiNG 热键检测 API 与默认 `75ms` 按压周期在真实修改器上的触发率 | **`not-run`** | 自动化测试仅使用可注入发送器断言 `INPUT` 序列与时序（严禁真实 `SendInput`）；待 `H-002` 第 4 步实机核验 |
| **`OSS-07` ⑤ (`UNV-05`)**：FLiNG 独立 EXE 的 Authenticode 数字签名实机分布 | **`not-run`** | 依赖真实外部修改器样本；待 `H-002` 第 2 步核验 |
| **真实 UAC 提权弹窗（Level 2）** | **`not-run`** | 自动化测试严禁弹出系统 UAC 对话框（`NFR-04`）；提权级别判定逻辑已由 `M3-A-10` 覆盖，真实 UAC 交互待 `H-002` 第 5 步 |
| **真实 Defender WMI 写入与篡改保护拦截（`M3-M-02`）** | **`not-run`** | 自动化测试严禁改写宿主机安全软件配置，仅用注入式 WMI 抽象验证 `M3-A-15`；真实写入与回读待 `H-002` 第 7 步可选实机 |
| **真实 Windows 11 Smart App Control 拦截（`M3-M-02`）** | **`not-run`** | 取决于宿主机系统 SAC 强制模式配置；透明引导卡逻辑已由 `M3-A-15`/`M3-A-16` 验证，实机触发待 `H-002` 第 7 步 |
| **混合单机 `VerifiedOfflineBypass` 真实游戏实机（`M3-M-02`）** | **`not-run`** | 取决于本机是否安装具备单人模式的 `Tier 2` 混合游戏（如《艾尔登法环》）；逻辑与看门狗已由 `M3-A-04`/`M3-A-16` 验证，实机待 `H-002` 第 7 步 |
| **`M3-M-01`（`H-002` 全流程人工体验）与 `H-001` 音效原版一致性试听** | **`not-run`** | 自动化测试严禁外放可闻声音或操作真实玩家游戏；待 `--install-dev` 更新本机 `SPAWN Dev` 后由用户随时免命令行体验 |
| **GitHub Actions 每周发布工作流真实运行（`M3-M-03` dry-run）** | **`not-run`** | 仅在 `M3` 合入 `main` 且完成 `ACT-009`（`release` 环境 `SPAWN_UPDATE_SIGNING_KEY_PEM` Secret 与远端 Actions 白名单补充）后可执行 `workflow_dispatch (publish=false)` 实跑；当前仅在本地完成 `ReleaseWorkflowGateTests` 7 项结构审计、临时密钥 `release.py --build` 与 `CI=true` `spawn-test` 演练 |
| **真实 GitHub Release 公开资产发布（`ACT-010`）** | **`not-run`** | 保守远端边界严禁擅自发布 Tag/Release；需合入 `main` 并由用户开启 `vars.SPAWN_RELEASE_PUBLISH=1` 后触发 |
| **正式版应用内自动更新端到端实机（`M3-M-03` / `H-005`）** | **`not-run`** | 依赖至少两个已发布的正式版 GitHub Release（跨版本下载与 `/SPAWNRELAUNCH` 无 .NET 换机验证）；离线校验链、TOCTOU 锁控、空闲判定与 UI 状态转换均已由 `M3-A-22`（`UpdateServiceTests` 34 项 + 无头 UI 门禁）覆盖 |

---

## 4. Agent A 集中技术审查记录（2026-11-22）

审查对象：`feat/m3-trainer` 阶段提交（父提交 `5bcd04c26372602d89d5d4e9eb541cec8b526193`），直接核查 `git diff`、`Spawn.sln` 引用链、Clean Host 侧全部 P/Invoke 清单、作业对象标志、跨 IL 管道 ACL 与握手、快照/回滚删除路径、FLiNG 请求合规与下载 Challenge 判定、`THIRD-PARTY-NOTICES.txt` 及测试数据目录隔离。

| 编号 | 严重度 | 触发条件与实际表现 vs 契约预期 | 位置 | 处置与验证 |
| :--- | :---: | :--- | :--- | :--- |
| `F-01` | `P1` | 沙箱根迁移不要求目标为空且复制后不校验即递归删除旧根全部子目录：用户若把已有个人目录设为自定义沙箱根，再“恢复默认”时其个人子目录会被搬入 SPAWN 数据目录并从原位置删除，ReparsePoint 文件直接丢失，违反非侵入可逆红线 | `src/Spawn.Modules.Progress/Security/DefenderExclusionAssistant.cs` `MigrateSandboxContentsAsync` | 已修复：用户目标须为空目录（忽略 `.staging`）；逐目录 `overwrite: false` 复制，文件数与总字节一致且源树零 ReparsePoint 才删除旧副本，否则原样保留并在提示中列出；新增 2 项回归用例 |
| `F-02` | `P2` | Worker 启动前 SHA-256 复核后即关闭文件句柄，至 `CreateProcessW` 之间存在可替换 EXE 的竞态窗口（Level 2 下等于把替换后的文件提权运行） | `src/Spawn.TrainerWorker/TrainerLaunchValidator.cs`、`TrainerWorkerApplication.cs` | 已修复：`OpenVerifiedImageLock` 以 `FileShare.Read` 打开并经同一句柄复算哈希，持有至进程创建完成；新增回归用例验证持锁期间写入/改名被拒且仍可原子启动 |
| `F-03` | `P2` | `AntiCheatGuardService` 的沙箱根提供器只返回默认根，用户设置自定义沙箱根后，守卫的启动前高危进程探测漏扫该目录（`LOOP-06` 钩子已正确解析，属纵深防御缺口） | `src/Spawn.Core/SpawnCoreServiceCollectionExtensions.cs` | 已修复：`ResolveTrainerSandboxRoots` 同时返回默认根与当前自定义根；新增回归用例 |
| `F-04` | `P3` | 阶段计划头部仍引用早期阶段提交短哈希 `cc85e1e` | `docs/delivery/stage-plan.md` | 已修正措辞 |

复验：`python -B scripts/quick_gate.py` 退出码 `0`（`45.14s`，`277 / 277 passed`，`0` 警告）；`python -B scripts/check_docs.py` 与 `git diff --check` 退出码 `0`。

### 4.1 `M3 v1.1`（`M3-W10` ~ `M3-W12`）Swarm 独立分包审查与整合记录（2026-11-22）

- **`M3-W10`（打包与签名发布脚本）**：`@verifier` 独立审查 **PASS（0 发现项）**，确认仅 10 份授权文件、零新增 NuGet 包、`.local/secrets/update-signing/` 严格忽略、`installer/update-signing-public.pem` 公钥与私钥逐字节匹配且无私钥泄露、`SHA256SUMS` LF 行尾与 `CI=true` 演练全绿。
- **`M3-W11`（每周发布工作流与门禁）**：`@verifier` 独立审查 **PASS（0 发现项）**，经 `gh api` 独立核验 Inno Setup `7.1.1` SHA-256 与 4 项 Action 40 位 Commit SHA 均满 7 天冷却，`ReleaseWorkflowGateTests` `7 / 7` 全绿。
- **`M3-W12`（签名应用内自动更新）**：`@verifier` 首轮审查检出 2 项生命周期缺陷（`F-01` P0 后台自动安装重入 `StopBackgroundSchedulerAsync` 导致 5s 死锁与取消后跳过 `CompleteDeferredShutdown`；`F-02` P1 提早释放 `_singleInstance` 导致启动失败回滚后失去单实例互斥锁），`@updater` 当轮闭环修复并新增 3 场景重入回归测试后，`@verifier` 复审 **PASS（0 遗留项）**。
- **整阶段集成自检结论**：① 实现与自检 **完成**（返工后 `321 / 321` .NET + `10 / 10` Python，0 警告 0 漏洞 0 Copyleft）；② 分包技术审查已全绿，Agent A 整阶段审查 `F-01` ~ `F-03` 已返工闭环并经 Agent A 复审 **`reviewed`**（§4.2）；③ 产品实机验收 `M3-M-01` / `M3-M-02` / `M3-M-03` / `H-001` / `H-005` 如实保持 **`pending` / `not-run`**。远端推送与 PR（[USER-ACTIONS.md](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md) `ACT-008`）已提交用户明确授权，未获授权前不推送。

### 4.2 Agent A `M3 v1.1`（`M3-W10` ~ `M3-W12`）整阶段集中审查记录与返工闭环（2026-11-22）

审查对象：`feat/m3-trainer` 阶段提交 `00bda595516110b40ea0ee1356544f8e237d4bad`（父提交 `5bcd04c26372602d89d5d4e9eb541cec8b526193`）。审查结论：**需返工**（3 项），由 Agent B 当轮连续修复并收束回唯一阶段提交，Agent A 复审结论 **`reviewed`**（见本节末复审记录）。

| 编号 | 严重度 | 触发条件与实际表现 vs 契约预期 | 位置 | 处置与验证 |
| :--- | :---: | :--- | :--- | :--- |
| `F-01` | `P1` | `W12` 更新器网络超时：内部超时令牌触发后，`SendWithManualRedirectsAsync` 中按已链接令牌过滤的 `TaskCanceledException` 分支永远不生效，读流/写盘阶段也未转换，超时以裸 `OperationCanceledException` 逃出，`UpdateService` 状态卡在 `Checking` / `Downloading`；`UpdateViewModel` 命令未捕获异常，手动检查时异常可抛到 Dispatcher 致宿主崩溃 | `src/Spawn.Core/Update/UpdateHttpFetcher.cs`、`UpdateService.cs`、`src/Spawn.Host/ViewModels/UpdateViewModel.cs` | 已修复：① `DownloadBytesAsync` / `DownloadFileAsync` 覆盖发请求、读流、写盘全过程，内部超时触发且调用方令牌未取消时统一转为 `UpdateNetworkException`（“更新请求超时，已静默顺延。”），`.partial` 照常清理；调用方取消仍抛 `OperationCanceledException`；内容流 `HttpIOException` 同步转为网络异常；`SendWithManualRedirectsAsync` 改为“本令牌已取消则原样抛出交由入口分类，非本令牌的 `TaskCanceledException` 视为超时”。② `CheckForUpdatesAsync` 兜底：调用方取消时把 `Checking` / `Downloading` 恢复为 `ReadyToInstall`（有待安装更新）或 `Idle`，清空下载进度并发布快照后重新抛出；其它意外异常走 `HandleCheckFailureAsync`（持久化失败时仍在内存落定 `Error` / `ReadyToInstall`）；迟到的 `Progress<T>` 回调不再把已落定状态改回 `Downloading`。③ 两个命令捕获异常并经 `StatusFeedbackRequested` 反馈。新增 4 项离线回归用例（`UpdateServiceTests` `F01_*` 3 项 + 无头 `F01_UpdateViewModelCommands_*` 1 项），已验证在修复前源码上全部失败、修复后全部通过 |
| `F-02` | `P1` | `W11` 发布工作流可从任意分支手动触发：`plan` / `publish` 无分支限制，非 `main` 分支 `workflow_dispatch` 可构建并（开关打开时）发布、取用签名私钥 | `.github/workflows/release.yml`、`tests/Spawn.Architecture.GateTests/ReleaseWorkflowGateTests.cs`、`USER-ACTIONS.md` `ACT-009` | 已修复：`plan` 增加 `if: github.ref == 'refs/heads/main'`（`check` / `build` / `publish` 随之跳过），`publish` 的 `if` 显式前置同一守卫，其余权限与 SHA 钉扎不变；门禁新增断言 `plan` 守卫精确匹配、`publish` 以守卫开头且其后无顶层 `||`（已验证对旧工作流失败）；`ACT-009` 增加 `release` 环境 Deployment branches and tags → Selected branches 仅 `main` 一步并写入代配授权口径；同步 [ADR-0007 §3.2](../decisions/0007-weekly-release-and-signed-in-app-update.md) 与 [阶段计划 §4.10](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/docs/delivery/stage-plan.md) |
| `F-03` | `P3` | 文档未同步 `W10` 引入的 `spawn-test` 正式通道演练身份：`AGENTS.md` 红线仍写“安装演练仅限 `spawn-dev-test`”，数据隔离表缺 `spawn-test` | `AGENTS.md`、`docs/dev-rules/data-and-security.md` | 已修正：红线改为“开发通道演练用 `spawn-dev-test`、正式通道自包含发布演练用 `spawn-test`，二者都只允许在临时目录运行”，其它约束不变；安装目录表 `test` 行补 `spawn-test`（`%TEMP%` 临时目录，正式通道自包含演练专用） |

复验（返工后实跑）：`python -B scripts/quick_gate.py` 退出码 `0`（`42.5s`，.NET **`321 / 321`** + `test_release` **`10 / 10`**，`0` 警告 `0` 错误，离线网络绊线零触发）；`python -B scripts/check_docs.py` 与 `git diff --check` 退出码 `0`。本轮未改动打包脚本、NuGet 依赖与安装器，`release.py --drill-install`、漏洞与许可证审计沿用 §2 既有结果，未重跑（`not-run`）。

**Agent A 复审（2026-11-22）**：审查对象 `a61cab88fa8e0d521ac6b7bc95ce2dae108db790`（父提交 `5bcd04c26372602d89d5d4e9eb541cec8b526193`，单条有效 DCO），对照返工前提交 `00bda595516110b40ea0ee1356544f8e237d4bad` 逐项核查 diff。结论：**`reviewed`，0 遗留项**。① `F-01`：`UpdateHttpFetcher` 两个入口以 `IsInternalTimeout` 区分内部超时与调用方取消，覆盖发请求、读流、写盘与 `FlushAsync`；`.partial` 在超时、`HttpIOException` 与其余异常路径均删除；`SendWithManualRedirectsAsync` 中带过滤的 `OperationCanceledException` 先于 `TaskCanceledException`，本令牌取消原样上抛、与本令牌无关的取消视为超时；`UpdateService` 加锁入口在调用方取消时把 `Checking` / `Downloading` 恢复为 `ReadyToInstall` / `Idle` 并清空进度，其它意外异常以 `CancellationToken.None` 走失败记录、持久化失败时仍在内存落定，`finally` 释放操作锁；`Progress<T>` 晚到回调仅在 `Downloading` 时生效；`UpdateViewModel` 两个命令均不外泄异常。② `F-02`：`plan` 守卫精确为 `github.ref == 'refs/heads/main'`，`check` 依赖 `plan` 输出、`build` 依赖 `check`、`publish` 依赖 `build` 且自身前置同一守卫，非 `main` 触发时整条链跳过；`ACT-009` 第 3 步与第 5 步授权口径、ADR-0007 §3.2、阶段计划 §4.10 与 `M3-A-21` 预期一致。③ `F-03`：`AGENTS.md` 红线与数据隔离表 `test` 行已同步 `spawn-test`，全仓无残留旧口径。独立复验：在临时 Git worktree 中把 `UpdateHttpFetcher.cs` / `UpdateService.cs` / `UpdateViewModel.cs` 还原为返工前版本，新增 4 项 `F01_*` 用例全部失败（状态停在 `Checking`、裸 `TaskCanceledException` 外泄、命令异常外泄）；再还原旧 `release.yml`，`ReleaseWorkflowGateTests` 对应用例失败；worktree 已删除。HEAD 上 `python -B scripts/quick_gate.py` 退出码 `0`（`38.6s`，.NET **`321 / 321`** + `test_release` **`10 / 10`**，`0` 警告），`check_docs.py` 与 `git diff --check` 退出码 `0`。非阻塞观察（不需返工）：`App.xaml.cs` 托盘“重启更新”以 `Task.Run` 即发即弃调用 `InstallReadyUpdateAsync`，异常不会使宿主崩溃但也不会提示用户，可在后续阶段补反馈。`release.py --drill-install`、漏洞与许可证审计本轮未重跑（`not-run`，返工未改打包脚本与依赖）；`M3-M-01` / `M3-M-02` / `M3-M-03`、`H-001` / `H-005` 保持 `pending` / `not-run`。
