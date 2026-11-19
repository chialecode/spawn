# 质量门禁、两级验证与前台静音规范

本规范定义 SPAWN 从 `M1` 文档基线到 `M2`~`M6` 原生 C# 工程落地的自动化质量门禁体系。核心铁律：**只报告真实执行过的检查结果；缺环境、缺硬件或尚未建立的检查必须诚实标记为 `not-run`，绝不伪造或推导为 `passed`**。

---

## 1. 两级门禁体系设计（`M1` 已启用与 `M2+` 演进）

<a id="gate-tiers"></a>

为兼顾日常高频编码迭代的极速反馈与阶段交付时的严苛安全把关，SPAWN 实行**快速门禁 + 整阶段交付门禁**两级分层：

| 门禁层级 | 当前状态 | 触发运行场景 | 核心执行命令与检查内容 |
| :--- | :--- | :--- | :--- |
| **Level 0：文档与公开卫生门禁** | **`M1` 已启用并强制执行**（本地必跑 + GitHub Actions `docs` 必过检查） | 任何 `.md` / `.json` / `.github` 修改、每次 `git commit` 前及所有 PR | `python scripts/check_docs.py`<br/>`git diff --check`<br/>（Python 3.12+ 标准库零依赖，`< 3s` 完成全仓校验） |
| **Level 1：日常快速门禁（Quick Gate）** | `M2` 初始化 `Spawn.sln` 时同步建立 `scripts/quick_gate.py` | 阶段内工作包增量实现、审查局部修复、返工微调（目标耗时 `< 3 分钟`） | 依次串联：`check_docs.py` → `git diff --check` → `dotnet format --verify-no-changes` → 受影响子工程增量编译（`-warnaserror`）→ 核心单元测试与 `Spawn.Architecture.GateTests` 快速断言 |
| **Level 2：整阶段完整交付门禁（Stage Gate）** | `M2` 起按里程碑逐步扩充 | 仅在阶段全部工作包实现完毕移交集中审查（B→A / `@verifier`）时在同一源码指纹下完整跑一次 | Release 全量干净构建（`dotnet build Spawn.sln -c Release -warnaserror`）+ `Spawn.Architecture.GateTests` 真实二进制 `PEReader` IAT 全表扫描 + `Spawn.Core.Tests` / `Spawn.Achievement.Tests` / `Spawn.Integration.Tests` 全量确定性测试（15+ 离线模拟器与 Comet 9977 成就矩阵、FastCDC、WAL 断电恢复、Junction 防递归误删）+ NuGet 7 天冷却与已知漏洞审计（`dotnet list package --vulnerable`） |

---

## 2. `scripts/check_docs.py` 机械门禁校验清单（`M1` 已生效）

`scripts/check_docs.py` 仅使用 Python 3.12 标准库编写，无需安装任何第三方 pip 包（同时兼容本机 Windows PowerShell `python` 与 GitHub Actions Ubuntu runner `python3`），自动执行以下硬性检查：

1. **必需根入口与治理文件齐全性**：断言 `README.md`、`AGENTS.md`、`CLAUDE.md`（且内容严格等于 `@AGENTS.md`）、`CONTRIBUTING.md`、`DESIGN.md`、`REVIEW.md`、`SECURITY.md`、`USER-ACTIONS.md`、`LICENSE`、`docs/README.md`、`docs/project-baseline.md`、`docs/governance/documentation-policy.md`、`docs/governance/document-registry.json` 全部存在。
2. **文档注册表双向无遗漏对齐**：
   - 校验 `docs/governance/document-registry.json` 结构合法（`schemaVersion == 1`、`currentStage` 属于 `M1`~`M6`、无过期 `retireAfter` 滞留文档）；
   - 校验每条记录包含合法的 `path`、`kind`、`status`、`owner`、`scope`、`reviewed`（`YYYY-MM-DD`）、`basis`，且磁盘文件真实存在；
   - 反向扫描全仓所有自有 `.md` 文档（根级、`.github/`、`docs/`）与受管 `.json` 文档（`.github/*.json`、`docs/**/*.json`），任何未登记或重复登记的文件立即报错失败。
3. **全仓 Markdown 相对路径链接与锚点校验**：
   - 剥离围栏代码块后解析所有内联链接 `[...](...)` 与图片引用（禁止使用不易追踪的参考式链接 `[text][ref]`）；
   - 断言所有仓库内相对路径使用正斜杠 `/`、不越界逃逸仓库根目录、目标文件真实存在，且带 `#anchor` 的标题/显式 `<a id="...">` 锚点在目标 Markdown 中真实可定位。
4. **需求与里程碑追溯一致性（当 `traceability.json` 就绪时）**：校验 `docs/delivery/traceability.json` 中登记的需求编号、设计正本路径、模块规格路径与阶段验收用例引用真实有效、无悬空路径。
5. **公开卫生、敏感文件与跨项目零泄露扫描**：
   - 拦截任何误入工作区的数据库、密钥或证书文件（`.db`、`.sqlite`、`.sppk`、`.pfx`、`.p12`、`.key`、`.pem`、`.sec`、非示例 `.env`）；
   - 扫描全仓受管文本文件，硬性拦截个人机器绝对路径（如 Windows 用户目录盘符路径、个人代码盘绝对路径、Unix 用户主目录路径）、私钥/API Token 明文以及外部兄弟仓库名称或专属环境变量泄露。

---

## 3. `Spawn.Architecture.GateTests` PE 导入表零污染门禁（`M2` 起强制）

<a id="iat-gate"></a>

自 `M2` 创建 `Spawn.sln` 起，`tests/Spawn.Architecture.GateTests` 作为 CI 与本地交付的一票否决测试，必须同时通过两层静态断言：

1. **MSBuild 工程引用图断言**：解析 `src/**/*.csproj`，断言 `Spawn.Host`、`Spawn.OverlayHost`、`Spawn.Contracts`、`Spawn.Core` 与 `Spawn.Modules.*` 绝未直接或传递引用任何 `Spawn.*Worker` 工程及高敏 NuGet 包（`OpenCvSharp4`、`Sdcb.PaddleOCR`、`Reloaded.Memory.SigScan`、`Iced`、`Maa.Framework`、`Nefarius.ViGEm.Client`）。
2. **编译产物二进制 `PEReader` IAT 符号黑名单断言**：使用 `.NET` 标准库 `System.Reflection.PortableExecutable.PEReader` 直接读取构建产出的 `Spawn.Host.exe`、`Spawn.OverlayHost.exe` 及其独有程序集的导入表（Import Directory / Delay-Load Directory），一旦检出以下任一高敏 Win32/NTAPI 符号立即判门禁失败：
   - 跨进程内存与执行：`ReadProcessMemory`、`WriteProcessMemory`、`VirtualAllocEx`、`VirtualProtectEx`、`CreateRemoteThread`、`CreateRemoteThreadEx`、`NtWriteVirtualMemory`、`NtReadVirtualMemory`、`QueueUserAPC`、`NtSuspendProcess`、`NtResumeProcess`；
   - 全局输入钩子与模拟注入：`SetWindowsHookExA`、`SetWindowsHookExW`、`SendInput`、`keybd_event`、`mouse_event`（注：此类输入动作仅允许存在于按需启停的隔离 `BlueprintWorker` / `TrainerWorker` 中，严禁存在于常驻 `Host` 或 `OverlayHost`！）。

---

## 4. 测试与开发期命令前台无干扰（静音）硬约束

<a id="foreground-quiet"></a>

无论由人类开发者在本地运行，还是由 AI Agent 在后台执行构建、测试与门禁脚本，必须严格遵守**零前台打扰**纪律：

| 潜在干扰源 | 强制工程约束 |
| :--- | :--- |
| **命令行黑框闪烁** | 所有门禁脚本与产品进程拉起子进程时必须显式设置 `CreateNoWindow = true` / `UseShellExecute = false`（Python `subprocess` 在 Windows 下附带 `CREATE_NO_WINDOW` 标志） |
| **抢夺窗口焦点或弹窗** | 单元测试默认在无头（Headless）纯内存/临时文件层验证 ViewModel、状态机与解析器；确需验证真实 HWND / `DirectComposition` 冒烟时必须仅在显式开关下以不激活隐藏态（`WS_EX_NOACTIVATE` / `SW_SHOWNOACTIVATE`）创建并立即在专用 Job 中销毁，严禁切走用户当前正在操作的前台窗口焦点 |
| **系统错误框与防火墙弹窗** | 门禁入口设置 `SetErrorMode(SEM_FAILCRITICALERRORS \| SEM_NOGPFAULTERRORBOX)`；所有进程间通信测试走本地唯一前缀 Named Pipe，Comet GOG 协议回环测试严格仅绑定 `127.0.0.1:0` 随机回环端口，严禁绑定 `0.0.0.0` 触发 Windows 防火墙弹窗 |
| **音频外放与系统提示音** | 测试期间严禁向物理扬声器播放成就音效或触发终端响铃符 `\x07`；WASAPI 静音控制测试使用模拟音频会话接口或零音量静音夹具 |
| **自动打开外部应用** | 开发与测试命令严禁调用 `explorer.exe`、`Start-Process <url>` 或 `gh ... --web` 自动打开浏览器或资源管理器 |
| **CPU 满载拖慢前台** | 后台门禁运行器默认将自身设为 `BELOW_NORMAL_PRIORITY_CLASS` 低优先级，子进程通过带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的测试守护 Job 统一超时收口，严禁遗留失控后台进程 |
