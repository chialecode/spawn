# SPAWN 集中技术审查口径

人工与 AI 审查者共用。审查者必须不是作者：用干净上下文的子代理，或交给另一个 Agent / 人。先按 [AGENTS](AGENTS.md) 找到本次改动适用的规则，再直接核查实际 `git diff`、源码树、门禁退出码与产物证据，不能只复述作者的总结。流程见[开发流程](docs/dev-rules/development-workflow.md#change-loop)，门禁见[质量门禁](docs/dev-rules/quality-gates.md)。

---

## 1. 核心审查顺序（SPAWN 专项五大门禁）

1. **范围、需求与决策一致性**：
   - 实际改动是否严格对应关联 issue、`REQ-*` 需求、[已确认决策](docs/decisions/confirmed-decisions.md) 与五大模块规格书（`docs/modules/*`），有无擅自缩减核心能力或越界改动无关模块。
2. **进程物理隔离与 `PEReader` IAT 零污染门禁（一票否决项）**：
   - `Spawn.Host` 与 `Spawn.OverlayHost` 的 `.csproj` 引用链是否严禁依赖 `OpenCvSharp4`、`Sdcb.PaddleOCR`、`Iced`、`Maa.Framework`、`Nefarius.ViGEm.Client` 等高敏或重型 Worker 专属包；全仓传递依赖树是否零 `GPL`/`AGPL` 包（`Reloaded.Memory` 家族、`SevenZipExtractor` 等已判红，见 [ADR-0006](docs/decisions/0006-m3-external-trainer-manager-elevation-and-ipc.md)）；
   - `Spawn.Architecture.GateTests`（M2 起启用）是否对编译产出的 `Spawn.Host.exe` 与 `Spawn.OverlayHost.exe` 执行真实 `System.Reflection.Metadata.PEReader` 导入表扫描，确保零 `WriteProcessMemory`、`ReadProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、`SetWindowsHookEx`、`NtSuspendProcess` 导入；
   - 常规进程监控是否严格使用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION = 0x1000)` + `QueryFullProcessImageNameW`，杜绝读取强反作弊网游 `Process.MainModule` 触发 `PROCESS_VM_READ` 告警；
   - 跨完整性级别 Named Pipe 是否显式通过 `NamedPipeServerStreamAcl` 配置当前用户 SID 授权并拒绝 `NETWORK` SID。
3. **`Anti-Cheat Guard` 四级联锁与运行安全红线**：
   - `Tier 0`~`Tier 3` 风险指纹识别、启动前高危进程/驱动（CE、`dbk64`、残留 Worker）熔断清场、`Tier 2/3` Explorer Shell COM 脱钩启动（切断 PPID 血缘）是否闭环；
   - 受保护游戏退出后，是否持续确认 `SGuard64.exe` / `EasyAntiCheat_EOS.exe` / `BEService.exe` 彻底退出并严格执行 **15 秒余温冷却窗口** 后才解除对 `BlueprintWorker` / `TrainerWorker` 的互锁；
   - 游戏生命周期追踪 `JobObject` 是否**严禁设置** `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，而外部修改器/子脚本托管 `JobObject` 是否**强制设置**该标志；
   - 外部修改器静音是否坚持零改 EXE 的 `A0+A`（`SHGetKnownFolderPath` 双目录 INI + 27 字节 5.0 秒静音 SMF + 专用 MTA 线程全输出端点 WASAPI 会话监听，默认完全静音）方案；`Spawn.TrainerWorker` 是否零跨进程内存 API、倒置管道双向互验、`KILL_ON_JOB_CLOSE` 原子入组且发布于 `Workers\Trainer\`；热键代理是否使用 `MAPVK_VK_TO_VSC_EX` + `EXTENDEDKEY` 且默认不设 `SCANCODE`；二期 CE `.CT` 指针偏移解析是否严格执行 `<Offsets>` `.Reverse()`；WOF 压缩是否在检出 `dstorage.dll` 时强制熔断。
4. **WAL / NTFS Junction 原子防丢档与云同步安全**：
   - `spawn.db`（`<应用数据根>\db\spawn.db`）是否开启 WAL + `foreign_keys=ON` 且与网盘目录隔离；通道数据根是否严格区分 `spawn` 与 `spawn-dev`，测试是否 100% 落在独立临时目录；
   - 多维 Profile 切换是否具备 5 阶段 `profile_switch_journal` 断电自愈能力；摘除存档槽 NTFS Directory Junction 时是否严格使用非递归 `RemoveDirectoryW` / `DirectoryInfo.Delete(recursive: false)`，杜绝误清空真实存档槽；
   - Mod 可变配置文件（`.ini`/`.json`/`.toml`）是否使用独立物理副本并在退出后用 `XxHash3` 回收差异，杜绝硬链接原地写污染；多端云同步是否基于 `ParentSnapshotId` Git 祖先链判定 Fast-Forward 与分叉，杜绝时间戳盲目覆盖。
5. **诚实运行证据、前台静音与文档注册表同步**：
   - 报告中的每项检查是否附真实命令与退出码；缺环境（如可选虚拟外设驱动尚未安装）、缺外设（如 KMBox 硬件盒）或待人工体验项是否诚实标记为 `not-run`，绝不把模拟夹具或静态文档通过冒充为产品实机验收通过；
   - 测试与门禁运行期间是否严守零弹窗、零抢焦点、零声音的前台静音约束；
   - `python scripts/check_docs.py` 与 `git diff --check` 是否零报错通过，新增或调整的文档是否已同步更新 `docs/governance/document-registry.json` 与 `docs/delivery/traceability.json`，全仓无本机绝对路径与外部兄弟项目名残留。

---

## 2. 审查发现严重度与处置规则

| 严重度 | 判定标准 | 处置要求 |
| :--- | :--- | :--- |
| **`P0`（致命红线）** | Clean Host 混入敏感 Win32 API、反作弊联锁/15s 冷却失效、递归误删存档槽、云同步静默覆盖丢档、凭据或本机隐私路径入库泄露 | **阻止合并**；必须修复并复跑门禁清零后再审 |
| **`P1`（合并阻断）** | 关联 issue 承诺的功能缺失、契约前后矛盾、核心状态机异常未处理、文档注册表或追踪矩阵失步、将 `not-run` 虚报为 `passed`、碰了[高风险清单](docs/dev-rules/development-workflow.md#high-risk)却没加 `risk:high` | **阻止合并**；本 PR 内修复并复审 |
| **`P2`（局部瑕疵）** | 不影响反作弊安全、数据完整性与核心验收的边缘体验或可优化点，已有明确降级保护 | 能修则修；否则建 issue 写明触发条件 |
| **`P3`（表达建议）** | 纯措辞润色、注释排版或非契约性风格偏好 | 不构成返工理由，不制造额外审查轮次 |

> [!IMPORTANT]
> - 每条阻塞发现写清：严重度、文件路径/行号、触发条件、实际表现 vs 契约预期、验证方法。
> - 审查者只审不改：问题写进 PR 评论，由作者修复；修复后由同一审查者或新的审查者复核阻塞项，不要求全量重审。

---

## 3. 审查结论

结论写在 PR 评论里：**可合并** 或 **先修复**，再列阻塞问题与至多三条非阻塞建议。没发现问题时写明已审查的范围与没覆盖的部分（`not-run`）。实现与自检、技术审查、产品实机验收（真实游戏、真实修改器、外部服务）是三件事，分开下结论：技术审查可以通过而实机检查仍为 `not-run`，但不得声称产品验收通过；需要用户实机判断的，按[什么时候找用户](docs/dev-rules/development-workflow.md#user-actions)建 `needs-user` issue。
