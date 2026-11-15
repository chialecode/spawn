# ADR-0004：`FastCDC` 内容寻址存档库、`32MB SPPK` 云同步与 NTFS Junction Mod 拓扑

- **状态**：Accepted
- **日期**：2026-11-15
- **决策编号**：`D-08`、`D-09`、`D-12`
- **关联文档**：[数据模型与存储格式正本](../design/data-model.md) · [存档与修改器规格](../modules/progress-saves-and-trainer.md) · [Mod 与极客工具规格](../modules/nexus-mods-and-utilities.md) · [前期调研归档 02](../research/02-launcher-process-and-mod-nexus.md) · [前期调研归档 03](../research/03-save-timemachine-and-trainer.md)

---

## 1. 背景与问题陈述

在存档时间机器（Progress）与多维 Mod 编排（Nexus）两大存储密集型子系统中，现有开源与商业工具普遍面临三大工程瓶颈：

1. **整包 ZIP 存档备份导致磁盘空间爆炸与半写坏档**：现代 RPG / 开放世界单次存档常达 `100MB ~ 1GB+`（如大型体素世界或长流程 CRPG），传统每次按整包 `.zip` 全量复制，几十个自动历史点即可吞噬数十 GB SSD；同时若在游戏后台流式写盘瞬间强行读取，极易产生前新后旧的“撕裂坏档（Torn Save）”。
2. **碎块直传网盘引发 API 限流雪崩**：若直接模仿裸 CAS 目录将成千上万个小文件上传至坚果云、Nextcloud WebDAV 或对象存储，会瞬间触发 `429 Too Many Requests` 速率封禁并拖垮同步耗时。
3. **传统 Mod 部署污染原版、写穿原包与漫长重部署**：
   - MO2 所用的 `USVFS` 用户态 Hook 在新版 Windows 11 与 64 位反作弊/加壳游戏上兼容性脆弱；
   - Vortex 纯硬链接方案中，若游戏运行期直接打开 `.ini`/`.json` Mod 配置文件覆写保存，会因共享同一 NTFS MFT Inode **直接写穿污染仓库内的原始只读缓存**，且在数千个文件间切换不同 Mod Profile 时逐文件解绑重建耗时数秒至数十秒。

## 2. 备选方案对比

| 领域问题 | 备选方案 | 评估结论 |
| :--- | :--- | :--- |
| **存档历史去重** | A. 按时间戳全量压缩 `.zip` / `.7z`<br>B. 固定长度分块（Fixed-Size Chunking）<br>**C. 确定性 Gear-Hash `FastCDC` 变长内容分块 + `BLAKE3` + `Zstd`** | 选 **C**。固定分块在文件头部插入几个字节后会导致后续所有块偏移失效（Boundary-Shift 问题）；`FastCDC` 吞吐超 `1.5 GB/s`，插入字节仅影响局部单块，20 个增量快照额外占用缩减 90%+。 |
| **自建云端同步** | A. 裸传离散 Chunk 小文件<br>**B. 聚合封装 `32MB SPPK` 不可变 Packfile + 尾部索引 + 64 代血缘链** | 选 **B**。将数千个压缩块顺序拼装为 32MB 大包单次流式上传，彻底免疫 WebDAV 小文件限流，且无状态不可变树天然防并发写坏。 |
| **Mod 隔离与秒切** | A. `USVFS` 用户态虚拟文件系统注入<br>B. 每次切换逐文件删除并重建数千硬链接<br>**C. CAS 只读硬链接 + 可变配置 `MutableCopy`/`XxHash3` 回收 + 预构 Profile 视图目录顶层 NTFS Directory Junction `<100ms` 切换** | 选 **C**。100% 操作系统内核原生 NTFS 机制、零进程注入、彻底隔离配置文件写穿，且切换开销恒定为 `O(1)` 重解析点重定向。 |

## 3. 最终决策

### 3.1 `FastCDC` 存档时间机器与防撕裂双校验
1. **探测底座**：离线将 `ludusavi-manifest` 规则预编译入 `spawn.db` SQLite 表（`<1ms` 展开），严格分离 `[save]` 与 `[config]` 标签文件。
2. **去重管线**：自研零外部重依赖的 64 位 Gear-Hash `FastCDC` 分块器（最小 `256 KiB`、均值 `1 MiB`、最大 `4 MiB`），每个分块计算 32 字节 `BLAKE3` 摘要并以 `Zstd`（默认 Level 3）压缩落盘至 `SPAWN_Vault\Chunks\aa\bb\<blake3>.zst`。
3. **防撕裂与防误覆写**：文件变动触发 `2500ms` 写入静默窗口后，比对读取前后的 `(FileSize, LastWriteTimeUtc)` 向量（不一致则丢弃重采；排他锁冲突时按需调用 `AlphaVSS` 卷影快照）；任何回滚前强制自动生成 `[PreRollbackSafety]` 快照并校验 `mod_profile_hash`。

### 3.2 `32MB SPPK` Packfile 云同步协议
1. 待上传新块按序打包为上限 `32 MiB` 的不可变 `.sppk` 文件（头部 `SPPK` 魔数 + 中部连续压缩块 + 尾部偏移字典索引 + 末尾全包 `BLAKE3` 校验尾）。
2. 远端目录仅包含 `repo.json`、`packs/`、`snapshots/` 与按设备隔离的 `heads/<machine_id>.json`（携带最近 64 代 `ParentSnapshotId` 血缘列表），原生支持 S3/R2、WebDAV 与本地同步盘文件夹，分叉冲突时弹出双端可视化对比向导。

### 3.3 CAS 硬链接 + 可变配置回收 + 顶层 Junction WAL
1. **动静分离部署**：常规静态资产在 Profile 视图目录内通过同卷 `CreateHardLinkW` 指向只读 `SPAWN_Vault\Blobs\`；匹配 `.ini/.cfg/.toml/.json/.xml` 或规则声明的可变配置文件强制执行独立 `MutableCopy`，并在游戏进程退出后通过 `XxHash3` SIMD 扫描将差异写回 `SPAWN_Vault\Profiles\<ProfileId>\Overrides\`。
2. **原版纯净备份**：任何首次触碰游戏目录原版同名文件的部署，必须先将原版文件同目录重命名为 `<filename>.spawn_vanilla_bak` 并登记入 `deployed_manifest`。
3. **`<100ms` Junction 切换与严禁递归删除铁律**：多维 Profile 切换通过 `profile_switch_journal` WAL 事务日志驱动，利用 `DeviceIoControl(FSCTL_SET_REPARSE_POINT)` 毫秒级原子重定向游戏挂载目录与存档槽位顶层 NTFS Directory Junction（无需管理员权限）。移除 Junction 时**严禁调用递归删除 API**，必须校验 `FileAttributes.ReparsePoint` 后仅卸载重解析点壳本身。

## 4. 影响与后果

- **正面收益**：数十 GB 大型开放世界存档与百 GB Mod 整合包获得接近极致的 SSD 空间利用率、`<100ms` Profile 瞬切速度以及断电零损毁保障。
- **工程约束**：当 `SPAWN_Vault` 与目标游戏不在同一磁盘分区时，硬链接不可跨卷建立，系统必须自动检测卷序列号并平滑降级为带哈希跟踪的只读文件拷贝，同时在 UI 建议用户在该盘建立分卷缓存库。

## 5. 验证判据

- `FastCDC` 确定性测试向量在重复切分与局部插入场景下去重率与哈希还原字节级 100% 一致。
- 模拟切换事务中途强杀宿主及移除指向非空真实目录的 NTFS Junction，目标真实文件存活率 100%。
