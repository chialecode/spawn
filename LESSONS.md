# 教训

开发中踩过、值得后来者避开的坑。每条：**规则** → 原因 → 证据（PR 号或日期）→ 出现次数。出现 3 次的提升进[开发规则](docs/dev-rules/development-workflow.md#lessons)或 [AGENTS](AGENTS.md)，并从这里删除。对其他项目也适用的教训，去那些仓库开 issue 分享。

## 本机环境

- **从 Agent 命令里启动常驻程序要先脱离作业对象。** Agent 的命令沙箱结束时会结束作业里的全部进程，托盘常驻的开发版会被一起关掉；开发版安装脚本经桌面外壳启动并核对进程不在活动作业里。证据：2026-11-22。出现 1 次。
- **在 Windows 上用 Python 或脚本改文本文件要保留原换行符。** Python 的 `Path.write_text` 默认按平台换行，会把整个文件的 LF 换成 CRLF，文档检查与源码匹配型测试随之失败。读写用字节或 `newline=""`。证据：2026-11-24 另一个项目。出现 1 次。
- **中文 Windows 的默认代码页（cp936）会让文本模式子进程解码失败。** 调用 `git`、`gh`、`dotnet` 取输出时显式 `encoding="utf-8", errors="replace"`，打印前设 `PYTHONIOENCODING=utf-8`。证据：2026-11-24。出现 1 次。

## 测试与 CI

- **测试里不要写死会过期的日期。** 固定的过期时间、证书有效期或“未来”日期到期后测试会突然失败；用相对当前时间计算。证据：2026-11-24 另一个项目的 CI。出现 1 次。
- **性能与超时类测试在机器忙时会偶发失败。** 冷启动、全量并行测试与打包同时进行时，固定时限的断言会抖动；用显式就绪信号代替固定等待，性能断言单独运行或放宽到能区分回归的阈值。证据：2026-11-24 另一个项目；[#31](https://github.com/chialecode/spawn/issues/31)（反作弊冷却测试在 CI 上停在 `ResidualGuardWatch`）。出现 2 次。
- **在临时目录跑 `git` 的测试要先清掉继承的 `GIT_*` 环境变量。** 在 `git worktree` 或 Git 钩子（如 `pre-push`）里运行测试时，Git 会导出 `GIT_DIR`、`GIT_WORK_TREE`、`GIT_COMMON_DIR` 等变量，临时仓库里的 `git init`、`git config`、`git commit` 与 `git tag` 会落到外层仓库与共享 `.git/config` 上；测试进程与派生的 `git` 子进程要统一剔除 `GIT_DIR`、`GIT_WORK_TREE`、`GIT_INDEX_FILE`、`GIT_COMMON_DIR`、`GIT_OBJECT_DIRECTORY`、`GIT_ALTERNATE_OBJECT_DIRECTORIES`。证据：[#44](https://github.com/chialecode/spawn/pull/44)、[#45](https://github.com/chialecode/spawn/pull/45)、[#46](https://github.com/chialecode/spawn/pull/46)。出现 1 次。

## 打包与安装

- **安装器的压缩算法也要过许可证白名单。** NSIS 的安装器外壳与 zlib 模块是 zlib/libpng 许可，但 LZMA 模块是带链接例外的 CPL-1.0，不在白名单里；本仓的安装包统一用 zlib。证据：[#11](https://github.com/chialecode/spawn/issues/11)。出现 1 次。
- **NSIS 的 `/D=` 必须是命令行最后一个参数且不加引号。** 安装器把 `/D=` 之后的整段原样当作目录，路径带空格也一样；用参数列表拼命令会给它加引号，静默安装就装错地方。命令行拼成字符串，`/D=<目录>` 放在末尾。证据：[#11](https://github.com/chialecode/spawn/issues/11)。出现 1 次。
