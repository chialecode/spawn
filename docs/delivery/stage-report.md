# SPAWN M2 阶段执行与验证报告（Stage Report — M2 Launcher, Offline/GOG Achievement, Game Data Cleanup & Dev Installer）

> **阶段代号**：`M2`（计划版本 `1.1` — 三栏原生主壳、深度游戏扫描与数据清理、四通道单机成就闭环与 `SPAWN Dev` 自动安装体系）
> **执行日期**：2026-11-20（首轮审查 `F-01`~`F-08` 返工：2026-11-21）
> **所在分支**：`feat/m2-launcher-achievement`（基线 `main` @ `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`，对应已合并 [PR #1](https://github.com/chialecode/spawn/pull/1)）
> **阶段结论**：**实现与自检：`.NET 10` 全套工程、三栏主界面、PCGamingWiki 游戏数据可逆清理、3 款真实样本深度适配、Inno Setup 7 `SPAWN Dev` 自动安装体系与全套门禁测试本地收敛通过；集中技术审查：首轮 `changes-requested`（`F-01`~`F-08`）已全部返工，2026-11-21 Agent A 复审结论 **`reviewed`**（`P0`/`P1` = 0），同轮用户反馈（奖杯图标半缺、成就音效非桌面原版）已由 A 直接修复；产品实机验收：`H-001` 仍为 `not-run`（待用户体验）。可按 `ACT-007` 申请远端授权**
> **关联文档**：[项目交付总状态](status.md) · [M2 阶段计划](stage-plan.md) · [M2 阶段验收用例集](m2-stage-cases.json) · [需求追踪矩阵](traceability.json) · [USER-ACTIONS.md](../../USER-ACTIONS.md)

---

## 1. 阶段交付成果摘要

围绕 [stage-plan.md](stage-plan.md) 定义的六大工作包（`M2-W01` ~ `M2-W06`），本阶段在 `M1` [PR #1](https://github.com/chialecode/spawn/pull/1) 合入 `main`（`196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`）的基础上，完整交付了首个开箱即用的 `.NET 10 (LTS)` 产品闭环：

1. **`.NET 10` 物理隔离解决方案、版本身份注入与单实例底座（`M2-W01`）**：
   - 建立 `Spawn.sln`、`global.json`（`10.0.404`）、`Directory.Build.props`（支持 `SpawnChannel`/`SpawnIdentity`/`SpawnVersion`/`SpawnCommitSha`/`SpawnApplicationIcon` 注入至 `SpawnBuildInfo`）与 `Directory.Packages.props`；
   - 实现 `Spawn.Core`：`SpawnPaths` 双通道物理隔离 + `%LOCALAPPDATA%\spawn-dev\dev-workspace.json` 开发仓库桥接（使从桌面快捷方式启动的安装版 `SPAWN Dev` 自动定位 `.local\games` 与 `.local\secrets`）、`TrashDir` 回收站目录、SQLite WAL 单写者仓储、`DPAPI` 凭据存储、`0x1000` 安全进程探测、`AntiCheatGuard` 15 秒反作弊残影冷却锁（启动与每次高敏操作前实时探测残留守护进程、跨重启持久化）与 `Tier 2/3` 启动前 CE / `dbk64` 高危拦截、以及 `SingleInstanceCoordinator`（`<Identity>-instance` Mutex + 同用户 SDDL `<Identity>-control` 管道，支持 `--activate` 唤醒与 `--quit-for-update` 无损优雅退出）。
2. **深度嵌套游戏扫描、双引擎启动追踪与 PCGamingWiki 游戏数据管理（`M2-W02` & `M2-W06` · `Spawn.Modules.Launcher`）**：
   - **4 层子目录深度探测**：识别嵌套于 Unity `*_Data\Plugins\x86_64\` 及 UE `ThirdParty\Steamworks` 下的 `steam_api64.cdx`（CODEX）、`steam_api64.rne`（RUNE）、`steam_emu.ini` 与 `app.info`，零误差识别 `.local\games` 下全部 **3 款真实单机样本**（`LIMBO` GOG `1724299977`、`The Stanley Parable Ultra Deluxe` CODEX `1703340`、`Vampire Survivors` RUNE `1794680`）；
   - **无损启动追踪与三态计时**：无 `KILL_ON_JOB_CLOSE` 的 `JobObject` IOCP + `0x1000` 破壳观察窗，1Hz 采样统计 `Active / Background / AFK` 时长；
   - **`GameDataManagementService`（`D-22`）**：融合 PCGamingWiki Cargo API 在线解析 + SQLite 缓存、Unity `AppData\LocalLow\<Company>\<Product>`（兼容 `:` 转 `_` 等 Windows 路径清洗变体）与 `HKCU\Software\<Company>\<Product>` 自动推断、以及全流派模拟器/原生存档目录纳管；内置铁血防误删围栏（严禁删除游戏根目录/EXE/DLL/`*_Data`/`HKLM`；安装目录外仅允许已知数据根下含游戏专属片段的路径，通配符不得扩大为共享父目录），清理前先写 `pending` 撤销清单并逐项增量更新，支持移入 `TrashDir` 可逆备份、联动重置 SPAWN 成就与游玩时长，以及 `RestoreLastCleanupAsync` 一键完整恢复。
3. **四类全覆盖单机成就引擎、多通道并联监控与清理后初见跳杯保障（`M2-W03` · `Spawn.Modules.Achievement` · `D-21`）**：
   - 完整实现 ① 16+ 流派离线 Steam 模拟器与 Steam Binary VDF（兼容嵌套插件目录，支持模拟器与 `NativeSaveAchievementProvider` **多通道并联并集监控**与启动期 `ScanLocalUnlocksAsync` 自动增量同步，攻克部分游戏因 RUNE/CODEX 缺表导致 `GetAchievement` 异常只写 `remote\savedata` 的盲区，且父目录监听 + 500ms 微轮询确保无目录首次新建 `achievements.ini`/`savegame.txt` `<1s` 实时跳杯）；② GOG `127.0.0.1:9977` Comet 兼容守护与 `gameplay.db` 只读退让；③ 无 `Galaxy.dll` 与混合存档规则引擎（内置 `LIMBO` `AES-128-ECB + XOR` 双栈解码及《Vampire Survivors》`1794680` `JsonStringArray` `remote\savedata` 规则）；④ 全 `Tier 0` Steamrip 一键无损升级 `Detanup01/gbe_fork` 补丁（固定 `release-2026_09_27` 官方资产 + SHA-256 校验后按需拉取、按版本隔离缓存，生成全量 `steam_settings\achievements.json` 与 `.spawn_vanilla_bak` `SHA-256` 还原）；配套多源简中 Schema、隐藏描述瀑布流与 `SkiaSharp` Rec.709 灰锁图合成。
4. **三栏无边框主界面、220+ 成就虚拟化、托盘常驻与 `Spawn.OverlayHost`（`M2-W04` · `D-20`）**：
   - `Spawn.Host`：通顶无边框 `WindowChrome` 三栏结构（`52px` 图标栏 + `264px` 侧栏 + 主面板，`Popup` 跨视觉树 `PlacementTarget.Tag` 精准绑定）、`VirtualizingStackPanel` 回收虚拟化丝滑承载《Vampire Survivors》220+ 项带图标成就、游戏详情页 **`成就 | 游戏数据`** 双标签面板、系统托盘 `--background` 静默驻留（含右键快捷测试跳杯）与设置页 **`关于与版本`**；
   - `Spawn.OverlayHost`：分层窗口显式 `Show()`/`Hide()` 驱动像素提交 + 多屏高分屏物理像素 DPI 精确定位 + `WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 零抢焦点，内置 Steam / PS5（100% 自动白金连发）/ Xbox 三套动效；音效引擎默认关联本机官方 Steam 桌面客户端成就音效 `desktop_toast_default.wav`（Steam 桌面模式 `ToastAchievement` 实际播放文件，缺失时回退大屏幕/Deck 模式 `deck_ui_achievement_toast.wav`；`SteamClientSoundLocator` 免入库动态发现 + 内存缓存 + `-10 dBFS -> 0.85f` 温和峰值提振 + `160ms` 不可闻汉宁包络微激励唤醒帧与 `240ms` 尾静音保护帧 + Catmull-Rom 三次插值重采样 + `ExactMixFormatFloatWaveProvider` 原生保留 `WaveFormatExtensible` `dwChannelMask` 声道掩码 + `PlaySoundW` 异常兜底），默认严格跟随 Windows 系统默认输出设备（原生支持显示器 DP/HDMI 外接音箱与系统声卡切换，同时支持在设置页手动指定任意输出声卡）。
5. **双通道图标、Inno Setup 7 按用户打包与 `install-dev` 自动安装闭环（`M2-W05` & `M2-W06` · `D-23`）**：
   - 交付零外部依赖确定性图标生成器 `scripts/generate_icons.py`（按矢量正本 `assets/icon.svg|icon-dev.svg` 的统一扁平规格——`#3C4043` 深灰圆角方底、白色几何 `S`、`#F3A115` 琥珀 `DEV` 角标——1024 超采样栅格化产出 `assets/icon.ico|png` 与 `assets/icon-dev.ico|png`；托盘兜底 `BrandIconGeometry` 共用同一路径数据并由 `BrandIconParityGateTests` 守护）、`installer/spawn.iss`（`PrivilegesRequired=lowest`、独立 `AppId`、无 `[UninstallDelete]`、`--quit-for-update` 无损升级）与 `scripts/package.py --install-dev`，并已在封板后自动在本机静默安装且以 `--background` 拉起最新 `SPAWN Dev`。

---

## 2. 首轮集中审查（`F-01`~`F-08`）返工记录

Agent A 首轮集中审查结论为 `changes-requested`。Agent B 按 [REVIEW.md](../../REVIEW.md) 逐项修复并 amend 回唯一阶段提交（父提交保持 `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`），复审范围仅限下表 `F` 编号及受影响文件：

| 编号 | 严重度 | 修复内容 | 受影响范围 | 验证 |
| :--- | :---: | :--- | :--- | :--- |
| `F-01` | `P1` | ① `TestSandbox` 释放改为 `SqliteConnection.ClearAllPools()` + 终结器回收 + 约 6s 退避重试，清理失败不再使测试失败（改写 `%TEMP%\spawn-tests-leftover-*.txt` 诊断）；② 安装演练子进程设置 `MSBUILDDISABLENODEREUSE=1`，`package.py` 的 `dotnet publish` 统一追加 `-nr:false`；③ 删除依赖真实 Steam/GOG/CDN 的 `Schema_RealLocalGamesThreeTitles_*`，改为全内存 `OfflineSteamApiFakeHandler` 夹具测试；LIMBO 端到端测试固定 `AllowNetworkDownloads = false`；④ `quick_gate.py` 测试步骤启用离线网络绊线（`HTTP(S)_PROXY` → `127.0.0.1:9`） | `tests/Spawn.Core.UnitTests/TestSandbox.cs`、`SingleInstanceAndPackageInstallerTests.cs`、`LimboGbeForkAndSchemaIntegrationTests.cs`、`scripts/package.py`、`scripts/quick_gate.py` | `quick_gate` 连续 3 次全绿；单独 `dotnet test Spawn.sln` 全绿；运行后 `spawn-tests-*` 残留 `0`、leftover 诊断 `0`、`MSBuild.exe` 残留 `0` |
| `F-02` | `P1` | 删除生产代码中 `IsIsolatedOverride` 下返回假 PE 的 `BuildIsolatedTestGbePeShim`；新增 `IGbeForkBinarySource` 注入点，测试改为注入 `InMemoryGbeForkBinarySource` | `Enabler/GbeForkActivatorService.cs`、`Enabler/GbeForkBinarySource.cs`、`AchievementServiceCollectionExtensions.cs` | `GbeFork*` 测试通过；新增离线无缓存返回 `null`、无 SHA-256 清单的缓存 DLL 不被信任的测试 |
| `F-03` | `P1` | 新增只读 `HighRiskEnvironmentProbe`（Toolhelp 进程名 + `K32EnumDeviceDrivers` + SCM 活动驱动服务 `ImagePath` 回退）检出 Cheat Engine 进程、`dbk64.sys`/`dbk32.sys`/`WinRing0x64.sys`/`inpoutx64.sys` 驱动与 SPAWN 高敏 Worker；`IAntiCheatGuardService.InspectHighRiskEnvironment()` 探测异常时失败即阻断；`GameLaunchService` 对 `Tier 2/3` 检出即返回 `HIGH_RISK_ENVIRONMENT_BLOCKED`（不终止用户进程）；`M2-A-03` 恢复“高危拦截”原始验收描述；自动终止自有 Worker 登记 `LOOP-06`（`M3`） | `Spawn.Core/HighRiskEnvironmentProbe.cs`、`AntiCheatGuardService.cs`、`Spawn.Contracts`、`GameLaunchService.cs` | `AntiCheatGuard_Tier2LaunchWithCheatEngineOrDbk64_*`（启动器 0 次启动进程、0 条会话）与名称识别测试通过；新导入经 `PEReader` 门禁通过 |
| `F-04` | `P1` | 守卫构造时及每次 `EnsureHighRiskOperationPermitted` 实时探测残留守护进程，检出即进入“残留监视 → 消失后完整 15s 冷却”流程（冷却中复现则重新计时）；新增 `config\anticheat-guard-state.json` 持久化，重启后恢复未满冷却，受保护会话中途重启按“残留监视 + 完整 15s”处理，状态文件损坏失败即锁定 | `AntiCheatGuardService.cs`、`SpawnPaths.cs`、`SpawnCoreServiceCollectionExtensions.cs` | 新增启动即锁定、实时探测重启冷却、重启后仍锁定（含损坏状态文件）3 项测试 |
| `F-05` | `P1` | 安装目录外清理目标改为白名单：必须位于已知数据根下且含游戏专属片段（标题 / `AppID` / GOG ID / 引擎产品名 / `厂商\游戏`）；PCGamingWiki 非叶子段通配符直接丢弃，`*` 叶子与 `{{p|uid}}` 展开结果须通过同一判定 | `GameDataSafetyFence.cs`（新增 `GameDataTargetIdentity`）、`PcGamingWikiRuleParser.cs`、`GameDataManagementService.cs` | 新增 `My Games` / `AppData\Roaming` / `LocalLow\<厂商>` / `Saved Games` / `Steam\userdata\<uid>` 拦截与通配符不扩大测试；原有 3 款样本路径推断测试保持通过 |
| `F-06` | `P2` | 清理前先原子写入 `status=pending` 撤销清单，每项“备份完成”与“原件删除”后增量重写（`.tmp` + 原子替换），全部结束改为 `completed`；清单写入失败即中止且不删除任何数据 | `GameDataManagementService.cs` | 新增中途删除失败（文件锁）+ 模拟断电 `pending` 清单后一键恢复测试 |
| `F-07` | `P2` | `quick_gate.py` 以字节捕获子进程输出，UTF-8 解码失败回退本机代码页（GBK），控制台流 `errors=replace`；子进程强制 `PYTHONUTF8=1`、`DOTNET_CLI_UI_LANGUAGE=en-US` | `scripts/quick_gate.py` | 修复前在 cp936 控制台重定向复现 `UnicodeEncodeError`（退出码 `1`）；修复后同环境 3 次全绿且测试计数正确解析 |
| `F-08` | `P2` | `gbe_fork` 由“latest”改为固定 `release-2026_09_27` / `emu-win-release-vs22.7z` + SHA-256 `300186c3…1c85`（发布于 2026-09-27，满足 7 天冷却）；摘要不符拒绝解压；缓存按版本目录隔离并附 DLL SHA-256 清单 | `Enabler/GbeForkBinarySource.cs`、`open-questions.md` `OSS-06` | 摘要与 GitHub 发布资产 `digest` 一致，并由 Agent B 独立下载重算 SHA-256 核对一致；测试断言固定标签/URL 不含 `latest` |

**同轮用户反馈（非审查编号，一并纳入复审范围）**：用户指出应用图标与同系列工具图标存在色差（底色、`DEV` 角标样式不一致）。已统一为扁平规格——`#3C4043` 深灰圆角方底（无渐变/描边）、白色几何 `S`（笔画 `110`、端点圆角 `28`）、开发版 `#F3A115` 琥珀角标 + `#202124` 几何 `DEV` 字形；新增矢量正本 `assets/icon.svg` / `assets/icon-dev.svg`，重写 `scripts/generate_icons.py`（1024 超采样确定性栅格化）并重新生成 `icon(-dev).png|ico`；托盘兜底渲染改为共享路径数据的 `Spawn.Host/Services/BrandIconGeometry.cs`（移除 Segoe UI / Consolas 字体依赖）；新增 `BrandIconParityGateTests`（`10` 项）守护 SVG、生成脚本色值、PNG/ICO 尺寸与 SkiaSharp 兜底像素一致。规格写入 [DESIGN.md](../design-rules/DESIGN.md)。

### Agent A 复审结论（2026-11-21）

- **复审对象**：候选提交 `d13695ea94273cfbd4b5fddf0909ac9e8747b16b`（父提交 `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`，DCO 尾注有效），复审后由 A 直接修复用户反馈并 amend 收束。
- **逐项结论**：`F-01`~`F-08` 全部关闭。A 独立复核：`quick_gate` 退出码 `0`（`73 / 73`），运行后 `spawn-tests-*` 残留 `0`、`MSBuild.exe` / `dotnet.exe` 驻留 `0`（`F-01`）；生产代码已无假 PE 替身（`F-02`）；`Tier 2/3` 启动前高危探测失败即阻断，守卫启动/每次高敏操作实时探测并持久化跨重启锁定（`F-03`/`F-04`）；安装目录外白名单要求已知数据根 + 游戏专属片段，厂商名不进入身份集合，`LocalLow\<厂商>` 不可清理（`F-05`）；`pending` 清单在删除前落盘且可恢复（`F-06`）；cp936 编码回退与离线绊线到位（`F-07`）；经 GitHub 只读 API 核对 `release-2026_09_27` / `emu-win-release-vs22.7z` 发布于 2026-09-27，资产 `digest` 与固定常量一致（`F-08`）。
- **非阻塞观察（`P3`，不返工）**：残留守护进程探测异常时按“无残留”处理（高危环境探测则失败即阻断），两者失败策略不对称；备份目录时跳过子级重解析点，清理后该链接本身不随恢复重建。均不影响本阶段验收。
- **同轮用户反馈（A 直接修复）**：
  1. **侧边栏“成就中心”与设置页“成就与 Steam API”奖杯图标只显示一半**：根因是 `MainWindow.xaml` 两处奖杯 `Path` 只描了杯身右半边（缺少 `12,14.8 → 7,4` 左侧曲线）；已统一为完整对称奖杯路径，离屏渲染全部 `Path` 图标复核无其他残缺。
  2. **成就音效与 Steam 原版不一致**：经核对本机 Steam 客户端 `steamui/sp.js`，`ToastAchievement` 在桌面模式映射 `desktop_toast_default.wav`、仅在大屏幕/Deck 手柄界面映射 `deck_ui_achievement_toast.wav`；`SteamClientSoundLocator` 原先优先后者，现改为优先桌面文件并回退大屏幕文件，同步更新单测与 [交互设计](../design/interaction-and-workflows.md)。播放前的温和峰值提振（`0.85`）保持不变。

---

## 3. 质量门禁与自动化测试执行记录（含诚实 `not-run` 声明）

所有自动化验证均在独立临时目录（`%TEMP%\spawn-tests-*`）与后台低优先级静默模式（`BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW`）下真实运行，安装包自动化测试严格限定独立测试身份 `spawn-dev-test`（`--no-shortcuts --no-relaunch`），零前台弹窗、零音频打扰、零污染真实 `spawn` / `spawn-dev` 数据：

| 检查项 | 执行命令 | 状态 | 结果与证据说明 |
| :--- | :--- | :---: | :--- |
| **一键快速门禁总入口（连续 3 次）** | `python -B scripts/quick_gate.py` | **`passed`** | 连续 3 次退出码均为 `0`，总耗时 `30.58s` / `22.84s` / `20.09s`（预算 `60.0s`），每次 `73 / 73` 测试通过（含图标一致性门禁）；测试步骤启用离线网络绊线；在 cp936（GBK）控制台重定向下运行未再出现 `UnicodeEncodeError` |
| **单独全量测试** | `dotnet test Spawn.sln -c Release --no-build -nologo --verbosity minimal` | **`passed`** | 不带任何门禁环境变量直接运行，退出码 `0`：`Spawn.Architecture.GateTests` `26 / 26`、`Spawn.Core.UnitTests` `47 / 47`；运行后 `%TEMP%\spawn-tests-*` 残留 `0`、`spawn-tests-leftover-*` 诊断 `0`、`MSBuild.exe` 残留进程 `0` |
| **离线网络佐证** | 同上命令，附加 `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` = `http://127.0.0.1:9` | **`passed`** | 代理指向死端口时 `73 / 73` 仍全部通过（仅能拦截遵循代理设置的 HTTP 客户端，属佐证而非形式化证明；源码层面已无测试构造真实网络请求） |
| **文档治理与卫生门禁** | `python -B scripts/check_docs.py` | **`passed`** | `59` 篇 Markdown、`6` 份受管 JSON、`47` 项需求与 `180` 个受管文本文件校验通过，零盘符路径与零密钥明文 |
| **`.NET 10` Release 编译** | `dotnet build Spawn.sln -c Release -nologo -v quiet` | **`passed`** | `.NET SDK 10.0.404` 下 `8` 个子工程 **0 警告、0 错误** |
| **`PEReader` 架构与视觉/音频防回归门禁** | `dotnet test tests/Spawn.Architecture.GateTests -c Release --no-build` | **`passed`** | **`26 / 26` 通过**（含 `BrandIconParityGateTests` `10` 项）：`Host`/`OverlayHost` **0 敏感 Win32 API 导入**（含本轮新增 `K32EnumDeviceDrivers` / `EnumServicesStatusExW` 只读导入后复验）；弹窗视觉、XAML `<Popup>` 绑定与音频管线防回归 |
| **核心、样本、数据清理、反作弊与隔离安装单测** | `dotnet test tests/Spawn.Core.UnitTests -c Release --no-build` | **`passed`** | **`47 / 47` 通过**：新增 `F-02`~`F-06` 回归测试 8 项（启动即锁定、实时探测、重启后仍锁定、高危名称识别、`Tier 2` CE/`dbk64` 启动阻断、固定版本 gbe_fork 来源、白名单围栏与通配符、增量撤销日志）；220+ 简中 Schema 由离线 HTTP 夹具驱动；`spawn-dev-test` 真实打包/静默安装/卸载演练耗时约 `10s` |
| **本机 `SPAWN Dev` 自动打包安装上机** | `python -B scripts/package.py --install-dev` | **`passed`** | 返工 amend 后重新生成并静默安装至 `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`，以 `--background` 驻留托盘（回执 `.local/reports/m2/install-dev.json`） |
| **NuGet 传递依赖漏洞审计** | `dotnet list Spawn.sln package --vulnerable --include-transitive` | **`passed`** | `8` 个项目直接与传递依赖 **0 已知安全漏洞**（本轮未新增 NuGet 包） |
| **CE / `dbk64` 真实环境拦截** | 本机安装 Cheat Engine 并加载 `dbk64.sys` 后启动 `Tier 2/3` 游戏 | **`not-run`** | 本机未安装 Cheat Engine、未加载任何 `dbk*`/`WinRing0`/`inpout` 驱动（只读查询确认）；仅以注入探针夹具验证阻断路径，真实探测函数只验证“可运行不抛错” |
| **固定版本 gbe_fork 产品内真实下载** | 产品内 `PinnedGbeForkReleaseSource` 联网拉取并解压 | **`not-run`** | 测试禁止联网；仅由 Agent B 在开发机独立下载该资产并重算 SHA-256 与常量一致，未经产品代码路径执行真实下载/解压 |
| **反作弊重启锁定实机** | 真实 `Tier 2/3` 网游退出后重启 SPAWN | **`not-run`** | 无受保护网游实机环境；以虚拟时钟与状态文件夹具验证 |
| **`H-001` 人工桌面与游戏内体验** | 双击本机已安装的 `SPAWN Dev` 体验（见 `USER-ACTIONS.md`） | **`not-run`** | **未执行客观原因**：遵循零前台打扰红线，自动化门禁绝不启动真实游戏窗口或发声；本机 `SPAWN Dev` 已装妥驻留托盘供用户随时免命令体验（不阻塞 `ACT-007`） |

---

## 4. 保守远端边界与后续交接指引

1. 遵照 `D-15`，本阶段全套代码、图标、安装器模板、文档及 `F-01`~`F-08` 返工已通过 `git commit --amend -s` 收束在本地 `feat/m2-launcher-achievement` 分支唯一提交中（父提交严格等于 `main` `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`），未执行 `git push` 或创建 PR。
2. 请仓库所有者查阅根目录 [USER-ACTIONS.md](../../USER-ACTIONS.md)：
   - 可直接双击系统托盘琥珀色 `DEV` 图标或桌面 **`"SPAWN Dev"`** 快捷方式体验 **`H-001`**；
   - Agent A 复审已达 `reviewed`，可按 **`ACT-007`** 授权推送 `feat/m2-launcher-achievement`、创建 PR #2 并合入 `main`，即可无缝启动 `M3`（存档时间机器 + 自建云同步 + FLiNG 零报毒修改器管家）。
