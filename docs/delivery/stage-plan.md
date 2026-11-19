# SPAWN M2 阶段实施计划（Stage Plan — M2 Launcher, Offline/GOG Achievement, Game Data Cleanup & Dev Auto-Installer）

> **阶段代号**：`M2`（Three-Column Clean Host, Game Library & Launch Tracking, Full-Spectrum Achievement & Overlay, Game Data Cleanup & Per-User Dev Installer）
> **计划版本**：`1.1`（含阶段内增强：PCGamingWiki/引擎游戏数据管理清理、新增两款 Steamrip 游戏深度适配与 `install-dev` 自动打包安装上机体系）
> **工作分支**：`feat/m2-launcher-achievement`（基线锚点 `main` @ `196faf33fe1c2f18d1e14cfc97caf6f3f91baf16`，对应已合并 [PR #1](https://github.com/chialecode/spawn/pull/1)）
> **关联文档**：[项目交付总状态](status.md) · [M2 阶段执行报告](stage-report.md) · [M2 阶段验收用例集](m2-stage-cases.json) · [里程碑路线图](../engineering/delivery-plan.md) · [已确认架构决策](../decisions/confirmed-decisions.md)

---

## 1. 阶段目标与边界定义

依据用户确认的 **`D-19` ~ `D-23`**，`M2` 作为首个正式 `.NET 10 (LTS)` 代码交付阶段，一次性打通从**按用户免命令自动安装的 `SPAWN Dev` 桌面客户端**，到**多形态 GOG/Steamrip 游戏识别启动、全流派单机成就实时跳杯、以及 PCGamingWiki 多源游戏数据一键清理恢复**的完整闭环：

- **阶段范围内（In-Scope）**：
  1. **阶段初四分类清理**：清理已合入 `main` 的过期阶段临时清单 `docs/delivery/m1-stage-cases.json`，推进 `document-registry.json` `currentStage="M2"`；
  2. **`.NET 10` 物理隔离底座与单实例版本协调（`Spawn.sln`）**：建立 `Spawn.Contracts` 与 `Spawn.Core`（`SpawnPaths` 双通道隔离与 `dev-workspace.json` 桥接、SQLite WAL 单写者仓储、DPAPI + `.local` 只读凭据回退、`0x1000` 安全进程查询、`AntiCheatGuard` 15s 冷却状态机、`SingleInstanceCoordinator` `--activate`/`--quit-for-update` 优雅退出握手、SDDL `NamedPipeJsonRpc`）；
  3. **深度子目录游戏库扫描、启动接管与多源游戏数据管理（`Spawn.Modules.Launcher`）**：
     - 4 层子目录深度探测嵌套于 `*_Data\Plugins\x86_64\` 的 `steam_api64.cdx`（CODEX）、`steam_api64.rne`（RUNE）、`steam_emu.ini` 与 `app.info`，零误差识别 `.local\games` 下全部 3 款真实游戏（`LIMBO`、`The Stanley Parable Ultra Deluxe`、`Vampire Survivors`）；
     - 无 `KILL_ON_JOB_CLOSE` 的 `JobObject` IOCP + `0x1000` 破壳追踪与 `Active/Background/AFK` 三态计时；
     - **`GameDataManagementService`（`D-22`）**：融合 PCGamingWiki Cargo API + SQLite 缓存、Unity `LocalLow`（含冒号转下划线变体）与 `HKCU` 注册表离线推断、16+ 模拟器与原生存档目录纳管，实施铁血防误删围栏与 `TrashDir` 可逆备份一键恢复；
  4. **四类全覆盖单机成就引擎与清理后初见跳杯保障（`Spawn.Modules.Achievement`）**：落地 16+ 离线模拟器（支持嵌套 `Plugins\x86_64` 与清理后目录从不存在到运行期新建文件的实时 `<1s` 捕获）、GOG `127.0.0.1:9977` Comet 守护、`LIMBO` `AES-128-ECB + XOR` 原生存档规则引擎、一键无损 `gbe_fork` 激活还原、多源简中 Schema 与 `SkiaSharp` Rec.709 灰锁图合成；
  5. **三栏无边框主界面、系统托盘与独立弹窗宿主（`Spawn.Host` + `Spawn.OverlayHost`）**：高保真复刻 `52px+264px+主面板` 三栏界面、220+ 项成就 UI 虚拟化、详情页 **`成就 | 游戏数据`** 双标签面板、系统托盘 `--background` 静默驻留与设置页“关于与版本”，以及三主题零抢焦点 `Spawn.OverlayHost`；
  6. **双通道图标、Inno Setup 7 按用户打包与 `install-dev` 自动安装闭环（`D-23`）**：交付 `scripts/generate_icons.py`、`installer/spawn.iss`、`scripts/package.py --install-dev`、`tests/Spawn.Architecture.GateTests`、`tests/Spawn.Core.UnitTests` 与 `scripts/quick_gate.py`。
- **阶段范围外（Out-of-Scope，归属 `M3`~`M6`）**：
  - `FastCDC` 增量时间机器、`32MB SPPK` 云同步与 FLiNG 修改器管家（`M3`）；CAS 硬链接 Mod 中枢与 `Alt+Z` 锁鼠大面板（`M4`）；二次元并发 DAG（`M5`）与 `Nodify` UE 蓝图虚拟机（`M6`）；未经 `ACT-007` 授权擅自推送远端。

---

## 2. 工作包拆解（Work Packages）

| 工作包编号 | 工作包主题 | 核心产出文件范围 | 质量控制焦点 |
| :--- | :--- | :--- | :--- |
| `M2-W01` | **`.NET 10` 解决方案、版本身份注入、单实例协调与 `Spawn.Core` 底座** | `Spawn.sln`、`global.json`、`Directory.Build.props`、`Directory.Packages.props`、`src/Spawn.Contracts/**`、`src/Spawn.Core/**` | `dev-workspace.json` 桥接、`SingleInstanceCoordinator` 零强杀退出、SQLite WAL 与 `0x1000` 探测 |
| `M2-W02` | **深度嵌套游戏扫描、双引擎启动追踪与三态游玩计时** | `src/Spawn.Modules.Launcher/**` | 精准识别 `.local\games` 全部 3 款真实游戏、JobObject 严禁 `KILL_ON_JOB_CLOSE` |
| `M2-W03` | **四类全覆盖单机成就引擎、清理后初见跳杯与 `gbe_fork` 激活器** | `src/Spawn.Modules.Achievement/**` | 嵌套 `.cdx`/`.rne` 识别、无目录新建 `achievements.ini` `<1s` 实时跳杯、旧档静默对齐 |
| `M2-W04` | **三栏无边框原生 WPF 主壳、虚拟化列表、托盘常驻与 `Spawn.OverlayHost`** | `src/Spawn.Host/**`、`src/Spawn.OverlayHost/**`、`DESIGN.md`、`docs/design-rules/DESIGN.md`、`docs/design/interaction-and-workflows.md` | 220+ 成就丝滑虚拟化、`成就 \| 游戏数据` 双面板、`--background` 零抢焦点驻留 |
| `M2-W05` | **`PEReader` 架构门禁、`quick_gate.py` 与 Windows `.NET 10` CI** | `tests/Spawn.Architecture.GateTests/**`、`scripts/quick_gate.py`、`.github/workflows/ci.yml` | 编译器产物级 IAT 零敏感 API 断言、低优先级无窗静默门禁 |
| `M2-W06` | **PCGamingWiki 游戏数据管理清理、Inno Setup 7 打包与 `install-dev` 自动安装** | `src/Spawn.Modules.Launcher/GameData*`、`scripts/generate_icons.py`、`scripts/package.py`、`installer/spawn.iss`、`assets/*`、`tests/Spawn.Core.UnitTests/**`、治理与交付文档 | 防误删安全围栏、`TrashDir` 可逆恢复、`spawn-dev-test` 隔离安装演练、本机 `SPAWN Dev` 自动装妥上机 |

---

## 3. 阶段准出判据与验证映射

| 用例编号 | 类型 | 对应工作包 | 验证内容 | 准出标准 |
| :--- | :---: | :---: | :--- | :--- |
| `M2-A-01` | 自动化 | `M2-W01`, `M2-W05` | `Spawn.sln` Release 编译与 `PEReader` IAT 敏感 API 零导入静态门禁 | 0 警告 0 错误编译，`Host` / `OverlayHost` 0 敏感 API 导入（`AC-02`） |
| `M2-A-02` | 自动化 | `M2-W01` | `SpawnPaths` 双通道隔离、`dev-workspace.json` 桥接、SQLite WAL 与 DPAPI 凭据单测 | 100% 落入临时目录，桥接与密文往返通过（`AC-02`） |
| `M2-A-03` | 自动化 | `M2-W01`, `M2-W02` | `AntiCheatGuard` 四级分类、高危拦截与 15 秒反作弊退出冷却锁状态机单测 | 冷却未满 15s 严格拒绝解锁高敏能力；`Tier 2/3` 启动前检出 CE / `dbk64` 等高危进程或驱动即阻断（`AC-03`） |
| `M2-A-04` | 自动化 | `M2-W02` | 无强杀标志 `JobObject` IOCP 回调与三态游玩计时单测 | 无窗短命进程准确触发归零回调并落盘时长（`AC-04`） |
| `M2-A-05` | 自动化 | `M2-W02` | 嵌套子目录扫描与 `.local\games` 下全部 3 款真实游戏只读扫描测试 | `LIMBO`、`Stanley Parable` (CODEX `1703340`)、`Vampire Survivors` (RUNE `1794680`) 100% 只读识别成功（`AC-05`） |
| `M2-A-06` | 自动化 | `M2-W03` | 16+ 模拟器、GOG Comet、`LIMBO` 原生存档及清理后无目录首次新建跳杯测试 | 全部夹具解析率 100%，无目录新建 `achievements.ini`/`savegame.txt` `<1s` 触发跳杯（`AC-06`） |
| `M2-A-07` | 自动化 | `M2-W03` | `gbe_fork`（固定版本 + SHA-256）无损激活/SHA-256 还原与离线夹具简中 Schema / 隐藏描述 / 灰图合成测试 | 激活与还原哈希一致，测试注入 DLL 来源且全程零真实网络（`AC-07`） |
| `M2-A-08` | 自动化 | `M2-W04`, `M2-W05` | `NamedPipeJsonRpc` 通信、三主题弹窗队列与 `quick_gate.py` 一键门禁 | 全程无窗无声静默通过（`AC-08`, `AC-02`） |
| `M2-A-09` | 自动化 | `M2-W06` | `GameDataManagementService` 多源路径推断、安装目录外白名单围栏、增量撤销日志与 `TrashDir` 可逆恢复测试 | 拦截删除游戏根目录及 `My Games` / `AppData` / `LocalLow\<厂商>` 等共享父目录，通配符不扩大；文件与临时 `HKCU` 子键备份清理及一键恢复 100% 一致（`AC-05`, `AC-06`） |
| `M2-A-10` | 自动化 | `M2-W06` | 单实例 `--quit-for-update` 握手与 `spawn-dev-test` 隔离打包/静默安装/卸载测试 | 优雅退出零强杀，测试身份安装/卸载全绿且真实 `spawn`/`spawn-dev` 零被测污染（`AC-02`） |
| `M2-M-01` | 人工体验 | `M2-W06` | 本机 `SPAWN Dev` 已由 `package.py --install-dev` 自动安装驻留托盘供 `H-001` 体验 | 桌面/开始菜单快捷方式与托盘已就绪；`feat/m2-launcher-achievement` 保持本地单提交等待 `ACT-007` |
