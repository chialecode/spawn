# 质量门禁、两级验证与前台静音规范

本规范定义 SPAWN 从文档治理到 `.NET 10 (LTS)` 原生 C# 工程落地的自动化质量门禁体系。核心铁律：**只报告真实执行过的检查结果；缺环境、缺硬件或尚未建立的检查必须诚实标记为 `not-run`，绝不伪造或推导为 `passed`**。

---

## 1. 两级门禁体系设计（`M1`~`M2` 已启用与后续演进）

<a id="gate-tiers"></a>

为兼顾日常高频编码迭代的极速反馈与阶段交付时的严苛安全把关，SPAWN 实行**快速门禁 + 整阶段交付门禁**两级分层：

| 门禁层级 | 当前状态 | 触发运行场景 | 核心执行命令与检查内容 |
| :--- | :--- | :--- | :--- |
| **Level 0：文档与公开卫生门禁** | **`M1` 起已启用并强制执行**（本地必跑 + GitHub Actions `docs` 必过检查） | 任何 `.md` / `.json` / `.github` 修改、每次 `git commit` 前及所有 PR | `python -B scripts/check_docs.py`<br/>`git diff --check`<br/>（Python 3.12+ 标准库零依赖，`< 3s` 完成全仓校验） |
| **Level 1：日常快速门禁（Quick Gate）** | **`M2` 已启用并强制执行**（`scripts/quick_gate.py` + GitHub Actions `ci.yml`） | 阶段内工作包增量实现、审查局部修复、返工微调及提交前自检（低优先级静默运行，耗时 `< 1 分钟`） | `python -B scripts/quick_gate.py` 依次静默串联：`check_docs.py` → `dotnet build Spawn.sln -c Release` → `dotnet test Spawn.sln -c Release --no-build`（含 `Spawn.Architecture.GateTests` PE 导入表审计与 `Spawn.Core.UnitTests` 全量单测；测试步骤将 `HTTP(S)_PROXY` 指向本地死端口作为离线网络绊线，**测试一律禁止访问真实网络**，外部 HTTP 一律用进程内假 `HttpMessageHandler`）→ `git diff --check`；子进程输出按 UTF-8 优先、本机代码页回退解码，兼容 GBK 控制台 |
| **Level 2：整阶段完整交付门禁（Stage Gate）** | **`M2` 起已启用并随 `M3`~`M6` 扩充** | 阶段全部工作包实现完毕移交集中审查（B→A / `@verifier`）时在同一源码指纹下完整执行 | Release 全量干净构建 + `Spawn.Architecture.GateTests` 真实二进制 `PEReader` IAT 全表扫描 + `Spawn.Core.UnitTests` 全流派离线模拟器/GOG Comet/原生存档/进程追踪/IPC 确定性测试（`M3`~`M6` 递增扩充 `FastCDC`、WAL 断电恢复、Junction 防误删、DAG 与蓝图 VM 测试）+ NuGet 7 天冷却与已知漏洞审计（`dotnet list package --vulnerable`） |

---

## 2. `scripts/check_docs.py` 机械门禁校验清单（已生效）

`scripts/check_docs.py` 仅使用 Python 3.12 标准库编写，无需安装任何第三方 pip 包（同时兼容本机 Windows PowerShell `python` 与 GitHub Actions Ubuntu/Windows runner），自动执行以下硬性检查：

1. **必需根入口与治理文件齐全性**：断言 `README.md`、`AGENTS.md`、`CLAUDE.md`（且内容严格等于 `@AGENTS.md`）、`CONTRIBUTING.md`、`DESIGN.md`、`REVIEW.md`、`SECURITY.md`、`USER-ACTIONS.md`、`LICENSE`、`docs/README.md`、`docs/project-baseline.md`、`docs/governance/documentation-policy.md`、`docs/governance/document-registry.json` 全部存在。
2. **文档注册表双向无遗漏对齐**：
   - 校验 `docs/governance/document-registry.json` 结构合法（`schemaVersion == 1`、`currentStage` 属于 `M1`~`M6`、无过期 `retireAfter` 滞留文档）；
   - 校验每条记录包含合法的 `path`、`kind`、`status`、`owner`、`scope`、`reviewed`（`YYYY-MM-DD`）、`basis`，且磁盘文件真实存在；
   - 反向扫描全仓所有自有 `.md` 文档（根级、`.github/`、`docs/`）与受管 `.json` 文档（`.github/*.json`、`docs/**/*.json`），任何未登记或重复登记的文件立即报错失败。
3. **全仓 Markdown 相对路径链接与锚点校验**：
   - 剥离围栏代码块后解析所有内联链接 `[...](...)` 与图片引用（禁止使用不易追踪的参考式链接 `[text][ref]`）；
   - 断言所有仓库内相对路径使用正斜杠 `/`、不越界逃逸仓库根目录、目标文件真实存在，且带 `#anchor` 的标题/显式 `<a id="...">` 锚点在目标 Markdown 中真实可定位。
4. **需求与里程碑追溯一致性**：校验 `docs/delivery/traceability.json` 与当前阶段用例清单 `docs/delivery/m2-stage-cases.json` 中登记的需求编号、设计正本路径、模块规格路径与阶段验收用例引用真实有效、无悬空路径。
5. **公开卫生、敏感文件与跨项目零泄露扫描**：
   - 拦截任何误入工作区的数据库、密钥或证书文件（`.db`、`.sqlite`、`.sppk`、`.pfx`、`.p12`、`.key`、`.pem`、`.sec`、非示例 `.env`）；
   - 扫描全仓受管文本文件，硬性拦截个人机器绝对路径（如 Windows 用户目录盘符路径、个人代码盘绝对路径、Unix 用户主目录路径）、私钥/API Token 明文以及外部兄弟仓库名称或专属环境变量泄露。

---

## 3. `Spawn.Architecture.GateTests` PE 导入表零污染门禁（`M2` 已启用并强制执行）

<a id="iat-gate"></a>

`tests/Spawn.Architecture.GateTests` 作为本地 `quick_gate.py` 与 GitHub Actions CI 的一票否决测试，强制执行两层静态断言：

1. **MSBuild 工程引用图断言**：解析 `src/**/*.csproj`，断言 `Spawn.Host`、`Spawn.OverlayHost`、`Spawn.Contracts`、`Spawn.Core` 与 `Spawn.Modules.*` 绝未直接或传递引用任何 `Spawn.*Worker` 工程及高敏 NuGet 包（`OpenCvSharp4`、`Sdcb.PaddleOCR`、`Reloaded.Memory.SigScan`、`Iced`、`Maa.Framework`、`Nefarius.ViGEm.Client`）。
2. **编译产物二进制 `PEReader` IAT 符号黑名单断言**：使用 `.NET` 标准库 `System.Reflection.PortableExecutable.PEReader` 直接扫描构建产出的 `Spawn.Host.dll`（及 `.exe`）、`Spawn.OverlayHost.dll`（及 `.exe`）及其本地复制依赖程序集的原生导入表与托管 P/Invoke 元数据，一旦检出以下任一高敏 Win32/NTAPI 符号立即判门禁失败：
   - 跨进程内存与执行：`ReadProcessMemory`、`WriteProcessMemory`、`VirtualAllocEx`、`VirtualProtectEx`、`CreateRemoteThread`、`CreateRemoteThreadEx`、`NtWriteVirtualMemory`、`NtReadVirtualMemory`、`QueueUserAPC`、`NtSuspendProcess`、`NtResumeProcess`；
   - 全局输入钩子与模拟注入：`SetWindowsHookExA`、`SetWindowsHookExW`、`SendInput`、`keybd_event`、`mouse_event`（此类输入动作仅允许存在于后续阶段按需启停的隔离 `BlueprintWorker` / `TrainerWorker` 中，严禁存在于常驻 `Host` 或 `OverlayHost`！）。

---

## 4. 测试与开发期命令前台无干扰（静音）硬约束

<a id="foreground-quiet"></a>

无论由人类开发者在本地运行，还是由 AI Agent 在后台执行构建、测试与门禁脚本，必须严格遵守**零前台打扰**纪律：

| 潜在干扰源 | 强制工程约束 |
| :--- | :--- |
| **命令行黑框闪烁** | `scripts/quick_gate.py` 与产品进程拉起子进程时必须显式设置 `CreateNoWindow = true` / `UseShellExecute = false`（Python `subprocess` 在 Windows 下附带 `CREATE_NO_WINDOW` 标志） |
| **抢夺窗口焦点或弹窗** | `tests/Spawn.Core.UnitTests` 全部在无头（Headless）纯内存与独立临时目录（`%TEMP%\spawn-tests-*`）执行，绝不读写 `%LOCALAPPDATA%\spawn` 或 `%LOCALAPPDATA%\spawn-dev`（安装包静默安装/卸载演练仅限使用独立身份 `spawn-dev-test` 并加 `--no-shortcuts --no-relaunch`），也绝不启动外部游戏 EXE 抢占用户桌面焦点；仅在阶段交付人工体验时运行一次 `python -B scripts/package.py --install-dev` 静默驻留托盘 |
| **系统错误框与防火墙弹窗** | 门禁入口设置无错误弹窗模式；Named Pipe IPC 测试走本地唯一随机测试管道名，GOG Comet 协议测试严格仅绑定 `127.0.0.1` 随机空闲回环端口，严禁绑定 `0.0.0.0` 触发 Windows 防火墙弹窗 |
| **音频外放与系统提示音** | 测试期间严禁向物理扬声器播放成就解锁音效或触发终端响铃符 `\x07`；弹窗音效引擎在测试态或静音配置下保持 0 发声 |
| **自动打开外部应用** | 开发与测试命令严禁调用 `explorer.exe`、`Start-Process <url>` 或 `gh ... --web` 自动打开浏览器或资源管理器 |
| **CPU 满载拖慢前台** | `scripts/quick_gate.py` 默认以 `BELOW_NORMAL_PRIORITY_CLASS` 低优先级运行，子进程测试使用无窗口短命进程验证 Job Object IOCP 回调并即时清理，严禁遗留后台残留进程 |
