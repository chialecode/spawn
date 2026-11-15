# SPAWN 设计入口

界面视觉与控件约束正本为 [UI 与极客交互设计规范](docs/design-rules/DESIGN.md)，跨模块操作流与状态机正本为 [交互与工作流设计](docs/design/interaction-and-workflows.md)。本文件仅作根级导航入口，具体色板、字阶、节点连线规范与焦点状态机不在多处重复维护。

## 核心视觉与交互基调

1. **纯原生 Windows 11 极客暗色美学**：100% 采用 `.NET 10`（LTS）+ `WPF` + `lepoco/wpfui`，通过 `DwmSetWindowAttribute` 启用硬件级云母（Mica）与亚克力（Acrylic）背景材质，彻底拒绝 WebView2 或前端套壳带来的内存膨胀与空域撕裂。
2. **类 UE Blueprint 高帧率节点画布**：统一采用 `miroiu/nodify` 承载自定义按键/视觉脚本画布与二次元代肝排班 DAG，严格区分白色控制流引脚（`ExecPin`）与强类型彩色数据流引脚（`DataPin<T>`），支持运行态连线脉冲与断点单步高亮。
3. **144Hz+ 零掉帧免注入游戏内 Overlay**：独立宿主 `Spawn.OverlayHost.exe` 对瞬态成就弹窗采用 `DirectComposition` + `SkiaSharp` Flip-Model 交换链与 `ZBID_UIACCESS` 置顶层，不抢夺游戏焦点、不破坏高刷显示器 MPO 硬件独立翻转；`Alt+Z` 交互面板显式接管焦点与 `ClipCursor` 鼠标边界。
4. **多屏异构 PerMonitorV2 一致性**：统一以 96 DPI 设备无关像素（DIPs）布局，跨 4K（200%）主屏与 1080p（100%）副屏拖动及弹窗定位保持像素级清晰与坐标零偏移。
