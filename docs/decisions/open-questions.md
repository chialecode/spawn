# SPAWN 上游事项台账（Upstream Items）

> **文档状态**：Active（2026-11-24 按 [D-34](confirmed-decisions.md#d-34) 改为上游事项台账；原 `LOOP-*` 与用户事项已转为 GitHub issue）
> **关联文档**：[已确认架构决策](confirmed-decisions.md) · [技术选型与环境清单](../engineering/technology-selection.md) · [里程碑交付路线图](../engineering/delivery-plan.md) · [GitHub Issues](https://github.com/chialecode/spawn/issues)

本台账只登记上游开源组件与底层 API 的适配、兼容与合规事项（`OSS-*`）：它们能否收敛取决于上游版本或实机条件，不一定马上动手。需要动手时建 issue，并在“当前状态”里写上 issue 号。需求、缺陷、后续迭代与需要用户处理的事项都在 [GitHub Issues](https://github.com/chialecode/spawn/issues)，不在这里登记。

> **管理规则**：
> 1. 某事项实机验证收敛后，把状态改为 `Resolved` 并补充验证结论；涉及重大契约变更时同步升格为新 ADR。
> 2. 不在仓库里散落未追踪的 `TODO`：后续工作建 issue，上游风险登记在这里。

---

## 1. 外部开源生态与底层 API 适配事项（`OSS-*`）

| 编号 | 归属阶段 | 事项描述与潜在风险 | 收敛触发条件与既定降级方案 | 当前状态 |
| :--- | :--- | :--- | :--- | :--- |
| `OSS-01` | `M5` | **`Maa.Framework` C# 绑定对 `interface.json` V1 / V2 混合生态的兼容边界**：社区十余款 `MaaFramework` 衍生二次元工具存在不同版本运行时与 `interface.json` Schema 演进差异，单一内嵌 `MaaFramework.dll` 版本可能与个别老项目资源包产生 ABI 偏差。 | 进入 M5 开发时，优先加载外部项目自带目录中的原生 `MaaFramework.dll` 或校验 Schema 版本；一旦检测到大版本不匹配，自动无感回退至随包 `MaaPiCli.exe -d` 进程级标准 CLI 调度。 | Open |
| `OSS-02` | `M5` | **闭源 `BBChannel` 客户端更新对 `_ctypes.pyd` 代理补丁符号稳定性的影响**：`MaaFgo` 依赖的 `BBChannel` 若更新内嵌 Python 版本或调整内部类名（如 `_RetiredMumuApi`），代理初始化脚本需按特征自适应。 | 在 M5 编写 `_ctypes_proxy` 引导层时，增加 Python 次版本号探测与 `getattr`/`hasattr` 防御性反射检查；若目标函数签名变更，立即优雅回退并向用户输出版本兼容告警而非引发宿主崩溃。 | Open |
| `OSS-03` | `M6` | **Windows 11 24H2/25H2+ 未公开音频路由接口 `IAudioPolicyConfigFactory` 的 IID 稳定性**：微软曾在历史大版本更新中微调未文档化音频策略 COM 接口 GUID。 | 进入 M6 实现进程级音频端点路由前，封装基于系统构建号（Build `22621` / `26100` / `26300+`）的多版本 IID 探测适配器（参考 `EarTrumpet` 实现），探测失败时自动隐藏该单项极客开关。 | Open |
| `OSS-04` | `M2` | **便携免安装模式与 `ZBID_UIACCESS` 置顶签名的双态体验对齐**：Windows 内核强制要求 `uiAccess="true"` 进程必须带受信根证书签名且位于 `%ProgramFiles%` 等受保护目录；纯解压便携版用户在不运行安装向导时无法获得 `ZBID_UIACCESS`。 | M2 实现 `OverlayHost` 实时成就弹窗启动能力自检：已部署受保护目录签名版自动启用 `ZBID_UIACCESS`；纯便携版自动回退至 `SetWindowPos(HWND_TOPMOST)` + 无边框窗口化友好提示，并在设置页提供一键本地证书与受保护目录部署向导。 | Open |
| `OSS-05` | `M4`（随存档时间机器顺延，`D-25`） | **排他文件锁存档场景下的 `AlphaVSS` 卷影复制权限边界**：`AlphaVSS` 创建 NTFS 卷影副本需要管理员（High IL）权限，而日常 `Spawn.Host` 运行于 Medium IL；原“经已授权计划任务唤醒 High IL 辅助任务”路径已被 `D-11` 修订否决（按用户安装目录下的计划任务构成本地提权）。`M3` 启动前轻量快照（`REQ-PRG-08`）**不引入 `AlphaVSS`**，命中排他锁时非阻塞跳过并提示。 | 进入完整时间机器轨道时：默认 Medium IL `FileShare.ReadWrite \| FileShare.Delete` 非阻塞读取；仅当连续 3 次命中 `ERROR_SHARING_VIOLATION` 时，按 `D-11` Level 2 经用户显式确认的单次 `runas` UAC 拉起 High IL 辅助进程执行一次 VSS 快照提取，用户取消即降级为“跳过并提示”；Level 3 免 UAC 路径依赖 `LOOP-07`。 | Open（顺延 `M4`） |
| `OSS-06` | `M2` / `M3` / `M5` / `M6` | **第三方组件许可证合规与发行包合规物料自动化生成**：`M2` 外部 `Detanup01/gbe_fork` 与 `M5` `Maa.Framework` 为 `LGPL-3.0`，须独立 DLL 动态部署并附许可证文本与用户可替换说明。**2026-11-21 红灯纠偏（`D-29`）**：`Reloaded.Memory 9.x` 已改 `GPL-3.0`、`Reloaded.Memory.SigScan 3.1.7+` 与 `Reloaded.Hooks` 传递依赖 `GPL-3.0`，已从全部选型清单移除，`M6` 改用 `.NET 10` `Vector256` 自研；`SevenZipExtractor`（捆绑 `LGPL-2.1 + unRAR` 限制 `7z.dll`）、`Squid-Box.SevenZipSharp`、`Keystone`（`GPL-2.0`）一并禁用。`M3` 新增 MIT 依赖：`SharpCompress 0.50.9`、`AngleSharp 1.10.3`、`Microsoft.Windows.CsWin32 0.3.371`；随包精简 `AreWeAntiCheatYet` 数据索引为 MIT 数据集。 | `M2` 起 `gbe_fork` 固定为 `release-2026_09_27` / `emu-win-release-vs22.7z`（SHA-256 `300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85`），下载后校验失败即拒绝解压，缓存按版本隔离并附 SHA-256 清单；升级版本须同步修改常量与本条记录。**`M3` 增量已收敛**：仓库根目录已落盘完整 [THIRD-PARTY-NOTICES.txt](../../THIRD-PARTY-NOTICES.txt)（涵盖 `SharpCompress 0.50.9`、`AngleSharp 1.10.3`、`Microsoft.Windows.CsWin32 0.3.371`、`NAudio.Wasapi 2.4.0`、`AreWeAntiCheatYet` 嵌入数据集、`WPF-UI`/`SkiaSharp`/`CommunityToolkit.Mvvm`/`Google.Protobuf`/`Microsoft.Data.Sqlite` 及按需动态链接 `Detanup01/gbe_fork` `LGPL-3.0` 声明），由 `scripts/package.py` 与 `installer/spawn.iss` 随包复制至安装根目录；已审计全仓 13 份 `obj/project.assets.json`（49 个传递 NuGet 包）零 `GPL`/`AGPL`/`LGPL` 包且 `dotnet list Spawn.sln package --vulnerable --include-transitive` 报 0 已知漏洞。待 `M5` 接入 `Maa.Framework`（`LGPL-3.0`）时补全其动态链接物料。 | Open（`M3` 增量已收敛，待 `M5` 续接） |
| `OSS-07` | `M3` | **修改器实机未核实项（`UNV-01` ~ `UNV-07`）**：① 新版/经典 FLiNG 冷启动前是否读取凭空预建的 `OnLoadMusic` INI、是否兼容 `=0`；② 经典修改器经 `midiOutOpen`/`mciSendString` → GS Wavetable Synth 播放时音频会话 PID 归属；③ Medium IL `Spawn.Host` 对 High IL 修改器会话调用 `ISimpleAudioVolume::SetMute` 是否稳定返回 `S_OK`；④ 闭源 FLiNG 热键检测 API 与轮询周期；⑤ FLiNG 独立 EXE 是否带 Authenticode 签名；⑥ `SharpCompress 0.50.9` 对 RAR3/Solid RAR5 的通过率；⑦ `NAudio.Wasapi` `3.x` 升级影响。 | **已实测收敛**：② 本机 `M3-W06` 显式开启 `SPAWN_RUN_UNV02_AUDIO_PROBE=1` 实测 `UNV-02` 探针通过，观测到 4 个渲染端点且 Microsoft GS Wavetable Synth 音频会话归属当前 PID，同时真实 27B 静音 SMF `mciSendString` 播放与重播（`initialMode=playing, replayMode=playing`）通过；⑥ `M3-W04` 已通过仓库内无害合成压缩包/stub PE 夹具验证并保留用户本机 `7z.exe` 模式 C 兜底；⑦ `M3` 锁定沿用 `NAudio.Wasapi 2.4.0`（暂不升 `3.x`）。**待 `H-002` 实机核验（当前 `not-run`）**：①④⑤ 需用户使用真实修改器按 [#18](https://github.com/chialecode/spawn/issues/18)（原 `H-002`）的步骤反馈；③ 需真实 High IL 修改器会话实测（已实装 `E_ACCESSDENIED` → 管道回退 Worker 执行兜底）。 | Open（②⑥⑦已实测/定案；①③④⑤待 `H-002` 实机 `not-run`） |

---

## 2. 原 `LOOP-*` 与用户事项的去向

2026-11-24 起改用 GitHub Issues 跟踪（[D-34](confirmed-decisions.md#d-34)）。原条目全文见固定提交里的[旧台账](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/docs/decisions/open-questions.md)与[旧用户待办](https://github.com/chialecode/spawn/blob/83d749dbb07e3a112c03857f2bbe48eac4fffadd/USER-ACTIONS.md)。

| 原编号 | 主题 | 去向 |
| :--- | :--- | :--- |
| `LOOP-01` | 原生修改后端：声明式修改包与基础 `.CT` 子集（`M6`，目标收窄，`D-29`） | [#23](https://github.com/chialecode/spawn/issues/23) |
| `LOOP-02` | 远古独占全屏可选渲染桥 `Spawn.LegacyHook.Helper` | [#24](https://github.com/chialecode/spawn/issues/24) |
| `LOOP-03` | 硬件级 HID 输入执行器插件（KMBox Net / B+ 串口） | [#25](https://github.com/chialecode/spawn/issues/25) |
| `LOOP-04` | 专业版子会话后台并发槽（`WTSEnableChildSessions`） | [#26](https://github.com/chialecode/spawn/issues/26) |
| `LOOP-05` | 安装包与签名更新管线：正式版签名更新已在 `M3` 交付（[ADR-0007](0007-weekly-release-and-signed-in-app-update.md)）；剩余 Overlay 受保护目录的本地瞬态自签名证书部署 | [#27](https://github.com/chialecode/spawn/issues/27) |
| `LOOP-06` | `Tier 2/3` 启动前自动终止自有高敏 Worker 与改名驱动识别 | 已在 `M3` 闭环（`M3-A-05`） |
| `LOOP-07` | `%ProgramFiles%` 自包含提权 Worker 包与免 UAC 计划任务（`D-11` Level 3） | [#28](https://github.com/chialecode/spawn/issues/28) |
| `LOOP-08` | XInput 手柄组合键的修改器热键代理（`D-28`） | [#29](https://github.com/chialecode/spawn/issues/29) |
| `ACT-001`、`ACT-002`、`ACT-006`、`ACT-007`、`ACT-008` | `.NET 10 SDK`、`M1`～`M3` 推送与合并、Steam Web API Key 与本地测试样本 | 已完成（`M3` 经 [PR #7](https://github.com/chialecode/spawn/pull/7) 合入） |
| `ACT-009` | `release` 环境签名 Secret、Artifact Actions 白名单与签名私钥备份 | [#9](https://github.com/chialecode/spawn/issues/9)、[#10](https://github.com/chialecode/spawn/issues/10) |
| `ACT-010` | 正式版每周自动发布开关 | [#9](https://github.com/chialecode/spawn/issues/9) |
| `ACT-003` | 可选：IGDB 与 SteamGridDB 在线海报凭据 | [#13](https://github.com/chialecode/spawn/issues/13) |
| `ACT-004` | 可选（`M4`）：存档云同步的 S3 或 WebDAV 凭据 | [#14](https://github.com/chialecode/spawn/issues/14) |
| `ACT-005` | 可选（`M5`）：夜间战报推送通道凭据 | [#15](https://github.com/chialecode/spawn/issues/15) |
| `Q-001`、`Q-002` | Windows 代码签名证书；正式版自动安装更新的时机 | [#16](https://github.com/chialecode/spawn/issues/16) |
| `H-001` | 检查：`M2` 启动器、单机成就与游戏数据清理 | [#17](https://github.com/chialecode/spawn/issues/17) |
| `H-002` | 检查：`M3` 修改器安全管家与启动前存档快照 | [#18](https://github.com/chialecode/spawn/issues/18) |
| `H-003` | 检查：`M4` Mod 快照、存档时间机器与 `Alt+Z` 面板 | 随 [#20](https://github.com/chialecode/spawn/issues/20) |
| `H-004` | 检查：`M5`/`M6` 并发代肝与蓝图 | 随 [#21](https://github.com/chialecode/spawn/issues/21)、[#22](https://github.com/chialecode/spawn/issues/22) |
| `H-005` | 检查：首个正式版的安装与应用内更新 | [#19](https://github.com/chialecode/spawn/issues/19) |
