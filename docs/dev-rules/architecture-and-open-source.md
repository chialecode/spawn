# 开源复用、许可证兼容与架构边界规则

触发条件：技术选型评估、引入新 NuGet 依赖、对接第三方外部工具或决定自研基础设施。已核验的 35+ 标杆开源仓库与统一 NuGet 矩阵见 [技术选型与环境清单](../engineering/technology-selection.md)。

---

## 1. 活跃开源优先查证铁律（Open-Source First）

1. **规划期必须完成源码级查证**：
   - Agent A 在制定阶段计划或推荐技术组件前，必须核查目标开源仓库的官方地址、默认分支近期功能提交（`pushed_at` / 最新 commit，不轻信仅修改 README 的 `updated_at`）、最新 Release、`.NET 10` / Windows 10/11 x64 兼容性及许可证条款，并将结论记入选型文档；严禁凭过时记忆拍脑袋自研，也严禁把首次可行性调研推给实施阶段的 Agent B。
   - Agent B 在接入阶段通过 `Directory.Packages.props` 锁定精确兼容版本，跑通最小闭环并提供构建与运行证据。
2. **四级复用梯度**：
   - 遇到功能缺口时严格按 **“① 直接使用成熟开源 NuGet 包现成 API → ② 编写非侵入薄适配器（Thin Adapter） → ③ 提交最小隔离封装补丁 → ④ 经文档论证无合适方案后定向自研”** 推进。
   - SPAWN 的核心自研精力集中投入在 **跨模块联动状态机、Anti-Cheat Guard 物理隔离总线、Kismet 双引脚异步调试解释器、FastCDC + Junction 原子事务及多生态统一编排协议**，绝不重复自研节点画布底层渲染、OCR 推理引擎、Zstd/BLAKE3 算法或单款二次元游戏关卡识图脚本。
3. **上游缺陷追踪**：发现上游开源库 Bug 或平台限制时，在 [集中工程事项台账](../decisions/open-questions.md) 登记 `OSS-*` 条目（含锁定版本、触发条件、当前绕行封装及未来移除/升级条件）。

---

## 2. Apache-2.0 许可证兼容性红线

SPAWN 自有源码与文档采用 **[Apache License 2.0](../../LICENSE)** 开源协议，引入任何第三方依赖必须严格区分 **“进程内链接/打包分发（NuGet / 原生 DLL）”** 与 **“进程外独立工具编排（CLI / IPC）”** 两类边界：

| 许可类别 | 代表许可证 | 允许集成方式与合规义务 |
| :--- | :--- | :--- |
| **宽松兼容型（绿灯）** | `MIT`、`Apache-2.0`、`BSD-2/3-Clause`、`ISC`、`Zlib` | 可直接作为 NuGet 依赖编译进 `Spawn.*` 二进制并随安装包分发；发布打包时保留原始版权声明与 `THIRD-PARTY-NOTICES.txt` |
| **弱传染文件级（黄灯，受控使用）** | `MPL-2.0`、`LGPL-2.1/3.0`（动态链接不修改库源码） | 允许以未修改的官方标准 NuGet / 动态链接 DLL（如 `Maa.Framework` LGPL-3.0 动态库）形式在对应隔离 Worker 中加载；严禁静态链接或直接复制修改其源码并入自有 `.cs` 文件，且必须在第三方通知中注明上游源码获取地址 |
| **强传染同进程禁区（红灯）** | `GPL-2.0`、`GPL-3.0`、`AGPL-3.0` | **严禁作为 NuGet 包、源码拷贝或同进程内存链接库引入 SPAWN 任何工程！** 若涉及外部开源工具（如 `BetterGI`、`StarRailCopilot`、`Locale-Emulator` 等 GPL 项目），仅允许用户在本地独立安装，由 SPAWN 通过**标准命令行参数、配置文件读写或操作系统进程句柄在进程外松耦合启动调度**，绝不打包分发其受 GPL 约束的代码或衍生二进制 |
| **闭源免费外部工具** | 独立版 `FLiNG Trainer`、`BBChannel`、模拟器官方 CLI（`MuMuManager`） | 绝不逆向反编译重打包、绝不篡改其原版 `.exe` 二进制、绝不在仓库或安装包内内置分发；仅在用户本地已持有该工具的前提下提供无侵入外部目录/配置/协议协同 |

---

## 3. 核心架构解耦与单向契约准则

1. **UI 层与领域核心严格解耦**：
   - WPF 视图与 ViewModel（`Spawn.Host` / `Spawn.OverlayHost`）仅负责状态展示、命令绑定与画布交互，严禁在 `.xaml.cs` 或 ViewModel 内手写 SQL 语句、直接拼装 NTFS 硬链接事务或直接调用 `CreateProcess`；所有业务状态变更统一经领域应用服务执行，确保主界面、游戏内 `Alt+Z` 面板、托盘快捷菜单与自动化排班共享同一份状态真相。
2. **IPC 契约显式版本化**：
   - 主进程与各隔离 Worker 之间的通信契约统一集中在 `Spawn.Contracts` 的 Protobuf（`.proto`）与强类型记录中定义，附带请求超时 `CancellationToken`、Worker 运行代次（Generation ID）与结构化错误码，防止 Worker 重启或被 `Anti-Cheat Guard` 强杀后的晚到消息污染主界面状态。
3. **无消费者不预造抽象**：保持接口精炼直接（如 `ITrainerBackend`、`IInputActuator`、`IWorkflowRunnerAdapter` 均有明确的一期/二期多实现消费者），拒绝过度设计无实际落地的空泛中间件。
