# ADR-0005：二次元外部生态顶层编排、零报毒外部修改器管家与 `ZBID_UIACCESS` 高刷图层

- **状态**：Accepted
- **日期**：2026-11-15
- **决策编号**：`D-04`、`D-05`、`D-07`
- **关联文档**：[系统顶层架构](../design/system-architecture.md) · [成就与游戏内图层规格](../modules/achievement-and-overlay.md) · [二次元编排与蓝图规格](../modules/workflow-and-blueprint.md) · [存档与修改器规格](../modules/progress-saves-and-trainer.md)

---

## 1. 背景与问题陈述

在 Workflow（二次元自动化）、Progress（单机修改器）与 Achievement（游戏内成就弹窗面板）三大业务域中，工程路线选择直接关系到长期维护可行性、杀毒软件误报率与游戏帧率稳定性：

1. **二次元游戏高频版本迭代陷阱**：明日方舟、原神、崩坏：星穹铁道、绝区零、鸣潮、重返未来：1999、FGO 每 3~6 周即发生活动 UI 改版。若 SPAWN 试图自研全部游戏的图像识别任务逻辑，团队将彻底沦为疲于奔命的脚本修补工；此外 FGO 社区事实标准 `MaaFgo` 强耦合闭源打包程序 `BBChannel`，缺乏现成 CLI 接口。
2. **修改器启动背景音乐静音的杀软报毒雷区**：独立外部修改器（如 FLiNG）普遍自带高分贝启动音乐或按键音效；若采用向加壳修改器进程远程注入 DLL 并挂钩音频 API 的做法，会立即触发 Windows Defender 及主流 EDR 的“进程注入木马（Process Injection）”高危查杀。
3. **全屏高刷游戏内 Overlay 的注入风险与掉帧**：传统基于 `MinHook` 挂钩游戏 `IDXGISwapChain::Present` 的内嵌 OSD 不仅直接踩中内核反作弊红线，还极易因引擎升级引发游戏闪退；而普通 WPF 透明窗口在无边框全屏 DX12/Vulkan 游戏前常被系统压在底层或破坏 Flip-Model 独立翻转导致掉帧。

## 2. 备选方案对比

| 业务域 | 否决路线 | 采纳路线 |
| :--- | :--- | :--- |
| **二次元日常收菜** | 自研每款二次元游戏的 CV/OCR 刷本脚本逻辑。 | **顶层元调度编排器**：原生内嵌 `Maa.Framework` C# NuGet 直驱 `interface.json` 生态；`MAA` 原子预写 `gui.json`；`MaaFgo` 采用 `_ctypes.pyd` 代理免改包桥接 `BBChannel` 双端口；PC 原生助手统一走标准 CLI 适配器 + 四维资源锁并发 DAG。 |
| **外部修改器静音** | 远程线程向修改器 EXE 注入 `MinHook` 拦截 `waveOutWrite` / `WASAPI`。 | **`A0 + A` 零注入纯外部双保险静音**：`A0` 启动前预写 `FLiNGTSettings.ini` 与 22B 合法静音 MIDI 从源头关闭启动音乐；`A` 通过 `NAudio.Wasapi` 监听全声卡端点 `OnSessionCreated` 按 PID 微秒级静音并独立调节提示音音量。 |
| **游戏内成就与面板** | 默认向所有游戏注入 DirectX / Vulkan Present Hook DLL。 | **独立进程 `Spawn.OverlayHost`（`DirectComposition` + `SkiaSharp` Flip-Model + 本地自签名 `ZBID_UIACCESS`）**，144Hz 零掉帧、零游戏注入覆盖 95%+ 现代全屏游戏。 |

## 3. 最终决策

### 3.1 二次元外部生态无侵入集成矩阵
1. **`MaaFramework` 原生宿主内嵌**：在 `Spawn.WorkflowWorker` 中集成 `Maa.Framework` NuGet，动态解析外部工具目录 `interface.json`（兼容 V2）生成原生配置 UI、绑定控制器并订阅实时回调；遇到大版本 ABI 差异自动回退 `MaaPiCli.exe -d`。
2. **明日方舟 `MAA` 防自重启脱管**：严禁向 `MAA.exe` 传递会导致提权自重启并脱离父进程追踪的 `--config` 参数；改为启动前备份并原子写入 `config/gui.json` 任务开关与完成后动作，任务结束后自动恢复备份。
3. **FGO `MaaFgo + BBChannel` 双端口桥接**：在 `BBChannel` 工作目录部署轻量 `_ctypes.pyd` 转发代理模块，启动时自动激活 `127.0.0.1:25001`（指令 RPC）与 `25002`（事件流）、将废弃的 `_RetiredMumuApi` 透明替换为 `MaaFramework` ADB 控制器、静默屏蔽阻塞式 `tkinter.messagebox`，并配属 30 秒助战连接错误视觉看门狗。
4. **四维资源锁并发 DAG 与无人值守闭环**：统一调度 `PrimaryForegroundLock`、`ChildSessionSlot`、`EmulatorInstanceLock` 与内存/显存水位门禁，串联 RTC 睡眠唤醒、HDR 临时关闭、OLED 黑屏幕布、`SkiaSharp` 战报长图推送与 60 秒可取消自动休眠。

### 3.2 零杀软误报修改器管家与二期 `.CT` 预留
1. **一期（M3）纯外部管家**：通过 `A0`（配置与静音 MIDI 预置）+ `A`（全端点 WASAPI 会话静音控制器）彻底消灭启动爆音且实现 **0 注入、0 报毒**；沙箱目录 `SPAWN_Vault\Sandbox\Trainers\` 引导单次添加 Defender 目录排除项，由 High IL `TrainerWorker` 提供 35ms 硬件扫描码小键盘代理。
2. **二期（M6）原生内存后端预留**：抽象 `ITrainerBackend` 接口，二期在不触碰主宿主的前提下于 `TrainerWorker` 内扩展 `NativeCheatTableBackend`（CE `.CT` 逆序指针链 + `Reloaded.Memory.SigScan` + `Iced` 安全跳板）。

### 3.3 `ZBID_UIACCESS` 零注入高刷图层架构
1. 独立进程 `Spawn.OverlayHost.exe` 创建 `WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE` 窗口，挂载 `DirectComposition` 视觉树与 `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` + `SkiaSharp` GPU 上下文渲染多主题弹窗与 `Alt+Z` 战术面板。
2. 安装阶段在用户本机瞬态生成 4096-bit 自签名根证书（签名 `%ProgramFiles%\spawn\Overlay\Spawn.OverlayHost.exe` 后立即销毁内存私钥，杜绝私钥外泄风险），合法取得 Windows 内核 `ZBID_UIACCESS` 窗口带置顶特权；未安装便携态自动回退常规 `HWND_TOPMOST`。

## 4. 影响与后果

- **正面收益**：以极小维护成本统御整个中文二次元自动化开源生态；彻底规避杀毒软件对修改器管家的木马误杀；在严守反作弊零注入红线的同时实现媲美主机原生的 144Hz 成就解锁仪式感。
- **工程边界**：`<5%` 远古 DirectX 9 硬件独占全屏老游戏因绕过 DWM 合成器无法显示窗口化 Overlay，统一通过声音提示 + 游戏退出摘要兜底（可选隔离 Helper 归入 `LOOP-02` 远期按需提供）。

## 5. 验证判据

- 外部 FLiNG 修改器冷启动在开启 Windows Defender 实时防护与篡改保护环境下 0 报毒、0 启动音乐泄漏。
- `MaaFgo` 双端口握手、助战异常看门狗恢复及 `OverlayHost` 144Hz Flip-Model 帧耗时指标均在对应里程碑自动化夹具中验证达标。
