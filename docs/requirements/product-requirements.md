# SPAWN 产品需求规格正本（Product Requirements Specification）

> **文档状态**：Active（M1 基线锁定）
> **适用范围**：SPAWN 全生命周期（M1 ~ M6）功能需求、安全红线与非功能质量判据
> **关联文档**：[项目基线](../project-baseline.md) · [已确认架构决策](../decisions/confirmed-decisions.md) · [系统顶层架构](../design/system-architecture.md) · [里程碑交付路线图](../engineering/delivery-plan.md) · [需求追踪矩阵](../delivery/traceability.json)

---

## 1. 产品定位与核心价值主张

**SPAWN**（**S**pawn · **P**rogress · **A**chievement · **W**orkflow · **N**exus）是一款面向硬核单机玩家、Mod 深度折腾用户与二次元多修玩家的 **Windows 原生极客游戏全生命周期中枢**。

针对当代 PC 玩家桌面工具高度碎片化（启动靠 Playnite、Mod 靠 Vortex/MO2、备份靠 Ludusavi、修改器靠散装 FLiNG、离线成就靠 Achievement Watcher、二次元收菜靠各自独立的 MAA/BetterGI/M7A/MaaFgo 窗口）以及内核级反作弊误封焦虑等核心痛点，SPAWN 确立以下五大产品支柱：

| 支柱代号 | 领域模块 | 核心价值定位 | 模块规格正本 |
| :--- | :--- | :--- | :--- |
| **S — Spawn** | 进程与环境管线 | 零强杀副作用的进程树追踪、三态真实游玩计时、HDR/分辨率/CPU 亲和性断电自愈环境接管与三级瀑布流元数据刮削 | [spawn-launcher.md](../modules/spawn-launcher.md) |
| **P — Progress** | 存档与单机修改管家 | 基于 `Ludusavi` + `FastCDC` 的微秒级去重存档时间机器、`32MB SPPK` 自建云同步、零杀软报毒的外部 FLiNG 静音管家与二期原生 `.CT` 预留 | [progress-saves-and-trainer.md](../modules/progress-saves-and-trainer.md) |
| **A — Achievement** | 全域成就与高刷图层 | 原生打通 15+ 款离线模拟器、GOG 本地 Comet `9977` 端口与 Steam 正版成就，配合 `ZBID_UIACCESS` 独立合成进程实现 144Hz 零掉帧零注入游戏内面板 | [achievement-and-overlay.md](../modules/achievement-and-overlay.md) |
| **W — Workflow** | 二次元生态编排与蓝图 | 不重造单游戏识图轮子，原生内嵌 `MaaFramework`、桥接 `MaaFgo+BBChannel` 并统管主流 CLI 助手；辅以全程可视化类 UE 蓝图引擎赋能长尾单机自动化 | [workflow-and-blueprint.md](../modules/workflow-and-blueprint.md) |
| **N — Nexus** | 硬链接 Mod 与极客军火库 | CAS 硬链接零拷贝部署、可变配置文件退出后 `XxHash3` 增量回收、五位一体 Profile `<100ms` NTFS Junction 原子切换及六大极客底层扩展工具 | [nexus-mods-and-utilities.md](../modules/nexus-mods-and-utilities.md) |

---

## 2. 产品边界与明确非目标（Non-Goals）

为防止范围蔓延并守住反作弊合规底线，以下事项明确排除在 SPAWN 产品范围之外：

1. **拒绝混合 Web 套壳技术栈**：严禁引入 Electron、Tauri、CEF 或 `WebView2` 渲染主界面与节点编辑器，杜绝后台常驻数百 MB 浏览器渲染进程与空域穿透缺陷（见 [ADR-0002](../decisions/0002-pure-native-wpf-and-nodify-ui-stack.md)）。
2. **严禁触碰任何在线多人竞技与反作弊受保护游戏内存**：严禁针对受 EAC、BattlEye、Vanguard、ACE、RICOCHET 或服务器权威校验保护的在线游戏提供任何内存读写、DLL 注入、封包篡改或竞技辅助功能（见 [ADR-0003](../decisions/0003-anti-cheat-guard-and-process-isolation.md)）。
3. **不重复造二次元单游戏 CV 识图脚本轮子**：不自行维护明日方舟、原神、崩坏：星穹铁道、绝区零、鸣潮、FGO 等长线更新游戏的日常关卡识图逻辑，专注做顶层多工具资源锁并发 DAG 调度、无人值守唤醒黑屏与战报聚合（见 [ADR-0005](../decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md)）。
4. **不强迫玩家编写文本代码脚本**：类 UE 蓝图系统坚持 100% 纯可视化节点连线、内联字面量控件与单步发光调试体验，不要求用户编写 Lua/Python/C# 脚本。
5. **不建设中心化商业账号与收费云存储服务**：无强制登录、无广告与商业变现、无隐式行为遥测；云存档完全依托玩家自有的 S3/R2、WebDAV 或本地同步盘目录。

---

## 3. 结构化功能需求清单

> **优先级定义**：
> - `P0`：架构基石或安全生死线，对应里程碑必须 100% 达标方可准出。
> - `P1`：核心差异化特性，构成完整用户体验闭环的关键能力。
> - `P2`：高阶极客扩展或二期演进能力，在架构契约层提前预留接口并在后期里程碑交付。

### 3.1 Spawn：启动器、环境接管与元数据管线（`REQ-SPW`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-SPW-01` | P0 | **无损双通道进程树生命周期追踪**：对常规单机游戏采用显式剔除 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Windows `JobObject` + IOCP 完成端口毫秒级捕获子孙进程启停（SPAWN 自身重启绝不误杀在玩游戏）；对启动器脱壳自杀或反作弊脱钩场景，自动切换至基于 `0x1000` 受限权限的 35 秒慢速目录特征唤醒追踪器。 | 1. 模拟启动器拉起子进程后自杀退出的夹具测试中，子进程退出事件捕获准确率 100%；<br>2. 强制终止 `Spawn.Host.exe` 后，被追踪的目标测试进程继续正常运行且句柄零泄漏。 |
| `REQ-SPW-02` | P0 | **三态精准游玩时长统计引擎**：基于前台窗口句柄、光标/键盘输入、`XInput` 与 `Windows.Gaming.Input` 手柄轴死区突破及 `WASAPI` 会话音频峰值构建 `Active`（真实游玩）、`Background`（切出后台）与 `AFK`（前台无输入静默超时）三态独立累加计时，并支持 `Magpie` 缩放窗口与 `Spawn.OverlayHost` 焦点白名单继承。 | 1. 开启 Magpie 全屏缩放或呼出 `Alt+Z` 面板时，游戏仍持续计入 `Active` 时长；<br>2. 手柄持续推摇杆且无键鼠输入时不误判为 `AFK`；切至桌面超时后准确转入 `Background`/`AFK` 桶。 |
| `REQ-SPW-03` | P1 | **断电自愈的游戏启停环境接管管线**：支持按游戏/Profile 配置启动前自动切换显示器原生分辨率、通过 Windows CCD `DisplayConfigSetDeviceInfo` 开关系统 HDR、切换高性能电源计划，并在执行前写入 `environment_rollback.json` 事务快照；游戏退出或宿主异常崩溃重启后自动幂等还原。 | 1. 游戏正常退出后 2 秒内，HDR、分辨率与电源计划完整恢复启动前状态；<br>2. 模拟宿主断电强杀后再次冷启动，启动自检 100% 识别残留快照并完成显示环境复原。 |
| `REQ-SPW-04` | P1 | **双架构区域转码与混合架构 CPU 调度优化**：自动解析目标 `.exe` PE 头（`0x8664` vs `0x014C`）无感分发 `Locale Remulator`（x64 `LRProc`）或 `Locale Emulator`（x86 `LEProc`）解决日系 Galgame/RPG 乱码；支持通过 `SetProcessDefaultCpuSets` 绑定 Intel P-Core 或 AMD 3D V-Cache CCD0，并调用 `SetProcessInformation` 豁免 Windows 11 EcoQoS 降频。 | 1. 针对 32 位与 64 位测试程序分别触发正确的区域转码引导器；<br>2. 在启用 P-Core/CCD0 策略后，目标进程默认 CPU Set 掩码与 EcoQoS 豁免标志经系统 API 回读验证一致。 |
| `REQ-SPW-05` | P1 | **桌面伴生工具协同编排**：支持在游戏进入运行态时自动向 Wallpaper Engine 发送暂停/静音控制指令、按配置自动拉起并联动销毁 Blinue Magpie 窗口缩放，在游戏退出后自动恢复桌面动态壁纸运行。 | 配置伴生策略后，目标进程启动与退出时伴生工具状态切换时延 `<500ms`，无孤儿伴生进程残留。 |
| `REQ-SPW-06` | P1 | **三级瀑布流离线与多源元数据刮削**：第一级离线秒级解析 Steam `appmanifest_*.acf`、GOG 注册表键与目录名中的 DLsite `RJ/VJ` 编号；第二级提取 `.exe` 版本资源 `ProductName` 并剥离版本号/发行组标签噪音；第三级按游戏品类并发查询 Bangumi、VNDB、DLsite、IGDB 与 SteamGridDB，将高清竖版封面、背景英雄图与简介持久化至本地缓存。 | 1. 含 `RJ` 号与 Steam `.acf` 的测试库在断网状态下离线识别率 100%，单条目离线扫描 `<10ms`；<br>2. 联网刮削支持多源候选人工纠偏，封面图落盘后完全支持离线秒开浏览。 |

### 3.2 Progress：存档时间机器、自建云同步与单机修改器管家（`REQ-PRG`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-PRG-01` | P0 | **原生 `ludusavi-manifest` 存档探测与配置剥离**：构建编译/更新工具链将开源 `ludusavi-manifest` YAML 规则库预编译至 `spawn.db` SQLite 索引表（查询延迟 `<1ms`），完整解析 `<winAppData>`、`<winDocuments>`、`<steam>`、`<base>` 等 16 种跨平台路径占位符与 Windows 注册表项，并将 `[save]` 进度数据与 `[config]` 画质键位配置物理拆流。 | 1. 随机抽样 100 款主流 Steam/GOG/Galgame 规则路径展开准确率 100%，单游戏路径探测 `<1ms`；<br>2. 回滚仅含 `[save]` 标签的快照时，玩家画质与键位配置文件保持原状不被覆盖。 |
| `REQ-PRG-02` | P0 | **基于 `FastCDC` 的存档时间机器与防撕裂原子回滚**：采用确定性 Gear-Hash `FastCDC`（最小 `256KB`、均值 `1MB`、最大 `4MB`）配合 `BLAKE3` 哈希与 `Zstd` 压缩构建块级去重时间机器；备份前强制执行 2.5 秒写入静默窗口与前后 `(FileSize, LastWriteTimeUtc)` 二次一致性向量校验（文件排他锁冲突时自动降级 `AlphaVSS` 卷影复制）；任何回滚操作前必须自动创建 `[PreRollbackSafety]` 保护快照。 | 1. 对 500MB 单体大存档仅修改 64KB 数据后连续创建 20 个历史快照，新增物理磁盘占用 `<25MB`；<br>2. 模拟存档持续高频写入期间触发自动快照，零半写撕裂坏档；回滚后可一键无损撤销回 `[PreRollbackSafety]`。 |
| `REQ-PRG-03` | P1 | **32MB `SPPK` Packfile 自建云同步与血缘合并**：将离散小块聚合封装为尾部带索引与全包 `BLAKE3` 校验的 `32MB SPPK` 不可变 Packfile，原生适配 S3/Cloudflare R2、WebDAV（坚果云/Nextcloud/OpenList）与本地同步盘目录；远端 `heads/<machine_id>.json` 维护 64 代 `ParentSnapshotId` 哈希链，自动执行 Fast-Forward 无感更新或在双端离线冲突时弹出可视化分叉保留/覆盖向导。 | 1. 数千个碎块在 WebDAV/S3 后端同步时不触发 `429 Too Many Requests`，中断重连具备幂等断点续传能力；<br>2. 构造 A/B 两台设备离线分叉快照场景，100% 精准拦截静默覆盖并展示双端时间、游玩时长与截图对比向导。 |
| `REQ-PRG-04` | P1 | **FLiNG 外部修改器确定性检索与双语热键解析**：支持针对单机游戏从 FLiNG 官方主站与历史归档站双源精准匹配对应游戏构建版本的独立修改器包，校验哈希后隔离解压，并通过双语正则规则自动提取 `Num 1`、`Ctrl+Num 2` 等功能映射表生成原生 UI 卡片。 | 常见中英文双语说明文本与嵌入式说明热键提取准确率 `>=95%`，解析失败时优雅回退展示原始说明面板且不阻塞启动。 |
| `REQ-PRG-05` | P0 | **零杀软误报修改器启动静音与独立音效控制**：采用双保险纯外部静音机制——`A0` 在启动前预写 `%LOCALAPPDATA%\FLiNGTrainer\FLiNGTSettings.ini`（及便携目录配置）关闭启动音乐并支持 22 字节合法静音 MIDI 替换；`A` 由宿主通过 `NAudio.Wasapi` 监听全音频输出端点的 `OnSessionCreated` 事件，按进程 PID 微秒级拦截静音并提供修改器专属提示音滑块。**严禁对修改器进程执行任何 DLL 注入或内存补丁**。 | 1. 首次冷启动任意外部 FLiNG 修改器时零刺耳背景音乐爆音；<br>2. 全程不向修改器进程打开写内存句柄或注入模块，Windows Defender 与主流杀软零行为拦截告警。 |
| `REQ-PRG-06` | P0 | **High IL `TrainerWorker` 沙箱执行与硬件扫描码热键代理**：修改器统一存放于已引导添加 Defender 排除项的 `SPAWN_Vault\Sandbox\Trainers\<AppId>\` 隔离目录，由 High IL `Spawn.TrainerWorker.exe` 通过带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的强绑定作业对象拉起；无小键盘笔记本玩家点击 UI 开关或按自定义组合键时，由 Worker 发送带 `KEYEVENTF_SCANCODE` 与 35ms 按压保持的硬件扫描码代理触发。 | 1. 仅在 `AntiCheatTier == Tier 0` 纯单机下允许激活，一旦检测到 `Tier 1~3` 游戏运行立即拒绝启动或强杀沙箱内全部修改器；<br>2. 87/68 键无小键盘布局下通过 UI 卡片切换修改项响应成功率 100%。 |
| `REQ-PRG-07` | P2 | **二期原生 `.CT` 内存修改引擎扩展契约**：一期在架构层定义统一的 `ITrainerBackend` 策略接口（一期默认实现 `ExternalFlingBackend`）；二期（M6）在 `Spawn.TrainerWorker` 隔离域内落地 `NativeCheatTableBackend`，支持 Cheat Engine `.CT` XML 逆序 `<Offsets>` 指针链解析、`Reloaded.Memory.SigScan` AVX2 特征码定位与 `Iced` ±1.75GB 近端内存页安全跳板分配。 | 1. M2~M3 阶段接口契约与 IPC 消息提前冻结，二期扩展无需改动 `Spawn.Host` 代码且保持主宿主零敏感 API 引用；<br>2. M6 落地后支持标准静态指针与 AOB 脚本激活并在退出时原子还原原始机器码字节。 |

### 3.3 Achievement：全域离线/正版成就引擎与高刷游戏内 Overlay（`REQ-ACH`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-ACH-01` | P0 | **15+ 离线模拟器、GOG 本地安装版与正版 Steam 全栈解析矩阵**：原生内置零外部 Node.js 依赖的 C# 高性能解析器，完整支持 Goldberg、`gbe_fork`（`GSE Saves`）、CODEX/RUNE、EMPRESS、TENOKE、Online-Fix、ALI213（`.Bin` 文本 INI）、3DM/RLD!（小端序 Hex 时间戳）、SKIDROW、SmartSteamEmu（`stats.bin` 4B 计数 + 24B 记录结构体 + CRC32 映射）、GreenLuma 注册表、GOG 离线安装版（本地 `127.0.0.1:9977` Comet 协议监听 + `gameplay.db` SQLite WAL 只读快照 + `NemirtingasGalaxyEmu` 兜底）及正版 Steam `UserGameStats_*.bin` Binary VDF 位掩码。 | 针对上述全部 15+ 种格式构建标准黄金样本夹具集（含 UTF-8 BOM、无引号节名、小端 Hex 时间戳与二进制结构体边界），单元测试解析准确率 100%，单文件增量解析耗时 `<2ms`。 |
| `REQ-ACH-02` | P0 | **四层防漏监听管线与首次入库静默对齐**：组合 64KB 缓冲区 `FileSystemWatcher`、每路径独立 120ms 去抖合并定时器（处理 `MoveFileEx` 原子替换与 `ERROR_SHARING_VIOLATION` 指数退避重试）、仅针对当前前台运行游戏的 500ms `GetFileAttributesExW` 元数据微轮询兜底，以及首次扫描存量解锁记录时标记 `IsInitialSeed = true` 静默落库机制。 | 1. 连续同秒解锁 10 项成就零漏报、零重复弹窗，按队列平滑串行展示；<br>2. 导入已有 50 项历史解锁记录的旧存档目录时，历史进度完整入库且不触发任何弹窗轰炸。 |
| `REQ-ACH-03` | P1 | **无成就桩老旧发行版一键无损赋能（Achievement Enabler）**：静态扫描识别仅返回空桩、无持久化落盘能力的发行组 `steam_api.dll` / `steam_api64.dll`（如早期 FLT 发行版），计算并记录原始 DLL `SHA-256` 备份为 `.spawn_orig_bak` 后，一键部署开源 `Detanup01/gbe_fork` 兼容层并自动生成 `steam_settings/achievements.json`，支持随时一键无损还原。 | 部署赋能包后目标测试程序正常写入 `GSE Saves` 解锁事件；执行还原后游戏目录二进制哈希与赋能前 100% 一致。 |
| `REQ-ACH-04` | P1 | **零配置中文成就 Schema 瀑布流与剧透保护灰阶图标合成**：按“游戏本地模拟器 `steam_settings/achievements.json` -> 本机 Steam `appcache/stats/UserGameStatsSchema_<AppId>.bin` -> 免 Key 社区 Schema 镜像（SteamHunters / SteamDB 兼容端点）及可选用户免费 Steam Web API Key 增强通道”多级瀑布流自动拉取 `schinese` 标题、隐藏成就 `hidden=1` 真实揭晓描述与全球达成率；利用 `SkiaSharp` Rec.709 亮度矩阵离线预生成未解锁灰阶图标，默认对隐藏成就打码防剧透。 | 无需强制配置 Steam Web API Key 即可完整显示中文成就列表与隐藏成就解锁条件（配置可选 Key 时优先走官方权威接口）；未解锁隐藏成就默认模糊遮罩，支持鼠标悬停临时检视。 |
| `REQ-ACH-05` | P0 | **144Hz 零掉帧独立合成弹窗宿主（`Spawn.OverlayHost`）**：采用独立进程 `Spawn.OverlayHost.exe` 基于 `WS_EX_NOREDIRECTIONBITMAP` + `DirectComposition` + `SkiaSharp` Flip-Model 透明交换链渲染解锁弹窗，内置 Steam 原生、PS5 白金奖杯、Xbox 稀有钻石（达成率 `<10%` 自动触发长混响音画特效）、原神天地万象及崩铁星琼五套高帧率主题与可替换音效包；利用本地瞬态自签名证书赋予 `%ProgramFiles%\spawn\Overlay\Spawn.OverlayHost.exe` `uiAccess="true"`（`ZBID_UIACCESS`）越过无边框全屏置顶，便携未签名模式自动平滑回退常规 `TopMost`。 | 1. 在 144Hz/240Hz 无边框全屏 DirectX 12 / Vulkan 游戏上弹窗动画丝滑无闪烁，不引起可变刷新率（G-Sync/FreeSync）脱锁；<br>2. `Spawn.OverlayHost.exe` 对目标游戏进程零句柄打开、零 DLL 注入。 |
| `REQ-ACH-06` | P1 | **`Alt+Z` 与手柄组合键游戏内交互式面板**：支持 `Alt+Z` 全局热键及手柄 `Guide` / `View + Menu` 长按 400ms 呼出游戏内半透明毛玻璃战术面板（查看当前游戏成就清单、剧透悬停揭示、手动一键创建存档检查点、离线单机快捷冻结暂停）；激活时动态移除 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`、铺设 45% 暗化全屏防误触遮罩并通过 `ClipCursor` 接管光标边界，收起后毫秒级归还游戏焦点与锁鼠矩形。 | 全屏 FPS/ACT 游戏中呼出面板点击滚动列表时，点击事件 100% 不穿透至底层游戏镜头或开火逻辑；关闭面板后游戏立即恢复原有视角鼠标控制。 |

### 3.4 Workflow：二次元外部生态并发编排与类 UE 蓝图引擎（`REQ-WFL`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-WFL-01` | P0 | **明日方舟 `MAA` 无自重启配置预写与双轨集成**：针对 `MAA` v5.x 使用 `--config` 会触发自身提权自重启导致父进程句柄脱管的底层机制，在拉起 `MAA.exe` 前原子备份并精准覆写 `config/gui.json` 任务勾选与完成后退出策略，执行完毕后自动还原用户手工配置；同时预留 `MaaCore.dll` C API 动态绑定通道以获取细粒度掉落事件。 | 无人值守拉起 `MAA` 时进程生命周期跟踪准确率 100%，无自重启脱管假退出；任务结束后玩家原 `gui.json` 个人配置无损恢复。 |
| `REQ-WFL-02` | P0 | **`MaaFramework` 生态宿主级 C# 原生内嵌与 CLI 降级**：在 `Spawn.WorkflowWorker` 内原生集成 `Maa.Framework` C# NuGet 绑定，自动扫描外部工具目录的 `interface.json`（兼容 V2 规范）动态映射控制器、资源包与任务参数生成原生 WPF-UI 配置表单，直接内存级驱动 `M9A`（重返未来：1999）等十余款 `MaaFramework` 衍生项目并实时订阅识别与掉落回调；遇版本 ABI 不兼容时自动无感降级调用随包 `MaaPiCli.exe -d`。 | 接入任意符合 `MaaFramework` `interface.json` 标准的新二次元助手项目时，无需修改 SPAWN C# 代码即可自动渲染任务参数面板并完成全流程无人值守执行。 |
| `REQ-WFL-03` | P1 | **FGO `MaaFgo` + 闭源 `BBChannel` 双端口协议桥接与防卡死看门狗**：原样复用 `xlxyvergil/MaaFgo` 战斗指令卡/宝具编排模型与 `BBChannel` 高速后台控制底座；采用外置 `_ctypes.pyd` 代理模块在 `BBChannel` 启动时注入内存补丁（强制激活 `25001` 指令端口与 `25002` 通知端口、替换失效的 `_RetiredMumuApi` 为 `MaaFramework` ADB 控制器、静默拦截阻塞式 `messagebox`），并内置 30 秒助战连接错误视觉看门狗自动点击重试。 | 零修改 `BBChannel` 原始打包 EXE 即可稳定打通双端口 JSON RPC 调度；模拟网络波动弹出连接失败提示框时，30 秒看门狗 100% 自动恢复刷本循环且不挂起流水线。 |
| `REQ-WFL-04` | P1 | **主流 PC 原生二次元助手标准化 CLI 适配器与预更新**：内置标准化外部进程适配器统管 `BetterGI`（`--startOneDragon`）、`March7thAssistant`（`m7a.exe` 退出码 `0..6` 精确映射与 `fight`/`universe` 子命令）、`StarRailCopilot`（修改任务 YAML `NextRun` 时间戳调度）、`ZenlessZoneZero-OneDragon`（`-i -c` 批处理模式）与 `ok-wuthering-waves`（`-t -e` 命令行模式），并支持联动 `Hi3Helper.Sophon` 思路在收菜前静默预检游戏本体补丁更新。 | 各适配器标准输出流 ANSI 色彩码清洗与关键阶段状态提取准确，进程超时或卡死时由 `WorkflowWorker` 自动抓取现场截图并安全终止残留进程树。 |
| `REQ-WFL-05` | P0 | **四维资源锁感知的并发 DAG 编排器**：将每个自动化节点声明为四维资源契约——`PrimaryForegroundLock`（物理主屏前台键鼠独占锁）、`ChildSessionSlot`（专业版桌面分身后台并发槽）、`EmulatorInstanceLock:<AdbSerial>`（MuMu12/LDPlayer 后台帧缓冲并发锁）与 `SystemMemoryAndVramGate`（系统可用内存/显存硬水位门禁）；调度器自动按拓扑依赖与资源互斥实现“1 个 PC 原生前台游戏 + N 个后台模拟器实例”全速无冲突并发收菜。 | 当两个均声明 `PrimaryForegroundLock` 的任务同时就绪时严格串行排队零抢键鼠；同时就绪的后台模拟器 `MAA` 与 `MaaFgo` 任务在同一时段稳定并行执行；可用内存低于设定阈值时自动延后拉起新模拟器。 |
| `REQ-WFL-06` | P1 | **夜间无人值守闭环：RTC 唤醒、防烧屏黑屏幕布、战报推送与安全睡眠**：支持 `CreateWaitableTimerExW`（`TIMER_MODIFY_STATE` 唤醒标志）定时从 S3 睡眠唤醒主机并阻止锁屏阻断；执行期间自动临时关闭系统 HDR（保护 SDR 识图阈值一致）并开启 OLED 全黑软件防烧屏幕布或切换 `IddCx` 虚拟显示器关闭物理屏；全部 DAG 节点收尾后由 `SkiaSharp` 渲染暗黑毛玻璃汇总战报长图推送至 Bark / Telegram / 飞书 / 企业微信 / OneBot，最终弹出 60 秒可随时取消的倒计时后调用 `SetSuspendState(false, false, false)` 安全睡眠。 | 1. 无人值守期间临时关闭 HDR，流水线结束或中断后 100% 自动恢复原 HDR 状态；<br>2. 战报长图准确汇总各游戏耗时、成功/失败状态与异常现场缩略图；60 秒倒计时内任意键鼠输入立即取消自动睡眠。 |
| `REQ-WFL-07` | P1 | **全程可视化类 UE 蓝图引擎与发光单步调试虚拟机**：基于 `miroiu/nodify` 构建支持白线执行流（`ExecPin`）与多色强类型数据流（`DataPin<T>`）、拖拽悬空引脚弹出上下文搜索菜单、节点内联枚举/坐标拾取器的纯原生蓝图画布；底层在 `Spawn.BlueprintWorker` 运行异步 Kismet 状态机，通过命名管道向主界面实时回传当前激活节点脉冲发光流、支持红点断点挂起、`Step Over` 单步推进与引脚实时值监视（`Pin Watch`）；内置 `WGC` 捕获、`OpenCvSharp4` 多尺度模板匹配、`Sdcb.PaddleOCR` 离线文字提取及 L1~L4 四级可插拔输入执行器（后台消息、`SendInput`、`ViGEmBus` 虚拟手柄、可选硬件 HID），并通过 Windows 计划任务单次授权实现免 UAC 弹窗静默拉起 High IL Worker 解决管理员游戏 UIPI 拦截。 | 1. 1000 节点规模蓝图画布平移缩放帧率稳定 `>=60FPS`，`Spawn.BlueprintWorker` 崩溃不影响 `Spawn.Host` 主窗口；<br>2. 命中节点断点时自动化即时挂起且可在画布悬浮气泡读取上游 OCR/识图置信度与坐标值；仅在 `Tier 0/1` 安全白名单范围放行执行。 |

### 3.5 Nexus：硬链接 Mod 编排、多维 Profile 原子快照与极客军火库（`REQ-NEX`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-NEX-01` | P0 | **CAS 硬链接 Mod 仓库、原版文件保护与可变配置增量回收**：所有 Mod 解压后按 `BLAKE3` 内容寻址存入只读 `vault\Blobs\`（或同盘分卷 `SPAWN_Vault\Blobs\`）；同盘符部署采用 `CreateHardLinkW` 毫秒级零空间翻倍投射（跨盘符自动回退只读硬拷贝）；首次覆盖原版同名文件前强制重命名为 `<file>.spawn_vanilla_bak` 写入 `deployed_manifest`；对 `.ini/.toml/.cfg/.json/.xml` 等可变配置文件自动采用独立 `MutableCopy` 部署，并在游戏退出后通过 `System.IO.Hashing.XxHash3` SIMD 极速比对将玩家游戏内修改自动回收到当前 Profile 专属 `Overrides/` 层。 | 1. 部署 20GB 同盘符大型贴图 Mod 耗时 `<2s` 且额外磁盘占用接近 0MB，一键卸载纯净还原后游戏目录零残留且 `.spawn_vanilla_bak` 100% 复原；<br>2. 游戏内修改 Mod 热键配置退出后，切换到纯净 Profile 再切回该 Mod Profile，玩家自定义配置完整保留。 |
| `REQ-NEX-02` | P1 | **跨游戏引擎语义化加载器编排**：拒绝盲目平铺覆盖，内置四大引擎适配器——① Unity `BepInEx`（自动检测 `UnityPlayer.dll` 旁 `MonoBleedingEdge` vs `GameAssembly.dll` 精准匹配 Mono/IL2CPP Doorstop `winhttp.dll`）；② Unreal Engine 4/5（统一治理 `PakChunk` `.pak/.utoc/.ucas` 挂载槽位与 `Win64\dwmapi.dll` `RE-UE4SS`）；③ 通用引擎 `Reloaded-II` JSON 配置桥接；④ 二次元客户端 `XXMI` 风格 `3DMigoto` 沙箱目录隔离加载（`d3d11.dll` 驻留沙箱不写死游戏本体目录，强制高亮 `HideUID` 防晒图封号提醒）。 | 四类引擎标准目录结构自动识别准确率 100%；Unity Mono 与 IL2CPP 混装错误可提前静态拦截告警；切换至 `Purist` 纯净 Profile 时游戏根目录零第三方代理 DLL 残留。 |
| `REQ-NEX-03` | P0 | **五位一体多维 Profile `<100ms` 原子切换与 WAL 自愈**：每个游戏支持创建任意数量的独立 Profile（如 `Vanilla`、`HeavyMod`、`Speedrun`），单次切换在 `<100ms` 内原子联动五大维度：① Mod 视图目录（通过 `FSCTL_SET_REPARSE_POINT` 原子重定向非递归顶层 NTFS Directory Junction，彻底规避递归遍历删除目标文件风险）；② 绑定独立存档槽位目录 Junction；③ 专属命令行参数与区域转码配置；④ 默认伴生蓝图脚本；⑤ 修改器预设开关；全程由 `profile_switch_journal` 预写事务日志守护。 | 1. 含 5000+ Mod 文件的两个 Profile 相互切换耗时 `<100ms`；<br>2. 在切换事务任意中间阶段模拟断电强杀进程，重启后自愈引擎 100% 自动回滚或前滚至一致态，绝不误删任何真实存档或 Mod Blob。 |
| `REQ-NEX-04` | P1 | **绿色便携沙箱化与智能熵值感知透明压缩**：提供一键“便携沙箱化”开关，优先注入 `Goldberg`/`gbe_fork` `local_save.txt` 标记并在运行期挂载 `%APPDATA%` 临时 Junction 将漫游存档收拢至游戏便携子目录；内置基于 Windows WOF `FSCTL_SET_EXTERNAL_BACKING`（`XPRESS8K` / `LZX`）的无损磁盘瘦身器，对每个文件执行首中尾 `3 × 64KB` 采样计算香农熵（$H > 7.85\text{ bits/byte}$ 的高熵加密包或音视频流直接跳过零 CPU 空转），并在检测到游戏目录存在 `dstorage.dll`（DirectStorage）时强制置灰熔断防掉帧。 | 1. 压缩后的游戏无需任何后台守护进程即可从任意途径原生直接双击启动；<br>2. 含 `dstorage.dll` 的测试目录 100% 触发压缩熔断保护；高熵压缩包采样跳过单文件耗时 `<0.5ms`。 |
| `REQ-NEX-05` | P2 | **超分 DLL 官方签名热替换与 Sunshine 串流动态虚拟屏协同**：自动扫描游戏目录 `nvngx_dlss.dll` / `nvngx_dlssg.dll` / `amd_fidelityfx_dx12.dll` / `libxess.dll`，在部署替换前强制调用 `WinVerifyTrust` 校验 NVIDIA/AMD/Intel 官方数字签名并备份原文件（仅限 `Tier 0/1` 游戏，对 `Tier 2/3` 在线网游硬锁死禁改），支持一键开关 DLSS 调试 HUD；原生读写本地 `Sunshine` `apps.json` 导出游戏库，并在 Moonlight 串流连接时联动 `IddCx` 虚拟显示器动态匹配客户端分辨率与刷新率、熄灭主机物理 OLED 屏。 | 1. 篡改签名的伪造超分 DLL 100% 被 `WinVerifyTrust` 拒绝部署，`Tier 2/3` 游戏超分替换按钮强制禁用；<br>2. Sunshine 串流断开后主机物理显示器与原始显示拓扑自动复原。 |
| `REQ-NEX-06` | P2 | **进程级独立音频路由与战术应急工具箱**：封装 Windows 音频策略 COM 接口 `IAudioPolicyConfigFactory` 实现单游戏输出声卡持久化绑定（如伴生挂机游戏走虚拟声卡静音、主玩大作走外置 DAC）；内置三大单机应急工具——① 仅限 `Tier 0` 的 `NtSuspendProcess` / `NtResumeProcess` 过场动画急冻暂停（宿主退出自动恢复防僵尸挂起）；② 全局热键 `TerminateJobObject` + `ClipCursor(null)` + 分辨率还原的黑屏死锁强杀救援；③ 老旧窗口化游戏一键剥离边框 `SetWindowLongPtrW` 强制无边框全屏。 | 1. `NtSuspendProcess` 在 `Tier 1~3` 游戏中严格禁止调用；挂起态游戏在 SPAWN 退出时 100% 自动解冻恢复；<br>2. 模拟独占全屏假死抢锁光标场景，触发强杀救援热键后 1 秒内终止死锁进程树并释放鼠标活动范围。 |
| `REQ-NEX-07` | P1 | **跨模块 Profile 与存档联动防坏档护栏**：存档快照元数据强制记录创建时刻的 `mod_profile_hash`；当玩家在 `Vanilla` 纯净 Profile 下尝试回滚由 `HeavyMod` 创建的存档（或反之）时，主动弹出 Mod 依赖差异告警，支持“连同对应 Mod Profile 一键联动切换并回滚”或“克隆至新隔离存档槽位试玩”。 | 跨不兼容 Mod Profile 触发存档恢复时拦截提示率 100%，杜绝因缺失前置 Mod 物品表导致的静默坏档覆写。 |

### 3.6 Anti-Cheat Guard：四级反作弊物理隔离与全局安全红线（`REQ-SEC`）

| 编号 | 优先级 | 需求描述 | 验收判据 |
| :--- | :--- | :--- | :--- |
| `REQ-SEC-01` | P0 | **Clean Host 绝对纯净导入表与 CI 静态 PE 门禁**：主界面进程 `Spawn.Host.exe`（Medium IL）与弹窗进程 `Spawn.OverlayHost.exe`（UIAccess IL）严禁静态引用或动态反射调用 `ReadProcessMemory`、`WriteProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、`NtSuspendProcess`、`SetWindowsHookEx`、`SendInput`、`mouse_event` 等敏感 API 及 `MinHook`/`EasyHook`/`ViGEm` 库；CI 流水线通过 `System.Reflection.PortableExecutable.PEReader` 对编译产物执行零容忍门禁扫描。 | `Spawn.Architecture.GateTests` 扫描 `Spawn.Host.dll/.exe` 与 `Spawn.OverlayHost.dll/.exe` 导入表与程序集引用，发现任意一项黑名单符号立即令构建失败（Exit Code 非 0）。 |
| `REQ-SEC-02` | P0 | **全域 `0x1000` 最低权限进程探测铁律**：宿主与常驻监控组件枚举或查询任何外部游戏进程路径、存活状态与退出码时，一律仅允许调用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION [0x1000], false, pid)` 配合 `QueryFullProcessImageNameW`，严禁申请 `PROCESS_VM_READ (0x0010)`、`PROCESS_VM_WRITE` 或 `PROCESS_ALL_ACCESS`（避免触发 `.NET Process.MainModule` 内部的越权句柄打开）。 | 代码审计与运行时 API 审计确认：对外部进程打开的所有常驻监控句柄访问掩码恒等于 `0x1000`（或用于同步等待退出的 `SYNCHRONIZE`）。 |
| `REQ-SEC-03` | P0 | **四级 `AntiCheatTier` 全局联锁矩阵与启动前高危环境清场**：构建离线签名库 + 目录驱动特征启发式扫描器（识别 `EasyAntiCheat_EOS.sys`、`BEDaisy.sys`、`vgk.sys`、`AntiCheatExpert`/`SGuard64.exe`、`randgrid.sys` 等），将游戏划分为 `Tier 0`（纯离线单机）、`Tier 1`（弱联网/二次元客户端）、`Tier 2`（用户态/轻内核反作弊网游）、`Tier 3`（Ring-0 高敏内核反作弊竞技网游）；启动 `Tier 2/3` 游戏前必须先确认 `BlueprintWorker`、`TrainerWorker`、`WorkflowWorker` 已彻底退出且零句柄残留，若检测到系统加载了 `dbk64.sys`（Cheat Engine 驱动）或 `WinRing0` 则弹窗阻断启动并提示用户清理环境。 | 1. 主流 EAC / BattlEye / Vanguard / ACE 特征目录识别准确率 100%，未知联网状态游戏支持保守降级策略；<br>2. 在 `TrainerWorker` 运行期间点击启动 `Tier 3` 游戏，系统 100% 先自动销毁 `TrainerWorker` 沙箱或阻断启动告警，绝不允许二者并存。 |
| `REQ-SEC-04` | P0 | **Explorer Shell COM 进程血缘脱钩启动**：针对所有 `Tier 2` 与 `Tier 3` 受反作弊保护游戏，严禁由 `Spawn.Host.exe` 直接调用 `Process.Start` / `CreateProcess` 拉起；强制通过 `IShellDispatch2.ShellExecute` COM 接口委派桌面 `explorer.exe` 代为启动，从内核 `EPROCESS->InheritedFromUniqueProcessId` 层面彻底切断父子血缘与环境句柄继承。 | 通过系统进程查询验证：由 SPAWN 启动的 `Tier 2/3` 测试程序其父进程 PID 恒为当前会话 `explorer.exe` PID，而非 `Spawn.Host.exe` PID。 |
| `REQ-SEC-05` | P0 | **内核反作弊残影 15 秒强制静默冷却期状态机**：当 `Tier 2/3` 游戏主进程退出后，全域安全锁不得立即释放，必须持续以 `0x1000` 受限查询监测伴生反作弊服务进程（如腾讯 ACE `SGuard64.exe` / `SGuardSvc64.exe`、`EasyAntiCheat_EOS.exe`、`BEService.exe`）直至其完全终止（或达到安全上限告警），并在确认彻底消失后再追加 **15 秒强制静默冷却倒计时**，期间继续禁止拉起任何特权 Worker。 | 构造模拟 `Game.exe` 退出后 `SGuard64.exe` 延迟驻留 8 秒的夹具场景，全域安全锁在 `8s + 15s = 23s` 结束前严格阻止 `TrainerWorker` 与 `BlueprintWorker` 启动，UI 实时显示反作弊冷却倒计时。 |
| `REQ-SEC-06` | P0 | **跨完整性级别命名管道 SDDL 授权与本地文件 DPAPI 凭据保护**：所有宿主与 Worker 之间的 `gRPC over Named Pipes` 服务端必须通过 `NamedPipeServerStreamAcl.Create` 显式配置 `PipeSecurity`（仅授予当前登录用户 SID 读写权限，显式 `Deny` `WellKnownSidType.NetworkSid` 杜绝局域网 SMB 管道横移）；S3/WebDAV 云同步密钥与推送 Webhook Token 一律经 `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` 加密存储于数据目录独立密文文件，禁止明文落盘且不依赖系统凭据管理器弹窗。 | 1. 非当前登录用户 SID 或携带 `NETWORK` 令牌的连接请求 100% 被命名管道内核 ACL 拒绝；<br>2. 数据库 `spawn.db` 与配置文件中零明文密钥，跨用户或跨机器拷贝密文文件无法解密。 |

---

## 4. 非功能与工程质量需求（`NFR`）

| 编号 | 优先级 | 质量维度 | 量化指标与验收判据 |
| :--- | :--- | :--- | :--- |
| `NFR-01` | P0 | **原生桌面性能与低资源驻留** | 基于 `.NET 10`（LTS）+ 原生 WPF（`WPF-UI` + `Nodify`），SSD 环境下冷启动首屏可交互时间 `<800ms`；后台最小化托盘静默守望态 `Spawn.Host.exe` 私有工作集内存 `<120MB`、空闲 CPU 占用 `<0.2%`；1000 节点规模蓝图画布平移缩放稳定 `>=60FPS`。 |
| `NFR-02` | P0 | **游戏内高刷零帧率损耗** | `Spawn.OverlayHost.exe` 静态休眠时零 GPU 重绘开销；弹窗动画与 `Alt+Z` 面板渲染期间对目标 144Hz/240Hz 游戏平均帧率及 1% Low FPS 影响 `<0.5%`。 |
| `NFR-03` | P0 | **崩溃与断电幂等自愈** | 存档快照提取与回滚、Mod Profile NTFS Junction 重定向、`MAA` `gui.json` 临时覆写及系统 HDR/分辨率接管全部采用预写事务日志（WAL / Rollback Journal）与同卷原子重命名，任意时刻断电重启均自动自愈无数据损毁。 |
| `NFR-04` | P0 | **自动化测试全程静默与零抢焦点** | 所有单元、契约与集成测试必须在 Headless / Offscreen 模式下执行，严禁弹出可见桌面窗口、严禁抢占用户当前活动窗口焦点、严禁调用真实物理光标移动或键盘注入、严禁污染真实用户文档目录。 |
| `NFR-05` | P0 | **正式版与开发版双通道物理隔离** | 正式发行版固定使用 `%LOCALAPPDATA%\spawn`，本地源码开发、调试与测试固定使用 `%LOCALAPPDATA%\spawn-dev`（含独立 `spawn.db`、独立 `SPAWN_Vault`、独立命名管道前缀 `spawn-dev-*`），双通道可同机并存互不污染。 |
| `NFR-06` | P1 | **离线自治与隐私零遥测** | 在完全拔除网线状态下，游戏启动追踪、存档时间机器备份/回滚、离线模拟器成就监听弹窗、Mod 硬链接 Profile 切换与本地蓝图执行均 100% 正常运作；软件不包含任何第三方统计 SDK 或中心化行为上报。 |
| `NFR-07` | P0 | **开源合规与许可证洁净度** | 仓库整体采用 `Apache-2.0` 许可证发布；严禁直接复制或静态链接任何 GPL/AGPL 传染性代码；对 `Maa.Framework`、`Reloaded.Memory.SigScan` 等 `LGPL-3.0` 组件严格通过独立动态链接库（DLL）加载并在发行包附带完整 `THIRD-PARTY-NOTICES` 与替换说明。 |
| `NFR-08` | P0 | **文档治理与双向可追踪性** | 本正本中每一个 `REQ-*` 与 `NFR-*` 编号必须在 [traceability.json](../delivery/traceability.json) 中映射至具体设计章节、模块规格、目标里程碑与验收判据 `AC-*`，并由 CI `python scripts/check_docs.py` 自动化门禁持续校验。 |
