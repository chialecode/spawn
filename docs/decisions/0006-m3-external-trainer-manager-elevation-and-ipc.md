# ADR-0006：`M3` 外部修改器安全管家——分级按需提权、倒置跨 IL 管道与洁净室依赖边界

- **状态**：Accepted
- **日期**：2026-11-21
- **维护角色**：维护者
- **关联决策与需求**：`D-04`、`D-11`、`D-13`、`D-23`、`D-25` ~ `D-29`；`REQ-PRG-04` ~ `REQ-PRG-08`、`REQ-SEC-03`、`REQ-SEC-05`、`REQ-SEC-06`、`NFR-07`
- **部分替代**：[ADR-0003 §3.3](0003-anti-cheat-guard-and-process-isolation.md) 中“首次引导注册计划任务静默拉起 High IL Worker”与“`gRPC over Named Pipes`”两项表述；[ADR-0005 §3.2](0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) 中修改器静音参数与二期 `.CT` 依赖表述

---

## 1. 背景与问题约束

`M3` 技术调研（`M3-R01`，核验日 2026-11-21）对既有修改器正本做了源码级与官方文档级复核，发现 4 项必须在编码前改写的架构级前提：

1. **本地提权（LPE）风险**：`M2` 按 [D-23](confirmed-decisions.md) 改为按用户免 UAC 安装至用户可写的 `%LOCALAPPDATA%\Programs\spawn[-dev]\`，`%ProgramFiles%\spawn\` 并不存在。原 `D-11` / [IPC 契约 §6.1](../design/ipc-and-security-contracts.md) 的 `HighestAvailable` 计划任务一旦指向用户可写目录（或由受保护启动器加载可写目录内的 .NET DLL 闭包），任何同用户 Medium IL 进程替换二进制即可无 UAC 获得管理员权限（MITRE `T1053.005` / `T1574.001`，`CWE-732` / `CWE-427`）。
2. **跨 IL 管道被 MIC 拦截**：若 High IL Worker 作为命名管道服务端，Windows 强制完整性控制（MIC，`NO_WRITE_UP`）会在 DACL 检查之前拒绝 Medium IL `Spawn.Host` 写入（`ERROR_ACCESS_DENIED`）；现有 `NamedPipeSecurityFactory` 仅配置 DACL、未配置 SACL 强制标签。
3. **许可证传染**：`Reloaded.Memory` 自 `9.x`（2023-05）起改为 `GPL-3.0`，`Reloaded.Memory.SigScan 3.1.7+` 强制传递依赖 `Reloaded.Memory >= 9.1.0`；`SevenZipExtractor` 随包捆绑受 `LGPL-2.1 + unRAR` 限制条款约束的原生 `7z.dll`；三个公开 FLiNG 管理器参考实现分别为 `GPL-3.0` / `AGPL-3.0`。
4. **`.CT` 引擎可行性**：68 份真实社区 `.CT` 表静态审计显示 54.4%（现代表 78.6%–100%）依赖 Lua、全部纯 AA 表含原始文本汇编，而 `Iced` 无文本汇编器、`Keystone` 为 `GPL-2.0`，“实现 5 条基础 AA 指令覆盖 90% 单机表”的旧假设不成立。

---

## 2. 候选方案与查证证据

| 评估维度 | 方案 A：计划任务免 UAC + Worker 作管道服务端（原 `D-11`） | 方案 B：分级按需 UAC + Host 作管道服务端（倒置） | 对比结论 |
| :--- | :--- | :--- | :--- |
| 安装形态兼容 | 需 `%ProgramFiles%` 受 ACL 保护的自包含提权包，与 `D-23` 按用户静默安装/更新冲突 | 100% 兼容 `D-23`：Worker 与主程序同目录发布于 `Workers\Trainer\` | **B 胜** |
| 提权攻击面 | 指向用户可写目录即构成无 UAC LPE | 每次修改器会话由用户显式确认 1 次 UAC，零静默高权限入口 | **B 胜** |
| 跨 IL 连通性 | High IL 服务端需额外 SACL 低完整性标签 P/Invoke，否则 Medium 客户端被 MIC 拒绝 | High → Medium 写入天然满足 MIC；复用 `M2` 已有 `NamedPipeJsonRpcServer` | **B 胜** |
| 用户体验 | 零弹窗 | 修改器声明 `requireAdministrator` 时每会话 1 次 UAC（与玩家直接双击 FLiNG 相同），会话内数十次开关免二次弹窗；无管理员清单的修改器 0 次 UAC | 可接受 |

依赖与引擎维度：`SharpCompress 0.50.9`（MIT，纯托管，`.zip`/`.rar` RAR5 & Solid/`.7z`）、`AngleSharp 1.10.3`（MIT）、`Microsoft.Windows.CsWin32 0.3.371`（MIT，编译期源生成）均已过 7 天冷却（截止 2026-11-14）；`Reloaded.*`、`SevenZipExtractor`、`Squid-Box.SevenZipSharp`、`Keystone`、`Memory.dll` 全部判红。

---

## 3. 最终裁决与自研/复用边界

1. **分级按需提权（替代 `D-11` 的计划任务路径）**：
   - **Level 1（0 次 UAC）**：目标修改器 PE 清单未声明 `requireAdministrator` 且目标游戏为 Medium IL 时，`Spawn.Host` 直接以 Medium IL 启动 `Spawn.TrainerWorker.exe`；
   - **Level 2（`M3` 默认，零 LPE）**：修改器声明 `requireAdministrator`（或目标游戏为 High IL）时，经 `ShellExecuteExW`（`Verb = "runas"`、`SEE_MASK_NOCLOSEPROCESS`）按需拉起 High IL Worker，每个修改器会话仅 1 次 UAC，子进程继承 High IL；
   - **Level 3（顺延 `LOOP-07`）**：仅当未来发布 100% 自包含、受管理员 ACL 保护的 `%ProgramFiles%\SPAWN\Worker\` 独立提权包时，才允许计划任务免 UAC；**严禁**为 `%LOCALAPPDATA%` 下任何文件注册高权限计划任务；**否决** `SYSTEM` Windows 服务（Session 0 隔离破坏 `SendInput`）。
2. **倒置跨 IL `NamedPipeJsonRpc`（替代 `gRPC over Named Pipes` 表述）**：Medium IL `Spawn.Host` 托管修改器会话管道服务端（单实例 `maxNumberOfServerInstances = 1` + `FirstPipeInstance`，管道名含每会话 128-bit 随机后缀），`Spawn.TrainerWorker` 作为客户端反向连入；双向鉴权：Host 校验 `GetNamedPipeClientProcessId` 等于 `ShellExecuteExW`/`CreateProcessW` 返回的 Worker PID、`0x1000` 回读路径等于 `<InstallDir>\Workers\Trainer\Spawn.TrainerWorker.exe`、首帧携带 256-bit CSPRNG `LaunchNonce`；Worker 校验 `GetNamedPipeServerProcessId` 等于启动参数中的父 Host PID 且路径为同安装目录 `Spawn.Host`（spawn/spawn-dev 主程序），否则立即退出。Worker 以 `SYNCHRONIZE` 监视 Host PID 与管道断连，任一失效即自毁并触发 `KILL_ON_JOB_CLOSE`。
3. **Worker 命令白名单（纵深防御）**：Worker 只接受沙箱根 `vault\Sandbox\Trainers\` 内、且本地重算 SHA-256 与 Host 下发钉扎值一致的可执行文件；拒绝任意绝对路径、命令行注入与沙箱外工作目录。
4. **零内存 API 的 `M3` Worker**：`Spawn.TrainerWorker` 在 `M3` 仅使用 `CreateJobObjectW` / `PROC_THREAD_ATTRIBUTE_JOB_LIST` / IOCP / `SendInput` / `MapVirtualKeyW` / `PostMessageW` 等 API，**零** `ReadProcessMemory` / `WriteProcessMemory` / `VirtualAllocEx` / `CreateRemoteThread` 导入，由 `Spawn.Architecture.GateTests` 增设断言守卫；`M6` 引入 `.CT` 子集时再单独扩展门禁白名单。
5. **洁净室依赖边界**：全部 FLiNG 发现、解析、下载与解压模块 100% 原创实现，严禁复制或移植任何 `GPL` / `AGPL` 参考管理器代码、正则或标题词典；全仓移除 `Reloaded.Memory` 家族，`M6` 特征码扫描改用 `.NET 10` `Vector256` / `Avx2` 自研；压缩包解压仅用 `SharpCompress` + BCL `System.IO.Compression`，异常兜底调用用户本机已安装的 `7z.exe`（模式 C，严禁随包分发）。

---

## 4. 架构影响、避坑红线与退出条件

1. **上下游契约影响**：
   - `Spawn.Contracts` 新增修改器会话 DTO 与 `ITrainerBackend` 等接口；`Spawn.Core` 新增单实例修改器管道服务端工厂与客户端/服务端 PID 校验辅助；`SpawnPaths` 新增 `TrainerSandboxDirectory`、`PreTrainerSnapshotsDirectory`、`TrainerPipeNamePrefix`。
   - `scripts/package.py` 必须把 `Spawn.TrainerWorker` 独立发布到 `stage_app\Workers\Trainer\`，严禁平铺于主程序根目录（否则触发 `CleanHostArchitectureGateTests` 顶层目录 IAT 扫描失败）；`--quit-for-update` 先优雅关闭活跃 Worker。
2. **强制避坑核查项**：
   - 修改器专用 Job 必须 `KILL_ON_JOB_CLOSE = ON`、`BREAKAWAY_OK = OFF`、`SILENT_BREAKAWAY_OK = OFF`，以 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 原子入组；与游戏追踪 Job 的“严禁 `KILL_ON_JOB_CLOSE`”方向严格相反，两者代码不得共用。
   - 管道服务端绝不使用 `MaxAllowedServerInstances`；客户端必须校验服务端 PID，防止同用户进程抢注同名管道向 High IL Worker 下发指令。
   - UAC 仅由用户显式点击“启动修改器”触发，严禁在 SPAWN 启动、扫描或后台轮询时弹出。
3. **可逆性与替换触发条件**：若未来正式版改回 `%ProgramFiles%` 管理员安装形态，可按 `LOOP-07` 评估 Level 3 计划任务；若 `.NET` 原生命名管道 API 支持便捷的 SACL 强制标签配置，倒置角色仍保留（不依赖该能力）。

---

## 5. 验证方式

- 伪造 Nonce、非 Worker 路径客户端或非 Host 服务端的连接在单元测试中 100% 被拒绝；
- 合成无害多子进程 Stub 实测：Worker 被强杀或 Host 断连后，修改器子孙进程全部由内核清零；
- `Spawn.Architecture.GateTests` 断言 `Spawn.TrainerWorker` 零跨进程内存读写导入、Clean Host 未引用 Worker、打包产物 `Workers\Trainer\` 目录隔离；
- `project.assets.json` 传递依赖树中不出现任何 `GPL` / `AGPL` / `LGPL` 包。
