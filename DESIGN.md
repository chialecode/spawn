# SPAWN 设计入口

界面视觉、色板与控件约束正本为 [UI 与交互设计规范](docs/design-rules/DESIGN.md)，跨模块操作流与状态机正本为 [交互与工作流设计](docs/design/interaction-and-workflows.md)。本文件仅作根级导航入口，具体色板、字阶、三栏尺寸、节点连线规范与焦点状态机不在多处重复维护。

## 核心视觉与交互基调

1. **纯原生极简三栏桌面架构（清透浅色默认 + 极客深色双主题）**：`Spawn.Host.exe` 100% 采用 `.NET 10`（LTS）+ 原生 `WPF`（`WindowChrome` 沉浸式无边框通顶窗口），由 **最左窄图标切换栏（`52px`）+ 二级上下文侧栏（`264px` 自适应折叠）+ 右侧主面板** 组成；默认采用清爽克制的清透浅色主题（`#F7F7F7` 中性浅灰侧栏 + `#FFFFFF` 纯白主面板 + `#EAEAEA` `1px` 细分隔线 + `#1677FF` 强调蓝），并在设置中支持一键实时切换极客深色主题，彻底拒绝 `WebView2` 或前端套壳。
2. **克制整洁的单主按钮、双子页签与浮动 `⋯` 菜单原则**：仅展示当前已完整落地可用的导航入口，绝不堆砌空壳页面；选中游戏详情头部下方采用 **`成就 (已解锁/总数)` | `游戏数据`** 双标签切换（成就列表启用 `VirtualizingStackPanel` 回收虚拟化丝滑承载数百项带图标成就；`游戏数据` 页聚合 PCGamingWiki、Unity/Unreal 引擎推断与离线模拟器路径支持可逆清理与一键恢复）；每个功能区最多保留一个强调色主按钮，低频操作统一收进浮动 `⋯` 弹出菜单，同时支持 `--background` 系统托盘静默常驻与 `--quit-for-update` 无损升级退出。
3. **零抢焦点、零掉帧免注入游戏内 Overlay**：独立宿主 `Spawn.OverlayHost.exe` 通过 `WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 扩展样式与排队滑入动效渲染 Steam 经典、PS5 奖杯（达成 100% 全收集自动追加白金奖杯连发）与 Xbox 稀有紫钻弹窗，配合 `NAudio.Wasapi` 共享模式合成和弦音效，全屏激战绝不抢夺键鼠焦点或切出桌面。
4. **类 UE Blueprint 节点画布与多屏 PerMonitorV2 一致性**：面向后续可视化脚本阶段统一预留 `miroiu/nodify` 双引脚节点画布规范；全宿主统一声明 `PerMonitorV2` DPI 感知，跨异构缩放显示器拖拽与四角弹窗定位保持像素级锐利。
