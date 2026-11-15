# SPAWN 前期深度技术调研档案索引（Research Archive）

> **档案状态**：`Reference`（前期源码级调研与第三方协议逆向归档）
> **规范效力声明**：本目录下的 `01` ~ `05` 五份文档系 SPAWN 在 M0/M1 阶段针对外部生态源码、Windows 内核/COM 未公开接口、离线模拟器字节结构及开源组件边界开展的**工程推导与证据档案**。
> **正式基线升级说明**：历史调研档案形成于早期预研阶段；正式工程基线已按 LTS 长期支持策略全面升级锁定为 **`.NET 10 (LTS)`（`net10.0-windows`）**，且 `M2` 首发编码里程碑已优先锁定“启动器与 Steamrip/GOG 游戏库管理 + 全渠道单机成就实时捕获与 `Spawn.OverlayHost` 弹窗管理闭环”。
> **单一事实正本原则**：当本目录下的早期推演细节与正式契约存在表述差异时，**一律以 [产品需求规格正本](../requirements/product-requirements.md)、[已确认架构决策](../decisions/confirmed-decisions.md)、[系统设计正本](../design/system-architecture.md) 及 [五大领域模块规格正本](../modules/README.md) 为唯一有效实现依据**。

---

## 1. 档案定位与使用指南

1. **为什么保留调研档案**：正式模块规格（`docs/modules/*.md`）聚焦于“是什么”与“怎么验收”（职责边界、接口签名、状态机、SQLite 表契约、反作弊熔断红线与测试判据）；而本目录完整保留了“为什么这样做”的一手逆向证据、备选方案淘汰推演（如为何否决 Electron/`WebView2`、为何否决修改器 `MinHook` DLL 注入、为何否决 `USVFS` 虚拟文件系统）以及外部工具源码级行号级行为分析。
2. **何时查阅本目录**：
   - 在 M2 ~ M6 编码阶段需要核对第三方二进制结构细节（如 `SmartSteamEmu` 24 字节结构体偏移、`BBChannel` 双端口 TCP 报文示例、`Locale_Remulator` vs `Locale-Emulator` CLI 参数差异）时；
   - 当上游开源组件（如 `MaaFramework`、`BepInEx`、`ludusavi-manifest`、`miroiu/nodify`）发布大版本更新，需要重新评估底层假设是否依然成立时。
3. **归档维护纪律**：本目录文档作为历史技术论证留存，不再随日常业务小需求频繁改写；任何架构决策变更必须通过新增 ADR（`docs/decisions/`）并同步更新 `docs/design/` 与 `docs/modules/` 正本生效。

---

## 2. 调研专卷与正式规格映射矩阵

| 调研卷号 | 档案文件 | 核心技术攻关与逆向取证范围 | 已沉淀的正式契约正本 |
| :--- | :--- | :--- | :--- |
| **卷 01** | [01-ui-architecture-and-blueprint.md](./01-ui-architecture-and-blueprint.md) | 原生 WPF + `WPF-UI` + `Nodify` 选型淘汰论证（正式基线已锁定 `.NET 10 LTS`）、类 UE 双引脚节点模型、异步 Kismet 单步发光虚拟机、`WGC + OpenCvSharp4 + Sdcb.PaddleOCR` 视觉管线、L1~L4 输入驱动矩阵、UIPI 完整性级别破局与四级 `AntiCheatGuard` 进程物理隔离模型 | [ADR-0002](../decisions/0002-pure-native-wpf-and-nodify-ui-stack.md) · [ADR-0003](../decisions/0003-anti-cheat-guard-and-process-isolation.md) · [system-architecture.md](../design/system-architecture.md) · [ipc-and-security-contracts.md](../design/ipc-and-security-contracts.md) · [workflow-and-blueprint.md](../modules/workflow-and-blueprint.md) |
| **卷 02** | [02-launcher-process-and-mod-nexus.md](./02-launcher-process-and-mod-nexus.md) | 无强杀副作用 `JobObject` IOCP + `0x1000` 破壳追踪、三态真实游玩计时、HDR/分辨率/CPU Sets/`LRProc`+`LEProc` 自愈环境管线、三级漏斗离线元数据刮削、CAS 硬链接 + `XxHash3` 可变配置回收、四大引擎 Mod 编排、`<100ms` 顶层 NTFS Junction Profile 切换与六大极客扩展工具箱 | [ADR-0004](../decisions/0004-cas-fastcdc-storage-and-mod-junction-topology.md) · [spawn-launcher.md](../modules/spawn-launcher.md) · [nexus-mods-and-utilities.md](../modules/nexus-mods-and-utilities.md) |
| **卷 03** | [03-save-timemachine-and-trainer.md](./03-save-timemachine-and-trainer.md) | `ludusavi-manifest` 16 种跨平台占位符编译与 `[save]`/`[config]` 拆流、确定性 Gear-Hash `FastCDC + BLAKE3 + Zstd` 去重与 `AlphaVSS` 热备份、`32MB SPPK` Packfile 多后端云同步与 64 代血缘合并、FLiNG 双源确定性匹配与零注入双保险静音（`FLiNGTSettings.ini` + 22B MIDI + `NAudio.Wasapi`）、二期 CE `.CT` 逆序指针链与 `Iced` ±1.75GB 跳板契约 | [ADR-0004](../decisions/0004-cas-fastcdc-storage-and-mod-junction-topology.md) · [ADR-0005](../decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) · [progress-saves-and-trainer.md](../modules/progress-saves-and-trainer.md) |
| **卷 04** | [04-achievement-and-ingame-overlay.md](./04-achievement-and-ingame-overlay.md) | 15+ 流派离线 Steam 模拟器精确路径与字节级解析对照、GOG 本地 `127.0.0.1:9977` Comet Protobuf 免改 DLL 监听与正版 Binary VDF 解析、四层防漏 FSW 状态机、无成就桩发行版一键 `gbe_fork` 无损赋能、免 API Key 中文隐藏成就瀑布流、`Spawn.OverlayHost` 本机瞬态自签 `ZBID_UIACCESS + DirectComposition` 144Hz 弹窗与 `Alt+Z` `ClipCursor` 锁鼠交互面板 | [ADR-0005](../decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) · [achievement-and-overlay.md](../modules/achievement-and-overlay.md) |
| **卷 05** | [05-gacha-workflow-orchestration.md](./05-gacha-workflow-orchestration.md) | 二次元顶层元编排边界、`MAA` 预写 `gui.json` 防自重启脱管、宿主内嵌 `Maa.Framework` C# 直驱 `interface.json` V2 衍生生态、`xlxyvergil/MaaFgo` 配合 `_ctypes.pyd` 代理注入闭源 `BBChannel` 双端口（`25001`/`25002`）JSON RPC 与 30s 视觉看门狗、`BetterGI`/`m7a`/`SRC`/`OneDragon`/`ok-ww` 标准化 CLI 矩阵、四维资源锁并发 DAG、RTC 唤醒 + OLED 黑屏幕布 + `SkiaSharp` 战报长图推送闭环 | [ADR-0005](../decisions/0005-external-ecosystem-orchestration-and-uiaccess-overlay.md) · [interaction-and-workflows.md](../design/interaction-and-workflows.md) · [workflow-and-blueprint.md](../modules/workflow-and-blueprint.md) |

---

## 3. 原临时总纲退役说明

早期预研汇总文件 `docs/SPAWN-Research-Master-Report.md` 包含的多进程拓扑、SQLite 全局 ER 模型、核心开源选型矩阵与 15 条底层工程避坑红线，已在 M1 阶段 100% 拆解沉淀至：

- 系统架构与安全模型：[system-architecture.md](../design/system-architecture.md)、[data-model.md](../design/data-model.md)、[ipc-and-security-contracts.md](../design/ipc-and-security-contracts.md)
- 技术选型与交付路线：[technology-selection.md](../engineering/technology-selection.md)、[delivery-plan.md](../engineering/delivery-plan.md)
- 五大业务域规格正本：[docs/modules/README.md](../modules/README.md)

为消除“双总纲”维护漂移风险，原临时汇总文件已按治理规范正式退役移除。
