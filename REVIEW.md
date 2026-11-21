# SPAWN 集中技术审查口径

本文件定义 Agent A 与 Swarm `@verifier` 对阶段交付物的集中技术审查标准。审查者必须直接核查实际 `git diff`、源码树、门禁退出码与产物证据，绝不能仅复述实现者的口头总结。前置协作与门禁背景见 [开发协作流程](docs/dev-rules/development-workflow.md) 与 [质量门禁](docs/dev-rules/quality-gates.md)。

---

## 1. 核心审查顺序（SPAWN 专项五大门禁）

1. **范围、需求与决策一致性**：
   - 实际改动是否严格对应当前 `docs/delivery/stage-plan.md`、关联 `REQ-*` 需求、[已确认决策](docs/decisions/confirmed-decisions.md) 与五大模块规格书（`docs/modules/*`），有无擅自缩减核心能力或越界改动无关模块。
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
| **`P0`（致命红线）** | Clean Host 混入敏感 Win32 API、反作弊联锁/15s 冷却失效、递归误删存档槽、云同步静默覆盖丢档、凭据或本机隐私路径入库泄露 | **立即阻断交付**；必须修复并复跑门禁清零后方可继续 |
| **`P1`（阶段阻断）** | 当前阶段承诺的工作包功能缺失、契约前后矛盾、核心状态机异常未处理、文档注册表或追踪矩阵失步、将 `not-run` 虚报为 `passed` | **阻断当前阶段收敛**；阶段内修复并复验通过 |
| **`P2`（局部瑕疵）** | 不影响反作弊安全、数据完整性与阶段核心验收的边缘体验或可优化点，已有明确降级保护 | 能在当前阶段顺手修则修；否则登记入 [集中工程事项台账](docs/decisions/open-questions.md) `LOOP-*` 并明确承接阶段 |
| **`P3`（表达建议）** | 纯措辞润色、注释排版或非契约性风格偏好 | 不构成返工理由，不制造额外审查轮次 |

> [!IMPORTANT]
> - 每条审查发现统一使用稳定编号 `F-01`、`F-02`……记录：**严重度、触发条件、实际表现 vs 契约预期、精确文件路径/行号、验证方法**。
> - **单会话 A/B 模式**：局部确定性小问题由 Agent A 直接修复并复跑门禁后 amend 进阶段提交；跨模块较大缺口汇总为单次完整 `F-*` 清单退回 Agent B。
> - **Swarm 多智能体模式**：`@verifier` 严格保持只读独立验收身份，**绝不直接代改其他 Worker 负责的文件**；发现任何问题在消息板精准 `@<责任 Worker>` 附 `F-*` 清单要求修复，修复后由 `@verifier` 复跑门禁关闭。

---

## 3. 三维结论分离与收敛后移交

每次集中审查结束必须分别明确给出三项正交结论，严禁混为一谈：

1. **实现与自检状态**（工作包与自动化门禁完成度）；
2. **集中技术审查结论**（`reviewed` 通过 / `changes-requested` 需返工）；
3. **产品阶段实机验收状态**（含用户人工检查 `H-*` 与外部真实服务 `ACT-*`，未测前保持 `pending` / `not-run`）。

当技术审查达到 `reviewed`、当前阶段可运行的自动化门禁全部通过且 `P0`/`P1` 阻断项归零时：

- 在本地阶段分支收束唯一带 DCO `Signed-off-by` 签名的阶段提交（核验父提交不变）；
- 拟定清晰的 PR 标题与正文草稿（含实际变更、已跑门禁、`not-run` 边界及 DCO 尾注）；
- 在 [USER-ACTIONS.md](USER-ACTIONS.md) 登记具体远端授权项（`ACT-*`），向用户请求授权推送阶段分支与创建 PR。顺延的人工检查（`H-*`）与非阻塞可选配置不是申请远端授权的前置阻塞条件。
