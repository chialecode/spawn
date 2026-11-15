# SPAWN UI 与极客交互设计规范

本规范约束 `Spawn.Host.exe`（桌面主界面与 `Nodify` 双画布编辑器）及 `Spawn.OverlayHost.exe`（高刷游戏内瞬态弹窗与 `Alt+Z` 快捷交互面板）的全部视觉体系、动效帧率、输入焦点与多屏 DPI 行为。跨模块业务操作流详见 [交互与工作流设计](../design/interaction-and-workflows.md)。

---

## 1. Windows 11 原生 Fluent + DWM Mica 极客暗色美学

1. **视觉基调与材质层次**：
   - 默认且核心打磨 **深色极客主题（Dark Geek Mode）**（同时支持跟随系统浅色高对比度回退，但所有发光引脚、奖杯流光与安全状态色以深色材质为基准调校）。
   - 主窗口通过 `lepoco/wpfui` 调用 `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE = 38)` 启用原生 **Mica Alt（Tabbed）或 Mica** 硬件背景材质；侧边导航与浮动抽屉使用半透明深灰卡片层叠（主内容底色 `#14161A`、一级卡片 `#1C1F26`、二级悬浮层 `#252932`、1px 发丝描边 `#2E3440`），严禁使用高消耗软件高斯模糊滤镜拖慢窗口拖拽帧率。
2. **语义与 Anti-Cheat Guard 警戒色板**：
   - 品牌主强调色：电光青蓝 `#38BDF8`（悬停 `#7DD3FC`，按压 `#0284C7`）。
   - **四级反作弊安全徽章专色（全局固定语义，不得挪作普通装饰色）**：
     - `Tier 0` 纯离线单机区：翡翠绿 `#10B981`（全功能解锁）；
     - `Tier 1` 弱保护合作区：琥珀青 `#06B6D4`（联机礼仪温和提示）；
     - `Tier 2` 二游内核保护区：警戒橙 `#F59E0B`（Worker 物理熔断 + Explorer 脱钩启动）；
     - `Tier 3` 高压电竞核禁区 / `15s 冷却锁定中`：熔断绯红 `#EF4444`（伴随倒计时秒数徽标，相关修改器与蓝图入口呈置灰锁定态并给出明确悬浮原因）。
3. **字体与高密度数字排版**：
   - 中文与常规界面文案优先使用系统原生 `Segoe UI Variable Display / Text` + `Microsoft YaHei UI`；
   - 游玩时长（`HH:MM:SS`）、帧率延迟、内存地址偏移、BLAKE3/SHA-256 指纹、端口号与版本号强制使用等宽字体（`Cascadia Mono` / `Consolas`）并开启 `FontNumeralAlignment="Tabular"`，确保动态刷新时数字宽度零跳动。
4. **外壳导航结构**：
   - 左侧紧凑型可折叠 Fluent 导航栏（游戏库 `Spawn` · 存档与修改器 `Progress` · 成就馆 `Achievement` · 自动化与代肝 `Workflow` · Mod 与工具箱 `Nexus` · 底部固定 `Anti-Cheat Guard 监控台` 与 `设置`）+ 顶部全局安全状态条（实时显示当前运行游戏、活跃 Profile、反作弊等级与 15s 冷却倒计时、后台代肝队列状态）。

---

## 2. `miroiu/nodify` 类 UE Blueprint 双画布视觉规范

统一使用 `miroiu/nodify` 承载 **单机可视化按键/视觉脚本画布** 与 **二次元并发代肝排班 DAG 画布**，视觉语言严格对齐 Unreal Engine 5 Blueprint：

1. **无限网格与硬件视口缩放**：
   - 深色点阵/双级网格背景（主网格 `100px` `#262B36`，次网格 `20px` `#1B1F27`），视口缩放范围 `0.15x ~ 2.50x`，仅通过 `MatrixTransform` 驱动 GPU 变换，保证 500~2000 节点拖拽平移稳跑 `60+ FPS`。
2. **双引脚（ExecPin vs DataPin）形态与标准色板**：
   - **执行流引脚（`ExecPin`）**：五边形箭头图标 + **纯白高亮 `#F8FAFC`** 连线（线宽 `2.5px`，运行态激活时叠加青白流光脉冲动画），严格约束“输入多对一汇聚、输出一对一跳转”。
   - **强类型数据流引脚（`DataPin<T>`）**：圆形图标（已连线实心、未连线空心并内联字面量编辑器或十字准星拾取按钮），按数据类型严格着色：
     - `Boolean`（条件判断）：猩红 `#DC2626`
     - `Int32 / Double`（次数/耗时/置信度阈值）：翠绿 `#22C55E`
     - `String / OCR Text`（文本/正则）：洋红 `#EC4899`
     - `Point / Rect ROI`（屏幕归一化坐标/识别框）：金黄 `#EAB308`
     - `ImageMat / Template`（OpenCV 图像模板）：湖蓝 `#0EA5E9`
     - `WindowHandle / EmulatorRef`（目标窗口/模拟器句柄）：紫罗兰 `#8B5CF6`
3. **节点头部类别色带与实时调试态**：
   - 触发器节点（红棕色头 `#991B1B`）、流程控制节点（深灰蓝头 `#334155`）、视觉/OCR 感知节点（青蓝头 `#0369A1`）、输入执行动作节点（靛紫头 `#4338CA`）、纯函数计算节点（无执行引脚，暗绿头 `#166534`）。
   - **断点与单步调试视觉**：挂载断点的节点左上角显示红色八角停止徽标；虚拟机挂起在某节点时，节点外框呈现黄色呼吸光晕（`#FACC15`），并在右侧检查器实时展示当前协程帧各 `DataPin` 的求值快照与 ROI 匹配缩略图。

---

## 3. 144Hz+ 零掉帧 Overlay 多主题动效与防抢焦点交互规范

`Spawn.OverlayHost.exe` 直接叠加在全屏游戏之上，交互失误会直接导致玩家 FPS 误开枪、魂类游戏暴毙或掉帧卡顿，必须遵守以下硬性规则：

1. **双层渲染分离架构**：
   - **瞬态成就/状态弹窗层**：采用无重定向表面（`WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`）+ `DirectComposition` + `SkiaSharp` Flip-Model 交换链渲染，安装模式下运行于 `ZBID_UIACCESS = 2` 置顶频段。严禁夺取前台焦点，全透明区域 100% 鼠标物理穿透，保全高刷显示器 MPO 硬件独立翻转 **0.0 FPS 掉帧**。
   - **内置五大极客成就主题**：① `Steam Deck Modern`（深灰毛玻璃侧滑）；② `PS5 Trophy`（区分铜/银/金杯音效与光效，全成就达成时自动连发「白金奖杯」全息流光特效）；③ `Xbox Rare Diamond`（全球解锁率 `< 10%` 自动触发紫钻粒子音效）；④ `原神·天地万象`（羊皮卷轴展开动效）；⑤ `崩坏：星穹铁道`（星穹列车车票打卡动效）。初始建档存量已解锁成就默认静默入库（`NotifiedPopup = 1`），严禁首次导入时弹窗刷屏。
   - **`Tier 3` 高压电竞自动避让**：检测到 `Tier 3` 游戏运行时，自动抑制自绘 Topmost 覆盖层，降级为系统原生通知或赛后结算汇总展示。
2. **`Alt + Z` 快捷交互面板与锁鼠状态机**：
   - 热键监听强制采用内核级 `RegisterHotKey`（严禁挂载全局 `WH_KEYBOARD_LL` 低级钩子）。
   - 呼出 `Alt + Z`（或手柄组合键）时：先铺设全屏半透明暗化拦截层（`#880B0E14`），显式调用 `SetForegroundWindow` 接管焦点并调用 `ClipCursor` 将系统光标约束在当前游戏显示器内，阻断底层游戏 `RawInput` 继续接收鼠标位移导致镜头乱转或误开火；按 `Esc` 或再次按 `Alt + Z` 立即销毁面板、释放 `ClipCursor(null)` 并将焦点精确归还给游戏 HWND。

---

## 4. 多屏异构 PerMonitorV2 DPI、无障碍与诚实状态反馈

1. **多屏异构 DPI 规范**：
   - 所有宿主清单（`app.manifest`）声明 `PerMonitorV2` DPI 感知。桌面窗口与 WPF 面板以 96 DPI 设备无关像素（DIPs）布局；涉及 `DirectComposition` 物理交换链尺寸、WGC 屏幕截图框选 ROI 与 `SendInput` 物理像素坐标换算时，必须显式通过 `GetDpiForMonitor` / `VisualTreeHelper.GetDpi` 按目标显示器实际缩放比（100% / 125% / 150% / 200%）精确转换，并在蓝图模板匹配中统一归一化至基准分辨率。
2. **输入法（IME）与快捷键防误触**：
   - 搜索框、Profile 命名、存档快照备注（如“进女武神雾门前”）与蓝图注释框在中文输入法候选组合态（IME Composition）下按 `Enter` 或字母键时，严禁误触发启动游戏、删除节点或全局单键快捷键。
3. **诚实状态与可取消反馈**：
   - 空游戏库、离线刮削降级、云同步分叉冲突、`AlphaVSS` 提权回退、修改器沙箱拦截、`15s 反作弊冷却中` 及 `60s 夜间代肝休眠倒计时` 均须展示清晰中文原因、倒计时进度条与一键取消/回滚按钮；尚未解锁或受反作弊等级锁死的功能入口必须明确标识原因，严禁静默无响应。
