# ADR-0002：纯原生 `.NET 10 (LTS)` + WPF（`WPF-UI` + `Nodify`）桌面与类 UE 蓝图技术栈

- **状态**：Accepted
- **日期**：2026-11-15
- **决策编号**：`D-01`、`D-06`
- **关联文档**：[系统顶层架构](../design/system-architecture.md) · [交互与核心工作流](../design/interaction-and-workflows.md) · [技术选型与环境清单](../engineering/technology-selection.md) · [前期调研归档 01](../research/01-ui-architecture-and-blueprint.md)

---

## 1. 背景与问题陈述

SPAWN 需要同时在桌面端承载三种截然不同但又深度耦合的图形交互场景：
1. **现代高颜值桌面中枢**：支持 Windows 11 Mica / Acrylic 半透明材质、流式海量封面海报墙虚拟化滚动与毫秒级托盘快捷浮窗（Flyout）。
2. **类 Unreal Engine Kismet 纯可视化节点蓝图编辑器**：支持 1000+ 节点同屏 60FPS 无卡顿平移缩放、白线执行流（`ExecPin`）与多色数据流（`DataPin<T>`）贝塞尔连线、节点内联枚举/坐标框选器，以及运行期实时节点激活发光脉冲与单步断点气泡监视。
3. **重度 Win32 系统编程**：多透明分层窗口、全局低延迟热键、共享内存位图零拷贝预览与深度 P/Invoke 硬件调度。

如何在单一技术栈内兼顾视觉品质、千节点画布性能、极低后台驻留内存与零空域割裂，是决定 SPAWN 工程成败的首要选型问题。

## 2. 备选方案对比

| 备选技术栈 | 核心优势 | 致命短板与否决/采纳原因 | 结论 |
| :--- | :--- | :--- | :--- |
| **Electron / Tauri v2 (`WebView2`) + React Flow** | Web 前端组件丰富，节点画布现成库多。 | 1. 常驻 Chromium/`msedgewebview2.exe` 渲染进程群，静默挂机内存高达 `250MB ~ 500MB+`，违背游戏伴侣轻量守则；<br>2. 实时传输 60FPS 识图裁剪帧与节点发光脉冲需跨 JS-IPC 序列化，延迟抖动高；<br>3. `WebView2` 无法原生渲染无边框真透明异形顶层悬浮窗。 | **否决** |
| **原生 WPF 主壳 + 局部嵌入 `WebView2` 画布** | 主界面轻量，仅蓝图页用 Web 生态。 | 存在经典的 **HWND Airspace（空域撕裂）顽疾**——WPF 原生弹出菜单、全局搜索浮层与通知气泡无法正常覆盖在 `WebView2` 子句柄上方，且首次点开蓝图页会有数秒白屏冷启动感。 | **否决** |
| **WinUI 3 (`Windows App SDK`)** | 微软新一代 Fluent 原生控件库。 | 1. 截至 2026 年仍缺乏任何达到生产级虚拟化水准的开源类 UE 节点蓝图画布控件（自研周期极长）；<br>2. 无边框异形透明穿透窗口、托盘原生 XAML 弹出菜单与多显示器 DPI 边界成熟度显著弱于 WPF。 | **否决** |
| **Avalonia UI 11 (`NodeEditorAvalonia`)** | Skia 自绘管线优秀，跨平台一致性强。 | SPAWN 为 100% 深度绑定 Windows 内核特性（NTFS Hardlink/Junction/WOF、`JobObject`、`ZBID_UIACCESS`、`DirectComposition`、`WASAPI`）的专属工具，无需跨 Linux/macOS；而在纯 Windows 桌面质感、控件生态厚度与 `Nodify` 画布精细度上原生 WPF 更具统治力。 | **备选不采纳** |
| **纯原生 `.NET 10 (LTS)` + WPF + `lepoco/wpfui` v4 + `miroiu/nodify` v7** | 1. 原生 Windows 11 Fluent 2 + Mica 材质；<br>2. `Nodify` 专为类 UE 蓝图打造，自带视口裁剪虚拟化，千节点稳超 60FPS；<br>3. `.NET 10` 长期支持（LTS）运行时增强动态 PGO 与硬件 SIMD，共享同一套 `CommunityToolkit.Mvvm` 强类型模型，零 Web 进程、零空域冲突，托盘静默内存 `<90MB`。 | 需要遵循规范的虚拟化容器配置（如 `VirtualizingWrapPanel`）与异步图片解码管线以避免 UI 线程阻塞。 | **正式采纳** |

## 3. 最终决策

1. **统一目标框架与语言**：全解决方案升级锁定 **`.NET 10 (LTS)`（`net10.0` / `net10.0-windows10.0.26100.0`，最低运行基线 `SupportedOSPlatformVersion=10.0.22621.0`，本机开发工作站已就绪 `SDK 10.0.404`）与 `C# 14`**，启用分层编译与动态 PGO（Profile-Guided Optimization）。
2. **主界面与 MVVM 架构**：
   - 采用 **`WPF-UI` (`lepoco/wpfui` v4.x, MIT)** 提供 `FluentWindow`、Mica 背景材质、`NavigationView` 导航骨架与全套暗黑极客主题样式。
   - 采用 **`CommunityToolkit.Mvvm` (v8.4+, MIT)** Source Generator 实现零反射编译期属性通知与异步命令绑定。
   - 海量封面海报墙采用 **`VirtualizingWrapPanel`** 配合 `DecodePixelWidth` 缩略图硬解码限制，确保万级游戏库滚动丝滑且显存可控。
   - 系统托盘采用 **`H.NotifyIcon.Wpf`** 实现纯原生 XAML 托盘菜单与快捷游戏/任务状态浮窗，彻底摒弃老旧 `System.Windows.Forms` 依赖。
3. **纯可视化类 UE 蓝图工作台**：
   - 采用 **`Nodify` (`miroiu/nodify` v7.x, MIT)** 构建蓝图画布，严格区分**白线执行流引脚（`ExecPin`）**与**彩色强类型数据引脚（`DataPin<T>`）**。
   - 画布仅负责高帧率视图编辑与调试态可视化渲染；实际节点拓扑求值与循环挂起由独立进程 `Spawn.BlueprintWorker` 中的异步 Kismet 状态机驱动，通过命名管道流式回传 `ActiveNodeId` 驱动节点外发光动画。

## 4. 影响与后果

- **正面收益**：单一原生 C#/.NET 技术栈贯通 UI、蓝图画布、存储引擎与系统底层调用，无前后端双栈割裂；冷启动 `<800ms`，静默后台无任何浏览器子进程负担。
- **工程纪律**：UI 层严格遵守线程亲和性规则，任何磁盘 I/O、`spawn.db` 查询、图像特征匹配均禁止在 WPF Dispatcher 主线程同步执行；图像预览一律通过 `WriteableBitmap` 对接共享内存零拷贝刷新。

## 5. 验证判据

- 构建产物目录不包含 `WebView2Loader.dll` 或任何 Chromium 衍生组件。
- 压测基准夹具加载 1000 个带双向引脚连线的 `Nodify` 节点时，平移与滚轮缩放帧率稳定 `>=60FPS`。
