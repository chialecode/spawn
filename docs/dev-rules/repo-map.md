# 仓库地图与 `Spawn.sln` 依赖隔离规范

实际落地进度以 `main` 上的代码与[需求追踪](../delivery/traceability.json)为准：`M1` 建立治理、架构契约、五大模块规格与 GitHub 配置基线；`M2` 起按本文件定义的物理隔离边界建立 `Spawn.sln` 多工程结构。

---

## 1. 仓库顶层目录所有权（`M1` 已建立与 `M2+` 演进）

| 仓库相对路径 | 状态 | 职责与所有权边界 |
| :--- | :--- | :--- |
| 根文档（`README.md`、`AGENTS.md`、`CLAUDE.md`、`CONTRIBUTING.md`、`DESIGN.md`、`REVIEW.md`、`SECURITY.md`、`LESSONS.md`） | `M1` 已建立 | 项目门面、AI/开发者工作指令唯一正本、审查口径、安全声明与开发教训 |
| `.github/`（`workflows/ci.yml`、`workflows/release.yml`、`dependabot.yml`、`rulesets/main.json`、`repository-settings.json`、`actions-policy.json`、`PULL_REQUEST_TEMPLATE.md`、`ISSUE_TEMPLATE/`） | `M1` 已建立 | 远端仓库设置镜像、`main-protection` 规则集、Actions 白名单、NuGet/Actions 7 天冷却 Dependabot 与 CI 必需检查 `repository-quality`（DCO、文档、构建与测试）；本机 pre-push 门禁在 `.githooks/pre-push` |
| `docs/governance/`、`docs/product-rules/`、`docs/design-rules/`、`docs/dev-rules/`、`docs/templates/` | `M1` 已建立 | 文档治理政策、`document-registry.json` 注册表、五大产品不变量、WPF/Nodify/Overlay 视觉规范、工程协作规则与写作模板 |
| `docs/requirements/`、`docs/decisions/`、`docs/design/`、`docs/engineering/`、`docs/delivery/` | `M1` 已建立 | 产品需求正本、ADR-0001~0005 与决策台账、系统架构/数据/IPC 契约、开源选型与环境清单、里程碑路线图与追踪矩阵 |
| `docs/modules/`、`docs/research/` | `M1` 已建立 | S.P.A.W.N. 五大领域模块正式实现规格书（`active`）及前期源码级深度调研档案（`reference`） |
| `assets/`、`installer/` | `M2` 已建立 | 双通道应用图标（矢量正本 `icon.svg` / `icon-dev.svg` 与栅格产物 `icon.ico|png`、带琥珀色 `DEV` 角标的 `icon-dev.ico|png`，规格见 [DESIGN.md](../design-rules/DESIGN.md)）及 NSIS 3.13 按用户免提权安装脚本（`installer/spawn.nsi`，[D-35](../decisions/confirmed-decisions.md#d-35)） |
| `scripts/` | `M1`~`M2` 已建立 | 零外部依赖 Python 3.12+ 门禁与打包脚本（`check_docs.py`、`quick_gate.py`、`generate_icons.py`、`package.py --install-dev`） |
| `src/`、`tests/`、`Directory.Build.props`、`Directory.Packages.props`、`global.json`、`Spawn.sln` | `M2` 已建立并按阶段扩充 | 纯原生 `.NET 10`（LTS）多进程物理隔离源码树、中央包版本管理（CPM）与 `PEReader` 架构门禁测试 |
| `.local/`、`SPAWN_Vault/`、`bin/`、`obj/`、`dist/` | `.gitignore` 忽略 | 本地隔离开发数据、门禁测试原始日志、构建中间产物与安装包暂存目录（严禁提交入库） |

---

## 2. `Spawn.sln` 物理隔离多工程结构与职责（`M2`~`M6` 目标拓扑）

为从 MSBuild 编译期与项目引用图（Project Reference Graph）根源上杜绝开发人员误将高敏内存/钩子/视觉 API 引入常驻主界面，`Spawn.sln` 严格拆分为以下子工程：

```text
Spawn.sln
├── Directory.Build.props                # 统一 net10.0 / net10.0-windows10.0.26100.0、Nullable=enable、ImplicitUsings、TreatWarningsAsErrors
├── Directory.Packages.props             # NuGet 中央包版本管理（Central Package Management），全解决方案锁定唯一兼容版本
├── src/
│   ├── Spawn.Contracts/                 # 零业务/零 Win32 纯契约层：DTO、枚举、Protobuf (.proto) IPC 定义、System.Text.Json SourceGen
│   ├── Spawn.Core/                      # 纯净共享底座：SQLite WAL 仓储、AntiCheatGuard 预检与 15s 冷却状态机、0x1000 安全进程查询、DPAPI 凭据、NamedPipe SDDL ACL 封装
│   ├── Spawn.Modules.Launcher/          # [Spawn] JobObject IOCP + 0x1000 破壳追踪、三态精准时长、CCD HDR/转区/CPU Sets 自愈管线、三级漏斗离线刮削
│   ├── Spawn.Modules.Nexus/             # [Nexus] CAS 硬链接部署 + XxHash3 可变配置回收、5 阶段 WAL + NTFS Junction 原子切换、四大引擎编排与极客工具箱纯净侧
│   ├── Spawn.Modules.Progress/          # [Progress 纯净侧，M3 已建项] 洁净室 FLiNG 发现/解析/下载、本地导入、A0+A WASAPI 会话静音、启动前轻量存档快照、Defender 排除助手；M4 轨道：Ludusavi/FastCDC/SPPK
│   ├── Spawn.Modules.Achievement/       # [Achievement 纯净侧] 15+ 离线模拟器解析、四层 FSW 状态机、Comet 127.0.0.1:9977 GOG 守护、隐藏成就瀑布流、gbe_fork 事务激活器
│   ├── Spawn.Modules.Workflow/          # [Workflow 纯净侧] 二次元排班配置模型、interface.json 元数据映射、蓝图有向图序列化契约与主宿主调度代理
│   │
│   ├── Spawn.Host/                      # 🟢 常驻桌面主进程 EXE (Medium IL)：WPF-UI 壳层 + Nodify 双画布编辑器（仅允许引用纯净层，受 IAT 门禁硬锁！）
│   ├── Spawn.OverlayHost/               # 🔵 独立置顶覆盖层 EXE (Medium+UIAccess IL)：DirectComposition+SkiaSharp 144Hz 弹窗 + Alt+Z 锁鼠交互面板（受 IAT 门禁硬锁！）
│   │
│   ├── Spawn.BlueprintWorker/           # 🟠 按需隔离子进程 EXE：Kismet 异步蓝图 VM + RawInput + WGC/OpenCvSharp4/PaddleOCR + SendInput/PostMessage/ViGEmBus/KMBox
│   ├── Spawn.TrainerWorker/             # 🟠 按需隔离子进程 EXE（M3 已建项，asInvoker，发布于 Workers\Trainer\；按需 Medium/High IL）：修改器 KILL_ON_JOB_CLOSE 原子托管 + MAPVK_VK_TO_VSC_EX 热键代理（M3 零跨进程内存 API）+ M6 收窄原生修改后端
│   ├── Spawn.WorkflowWorker/            # 🟠 按需隔离子进程 EXE (High IL)：并发资源锁 DAG + 内嵌 Maa.Framework C# + MaaFgo/BBC 双端口桥接 + 二游 CLI 适配器群 + 唤醒/防黑屏/战报长图
│   └── Spawn.LegacyHook.Helper/         # ⚪ 默认不加载可选包：仅面向极少数纯离线老独占全屏单机的 RTSS OSD / Vulkan Layer 辅助
└── tests/
    ├── Spawn.Architecture.GateTests/    # ★ 一票否决门禁：校验 csproj 引用图 + PEReader 静态扫描 Host/OverlayHost/Modules.Progress 零敏感导入 + TrainerWorker 零跨进程内存/调试 API + 全仓 project.assets.json 禁用包审计
    ├── Spawn.Core.UnitTests/            # M2~M3 核心与领域单测：SpawnPaths/SQLite v4 迁移与 CRUD/DPAPI、反作弊四层判定与看门狗、16+ 模拟器、GOG Comet、LIMBO 原生存档、进程追踪与 IPC
    ├── Spawn.Modules.Progress.UnitTests/# M3 已建项：Progress 模块目录发现/下载导入、A0+A WASAPI 静音、启动前快照、Defender 助手单测
    ├── Spawn.TrainerWorker.UnitTests/   # M3 已建项：TrainerWorker 作业对象、进程树清零、命令白名单与热键映射单测（含 TestStub 复制输出）
    ├── Spawn.TrainerWorker.TestStub/    # M3 已建项：无窗口无害合成多子进程桩程序（供 TrainerWorker 作业对象隔离测试使用）
    ├── Spawn.Achievement.Tests/         # (M4+ 按需拆分专项) 扩展成就样本回归与高刷压力夹具
    └── Spawn.Integration.Tests/         # (M4+ 按需扩充) 多 Worker 跨进程端到端集成测试
```

---

## 3. 项目引用方向与 NuGet 边界硬约束（强制执行）

```mermaid
flowchart TD
    Contracts["Spawn.Contracts<br/>(纯 DTO / Protobuf / 零 Win32)"]
    Core["Spawn.Core<br/>(SQLite WAL / AC-Guard / 0x1000 / PipeSecurity)"]
    Mods["Spawn.Modules.Launcher / Nexus<br/>Progress / Achievement / Workflow"]
    Host["🟢 Spawn.Host.exe<br/>(Medium IL | WPF-UI + Nodify 画布)"]
    Overlay["🔵 Spawn.OverlayHost.exe<br/>(UIAccess IL | DirectComposition + Alt+Z)"]
    Workers["🟠 Spawn.BlueprintWorker.exe<br/>🟠 Spawn.TrainerWorker.exe<br/>🟠 Spawn.WorkflowWorker.exe"]

    Host --> Mods
    Host --> Core
    Host --> Contracts
    Mods --> Core
    Mods --> Contracts
    Core --> Contracts
    Overlay --> Contracts
    Overlay --> Core
    Workers --> Contracts
    Workers --> Core
```

1. **严禁反向或跨宿主耦合**：
   - `Spawn.Contracts` 严禁引用任何其他项目或平台专属 UI/Win32 包；
   - `Spawn.Host`、`Spawn.OverlayHost` 及五个 `Spawn.Modules.*` **严禁直接或间接引用任何 `Spawn.*Worker` 或 `Spawn.LegacyHook.Helper` 项目**；
   - 各个 `Spawn.*Worker` 之间彼此独立，严禁相互项目引用，所有状态协同统一经 `Spawn.Host` 的 `NamedPipeJsonRpc` 总线仲裁。
2. **高敏与重型 NuGet 隔离红线**：
   - `OpenCvSharp4`、`Sdcb.PaddleOCR`、`Nefarius.ViGEm.Client` 仅允许出现在 `Spawn.BlueprintWorker`；
   - `Iced`（`M6`）、`Vortice.XInput`（`LOOP-08`）仅允许出现在 `Spawn.TrainerWorker`，`AlphaVSS` 仅允许出现在 `M4` 存档时间机器的独立提权辅助进程；`Reloaded.Memory` 家族全仓禁止；`SharpCompress`、`AngleSharp` 仅允许出现在 `Spawn.Modules.Progress`；
   - `Maa.Framework`、`Hi3Helper.Sophon` 仅允许出现在 `Spawn.WorkflowWorker`；
   - 违反上述任一规则将直接触发 `Spawn.Architecture.GateTests` 编译与门禁失败（详见 [质量门禁](quality-gates.md)）。
3. **按里程碑渐进建项原则**：只创建当前里程碑有真实代码与消费者的 `.csproj` 子工程，不为尚未进入的远期阶段预先堆砌空壳代码库。
