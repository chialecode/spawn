# SPAWN 安全策略与反作弊隔离声明

SPAWN 是面向硬核极客玩家的纯本地全能游戏中枢，同时纳管包含内核级反作弊保护的在线网游（如腾讯 ACE、HoYoProtect、EasyAntiCheat、BattlEye、Vanguard）与纯离线单机游戏的存档、修改器及自动化脚本。保护玩家账号安全、存档资产完整与个人凭据隐私是系统最高优先级红线。工程实现细则见 [数据与安全规则](docs/dev-rules/data-and-security.md) 与 [IPC 与安全隔离契约](docs/design/ipc-and-security-contracts.md)。

## 1. 私密漏洞报告入口

- 发现跨权限命名管道越界、反作弊等级误判、存档快照数据损坏或凭据泄露等安全隐患时，请直接通过 **[GitHub 私密漏洞报告（Private Vulnerability Reporting）](https://github.com/chialecode/spawn/security/advisories/new)** 提交。
- 为防止未修复漏洞被恶意利用，仓库已关闭公开 Issues，请勿在公开渠道粘贴可触发提权或破坏用户数据的复现载荷。
- 提交报告时请附受影响版本/提交 SHA、操作系统版本、最小脱敏复现步骤与预期安全边界，切勿附带真实云盘密钥、Cookie、个人存档或账号信息。

## 2. Anti-Cheat Guard 零越界承诺

1. **Clean Host 零敏感导入铁律**：常驻桌面主进程 `Spawn.Host.exe`（Medium IL）与游戏内覆盖层进程 `Spawn.OverlayHost.exe`（Medium + UIAccess IL）在编译与集成阶段强制接受 `Spawn.Architecture.GateTests` 的 `PEReader` PE 导入表（IAT）静态审计，严禁出现 `WriteProcessMemory`、`ReadProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、`SetWindowsHookExW`、`NtSuspendProcess` 等敏感符号；常规进程路径查询严格限定为免提权的 `PROCESS_QUERY_LIMITED_INFORMATION (0x1000)` + `QueryFullProcessImageNameW`。
2. **主从物理隔离与按需即焚**：内存修改、硬件扫描码注入、屏幕捕获与外部代肝编排物理拆分为独立的 `Spawn.BlueprintWorker.exe`、`Spawn.TrainerWorker.exe` 与 `Spawn.WorkflowWorker.exe`，平时零后台驻留。
3. **四级风险联锁与 15 秒余温冷却**：`AntiCheatGuardService` 在启动 `Tier 2`（二游内核保护区）与 `Tier 3`（高压电竞核禁区）游戏前，自动扫描并熔断残留高危进程/驱动，物理销毁并锁定高敏 Worker，且通过 Explorer Shell COM 代理启动以切断父进程（PPID）血缘；游戏主进程退出后，持续确认 `SGuard64.exe`、`EasyAntiCheat_EOS.exe`、`BEService.exe` 等反作弊守护进程完全消亡并强制等待 **15 秒余温冷却期** 后，方才解除对单机修改器与蓝图 Worker 的互锁。SPAWN 承诺绝不针对任何受保护联机网游开发或提供内存读写、封包篡改或图形注入能力。

## 3. 本机一次性 UIAccess 自签证书与私钥即毁策略

- 为在不向游戏进程注入任何 DLL 的前提下实现 `uiAccess="true"`（`ZBID_UIACCESS = 2`）全屏成就弹窗压制，安装程序在用户本机原地生成唯一的 **4096-bit RSA 自签名代码签名证书**，导入本机受信任根证书颁发机构后仅对安装目录下的 `%ProgramFiles%\spawn\Overlay\Spawn.OverlayHost.exe` 执行本地签名。
- **私钥即用即毁**：签名动作完成后，安装程序立即在内存中销毁私钥句柄并彻底删除临时密钥容器，绝不在磁盘残留 `.pfx`、`.p12` 或 `.key` 私钥文件，也绝不在安装包或代码仓库内置任何跨机器共享的通用根证书私钥，彻底杜绝第三方恶意程序借信任链伪造签名的供应链风险。绿色便携模式不导入根证书，自动平滑降级为常规 `WS_EX_TOPMOST` 置顶模式。

## 4. 凭据、存档隐私与跨权限 IPC 边界

- **本地凭据加密**：对象存储（Cloudflare R2 / S3）、WebDAV 云盘（坚果云 / NAS）、元数据刮削 API 及战报推送 Token 统一以当前登录用户范围的 Windows DPAPI 密文存储于 `%LOCALAPPDATA%\spawn\secrets\<credential-id>.sec`（开发版为 `spawn-dev`），绑定通道身份附加熵，绝不明文落盘、绝不写入日志或命令行参数。
- **命名管道最小权限 ACL**：跨完整性级别 `gRPC over Named Pipes` 统一通过 `NamedPipeServerStreamAcl` 显式配置 SDDL，仅授权当前登录用户 SID 读写并显式拒绝 `NETWORK` SID，防止局域网访问或低权限旁路劫持。
- **零遥测与本地数据主权**：SPAWN 不收集、不上传任何玩家游戏库清单、游玩时长、存档内容或硬件指纹；所有外部网络请求均仅在用户显式启用刮削、云同步、修改器检索或战报推送时按配置发往目标端点。
