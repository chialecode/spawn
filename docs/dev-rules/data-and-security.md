# 数据隔离、存储安全与系统权限红线

触发条件：涉及安装通道、应用数据目录、`spawn.db` 数据库连接、`SPAWN_Vault` 内容寻址库、NTFS 硬链接/目录联接点（Junction）、云同步、DPAPI 凭据、提权子进程或日志输出的全部设计与编码工作。数据表结构与二进制格式详见 [数据模型与存储契约](../design/data-model.md) 及 [IPC 与反作弊安全契约](../design/ipc-and-security-contracts.md)。

---

## 1. 正式版与开发版通道物理隔离

<a id="install-and-data-locations"></a>

全仓路径、通道标识、目录名与凭据命名空间统一采用小写 **`spawn`**（产品展示名称仍为大写 **SPAWN**）。正式版、开发版与自动化门禁测试必须保持 100% 物理隔离，支持同机并存且互不干扰：

| 运行通道 / 身份 | 程序安装目录（NSIS 按用户安装 `RequestExecutionLevel user`，卸载项 `io.github.chialecode.<身份>`） | 应用数据根目录（数据库/凭据/配置/回收站） | 默认本地内容寻址库（分卷库为 `<目标盘根>\SPAWN_Vault\`） | 独立实例锁与控制/IPC 管道前缀 |
| :--- | :--- | :--- | :--- | :--- |
| **`release`（`spawn` 正式版）** | `%LOCALAPPDATA%\Programs\spawn\spawn.exe`（可选受保护 UIAccess 目录 `%ProgramFiles%\spawn\Overlay\`） | `%LOCALAPPDATA%\spawn` | `%LOCALAPPDATA%\spawn\vault` | `spawn-instance` / `spawn-control` / `spawn-ipc-*` |
| **`dev`（`spawn-dev` 开发版与源码运行）** | `%LOCALAPPDATA%\Programs\spawn-dev\spawn-dev.exe`（琥珀色 `DEV` 角标图标） | `%LOCALAPPDATA%\spawn-dev`（含 `dev-workspace.json` 桥接） | `%LOCALAPPDATA%\spawn-dev\vault` | `spawn-dev-instance` / `spawn-dev-control` / `spawn-dev-ipc-*` |
| **`test`（`spawn-dev-test` / `spawn-test` / 单元测试）** | `%TEMP%\spawn-tests-<id>`（开发通道打包演练用独立身份 `spawn-dev-test`）；`spawn-test`：`%TEMP%` 临时目录 `%TEMP%\spawn-release-drill-<id>\Programs\spawn-test`，正式通道自包含演练专用，演练结束静默卸载并删除整个临时目录 | `%TEMP%\spawn-tests-<id>\data`（`spawn-test`：`%TEMP%\spawn-release-drill-<id>\data`） | `%TEMP%\spawn-tests-<id>\vault`（`spawn-test`：`%TEMP%\spawn-release-drill-<id>\vault`） | `spawn-dev-test-instance` / `spawn-test-instance` / 随机测试管道名 |

- **环境变量覆盖、开发桥接与门禁保护**：`SPAWN_DATA_DIR` 可显式覆盖数据根目录；`install-dev` 向 `%LOCALAPPDATA%\spawn-dev\dev-workspace.json` 写入当前代码仓库根路径，使从快捷方式启动的 `SPAWN Dev` 同样能只读发现仓库 `.local\games` 与 `.local\secrets`（正式版 `spawn` 严禁读取该文件）。**源码构建与所有自动化测试严禁回退读写 `%LOCALAPPDATA%\spawn` 或 `%LOCALAPPDATA%\spawn-dev` 真实目录**；安装包卸载程序严禁配置 `[UninstallDelete]` 删除用户数据目录。
- **游戏数据清理围栏与可逆回收站（`TrashDir`）**：`GameDataManagementService` 清理存档、离线成就文件或 `HKCU` 注册表项前，游戏运行中必须拒绝执行；严禁删除游戏安装根目录 `<GameDir>` 本体、任何 `.exe`/`.dll`、`*_Data`、`Engine` 或系统根目录，注册表仅允许操作 `HKCU\Software\<Vendor>\<Game>`（深度 ≥ 3，严禁触碰 `HKLM`）；安装目录以外的清理目标一律走白名单：必须位于 `AppData` / `LocalAppData` / `LocalLow` / 文档 / 公用文档 / `Saved Games` / `ProgramData` / Steam 安装目录等已知数据根之下，且相对该根的路径中至少含一个游戏专属片段（标题、`AppID`、GOG ID、引擎产品名或 `厂商\游戏`），`My Games`、`AppData\Roaming`、`LocalLow\<厂商>` 等共享父目录永不可清理，PCGamingWiki 通配符（`*` / `{{p|uid}}`）展开结果同样受此约束、不得扩大为共享父目录；默认先将待清理文件与导出的注册表 JSON 快照移入 `<应用数据根>\trash\<GameId>\<timestamp>\`，且在删除任何数据前先原子写入 `pending` 撤销清单、每备份/删除一项即增量更新，断电或崩溃中断后仍可一键完整恢复。
- **已知系统目录解析红线**：读取用户“文档”“公用文档”等系统特殊目录（如定位离线模拟器存档或 `FLiNGTrainer` 配置）时，必须通过 `Environment.GetFolderPath` 或 Win32 `SHGetKnownFolderPath` 获取真实注册路径（兼容用户将文档盘符迁移或 OneDrive 重定向场景），**严禁硬编码拼接 `%USERPROFILE%\Documents`**。

---

## 2. `spawn.db` SQLite WAL 与网盘禁令

1. **标准连接与事务规范**：
   - 数据库固定位于 `<应用数据根>\db\spawn.db`，所有连接统一开启连接池并初始化执行：
     `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;`
   - Schema 演进采用单调递增的向前迁移脚本，执行破坏性或跨表迁移前必须先调用 SQLite Online Backup API 生成一致性备份快照；UI 线程严禁同步阻塞等待长写事务。
2. **严禁将 SQLite 数据库直放网盘同步目录**：
   - 多设备同步工具（OneDrive、坚果云同步盘、Syncthing）对 `.db`、`.db-wal`、`.db-shm` 三文件的异步乱序覆盖极易造成数据库物理页损坏（`database disk image is malformed`）。
   - `<应用数据根>\db\spawn.db` 与本地内容寻址库（`<应用数据根>\vault\` 及分卷 `<目标盘根>\SPAWN_Vault\`）永远驻留本机不受网盘直管的位置；跨 PC 与 Windows 掌机同步仅允许通过 Progress 模块传输内容寻址不可变的 **`32MB SPPK Packfile`** 与带 `ParentSnapshotId` Git 祖先链的 JSON 清单。

---

## 3. NTFS 硬链接、Junction 非递归卸载与防丢档安全红线

1. **Mod 硬链接与可变配置物理隔离（防写污染）**：
   - 同盘静态只读大资产通过免提权 `CreateHardLinkW` 部署至游戏目录；遇到原版同名文件时，必须在同目录原子重命名为 `<原文件名>.spawn_vanilla_bak` 并记入 `deployed_manifest` 表，严禁直接覆盖删除原版文件。
   - 凡匹配 `.ini`、`.json`、`.toml`、`.cfg`、`.xml`、`.sav` 等运行期可能被游戏或 Mod 框架原地改写的文件，**一律禁止建立硬链接**，必须部署为当前 Profile 独占的物理副本（`MutableCopy`），并在游戏退出后通过 `System.IO.Hashing.XxHash3` 秒级比对差异写回 `vault\Profiles\<ProfileId>\Overrides\`。
2. **存档槽 NTFS Directory Junction 非递归卸载铁律（防灭门惨案）**：
   - 多维 Profile 切换物理存档槽时，真实存档文件永驻 `<应用数据根>\vault\Saves\<GameId>\Slot_<ProfileId>\`（或分卷库 `<目标盘根>\SPAWN_Vault\Saves\<GameId>\Slot_<ProfileId>\`），游戏存档路径仅作为免提权 NTFS Directory Junction（`IO_REPARSE_TAG_MOUNT_POINT`）指向活跃槽位。
   - **最高级别代码红线**：卸载或切换存档目录重解析点时，必须先校验 `FileAttributes.ReparsePoint` 标志，且**只能调用非递归 `RemoveDirectoryW` 或 `DirectoryInfo.Delete(recursive: false)`**！全仓严禁对可能挂载了 Junction 的存档路径调用 `Directory.Delete(path, recursive: true)`，并必须在 `Spawn.Core.Tests` 中保留断电恢复与防递归误删专项单元测试。
3. **5 阶段 WAL 事务与回滚后悔药**：
   - Profile 切换与启动前环境变更（分辨率、HDR、动态壁纸暂停、代肝配置覆写）必须先落盘 `profile_switch_journal` 表与 `<应用数据根>\config\environment_rollback.json` / `<应用数据根>\config\workflow_config_rollback.json`，确保遭遇断电、游戏闪退或系统蓝屏后下次启动 SPAWN 时 100% 幂等自愈还原。
   - 执行任何历史存档快照回滚前，系统必须强制先对当前磁盘存档自动生成一枚 `[PreRollbackSafety]` 快照，且在 `ModProfileHash` 不一致时弹出明确坏档风险确认。
4. **WOF 磁盘压缩 DirectStorage 熔断**：调用 `FSCTL_SET_EXTERNAL_BACKING` 执行透明压缩前，必须扫描目标游戏目录是否包含 `dstorage.dll` 或 `dstoragecore.dll`；一旦检出支持 DirectStorage 硬件直通解压，立即强制熔断拒绝压缩。

---

## 4. 凭据加密、沙箱隔离与脱敏日志

1. **DPAPI 通道绑定凭据存储**：
   - S3/R2 密钥、WebDAV 密码、IGDB/SteamGridDB Token 与战报推送密钥统一使用当前登录用户范围的 `ProtectedData.Protect`（Windows DPAPI）加密保存于 `<应用数据根>\secrets\<credential-id>.sec`，附加熵（Optional Entropy）绑定通道名（`spawn` / `spawn-dev` / `spawn-test`）与提供方标识；数据库与配置文件仅保存 `credential://` 逻辑引用，备份导出包默认剥离 `secrets/` 目录。
2. **外部修改器沙箱与专用回收 Job**：
   - 下载的独立版外部修改器统一隔离存放在 `<应用数据根>\vault\Sandbox\Trainers\<GameId>\`，由 `TrainerWorker` 绑定带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的专用内核 Job 启动，游戏结束或 Worker 退出时由内核 100% 兜底回收，绝不留后台僵尸进程；反之，**追踪玩家游戏本体的 JobObject 严禁附加 `KILL_ON_JOB_CLOSE`**。
3. **日志与诊断脱敏规范**：
   - 结构化日志仅记录 ULID 实体标识、状态迁移、耗时、错误码与相对文件名；严禁打印任何凭据明文、HTTP `Authorization` 头、完整带参预签名 URL 或用户个人账户敏感隐私。
