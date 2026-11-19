# SPAWN 五大核心产品原则（Core Product Principles）

本文件定义贯穿 SPAWN 全生命周期（`M1`~`M6` 及后续演进）的五项不可妥协产品不变量。任何功能提案、交互捷径或性能优化若违反以下任一原则，均不得合入主干；具体功能范围与验收判据见 [产品需求正本](../requirements/product-requirements.md)。

---

## 原则一：跨模块深度联动优先（Cross-Domain Synergy First）

SPAWN 的存在价值不在于将五个互不相干的小工具拼进同一个窗口，而在于打通 **Spawn（启动环境）↔ Nexus（Mod 与存档槽）↔ Progress（增量快照与修改器）↔ Achievement（成就监听与游戏内面板）↔ Workflow（二次元排班与蓝图自动化）** 的上下文闭环：

- 点击切换一套游戏 `Profile`，必须在 `< 100ms` 内原子联动切换 **Mod 硬链接组合 + 独立物理存档槽（NTFS Junction）+ 启动参数/转区/超分策略 + 关联蓝图脚本与修改器配置**；
- Boss 战门前按下 `Alt + Z` 呼出游戏内面板，可直接一键打下带实时游戏截图的 `[Pinned 存档时间锚点]`、查阅隐藏成就剧透指南或微调修改器提示音音量，无需切出桌面翻找多款软件；
- 夜间无人值守代肝唤醒电脑时，自动联动环境管线临时关闭 Windows 11 HDR（防截图泛白过曝）、点亮虚拟显示器或铺设 OLED 全黑防窥幕布，任务完成后合成战报并安全恢复休眠。

---

## 原则二：反作弊绝对零污染（Zero Anti-Cheat Contamination）

保护玩家的氪金二游与竞技网游账号 **0% 误封风险** 高于一切便利性诉求：

- **架构物理隔离**：常驻宿主 `Spawn.Host.exe` 与覆盖层 `Spawn.OverlayHost.exe` 永远保持零敏感 Win32 API 导入，经得起任何内核级反作弊驱动（ACE、HoYoProtect、EAC、BattlEye、Vanguard）的句柄与导入表扫描。
- **四级联锁与余温冷却**：针对 `Tier 2` / `Tier 3` 受保护游戏，系统在启动前强制执行高危进程/驱动清场、物理销毁并锁死内存/按键高敏 Worker、通过 Explorer Shell COM 切断父子进程血缘，并在游戏退出后严格执行 **15 秒反作弊守护进程（如 `SGuard64.exe`）余温冷却期**。绝不允许任何“一键绕过反作弊保护锁”的后门开关。

---

## 原则三：非侵入、可逆与原子级数据安全（Non-Invasive & Crash-Safe Operations）

玩家的原版游戏目录、数千小时存档与外部工具二进制文件神圣不可损坏：

- **零二进制篡改**：外部 FLiNG 修改器去电音采用预置配置 + WASAPI 会话实时静音（绝不默认用 `UpdateResourceW` 改写修改器 EXE 防破坏加壳与触发杀软误杀）；GOG 离线成就捕获采用本地 `127.0.0.1:9977` 协议守护（零修改游戏 DLL）；离线模拟器激活器修改前强制计算 SHA-256 并保留一键原版还原备份。
- **零写污染与零误删**：Mod 静态资产走同盘免提权硬链接（原版同名文件原子重命名备份为 `.spawn_vanilla_bak`），可变配置强制走独立副本并在退出后用 `XxHash3` 回收差异；多维 Profile 切换全程受 5 阶段 WAL 日志保护，摘除 NTFS Directory Junction 严禁递归删除；多端云同步严格按 `ParentSnapshotId` Git 祖先链防覆盖，回滚历史存档前强制自动生成 `[PreRollbackSafety]` 后悔药快照。

---

## 原则四：100% 纯原生极客高性能（Pure Native Zero-Bloat Performance）

作为与大型 3A 游戏及多开模拟器同台运行的常驻中枢，SPAWN 必须对宿主机 CPU、内存、VRAM 与高刷帧率保持极致克制：

- **零 WebView2 / 前端套壳**：全栈采用 `.NET 10`（LTS）原生 WPF + `DirectComposition` + `SkiaSharp`，主进程空闲内存与后台 CPU 占用压至最低，冷启动迅捷干脆。
- **零轮询风暴与零高刷掉帧**：游戏进程监控优先采用内核级 Job Object IOCP 事件通知（0% CPU 轮询）；全局触发优先采用 `RegisterRawInputDevices` / `RegisterHotKey`（拒绝拖慢全局鼠标的 `WH_KEYBOARD_LL` 低级钩子）；成就弹窗采用无重定向表面 Flip-Model 交换链，确保 144Hz/240Hz 游戏 MPO 硬件独立翻转 **0.0 FPS 掉帧**。

---

## 原则五：离线自治与零中心依赖（Local-First & Sovereign Autonomy）

SPAWN 是纯粹属于玩家个人的本地极客利器，不绑架用户数据、不制造服务端单点寿命风险：

- **断网满血可用**：无需注册任何官方账号，断网环境下游戏启动管线、三态时长统计、Mod 硬链接部署、多维存档槽切换、FastCDC 本地时间机器、15+ 离线模拟器成就解锁弹窗与本地 OCR 蓝图脚本 100% 正常工作。
- **开放标准与外生生态共生**：云同步直接对接标准 S3（Cloudflare R2 / MinIO）、WebDAV（坚果云 / 群晖 NAS）或本地同步目录；二次元自动化拥抱 `Maa.Framework` 与主流开源 CLI 生态，不重复造脆弱的封闭轮子；所有未配置的可选外部凭据均保持优雅降级并诚实提示能力边界。
