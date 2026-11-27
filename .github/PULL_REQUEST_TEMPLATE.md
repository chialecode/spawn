Closes #

## 做了什么

<!-- 行为变化、关联需求 REQ-* 与验收 AC-*；没有行为变化的文档/维护工作直接说明。 -->

## 验证

<!-- 实际命令、结果与证据路径；合成夹具与真实游戏、真实修改器、外部服务分开写；没运行的写 not-run 和原因。 -->
<!-- 仅真实待办验证项使用 `- [ ]`；`not-run` 与说明性条目使用普通列表 `- `（不要用 `- [ ]`）；提 PR 后完成的异步验证项（如 CI、审核前本机 `install-dev`）完成后回填勾选为 `- [x]`。 -->

- [ ] `python -B scripts/quick_gate.py`

## 风险

<!-- 进程隔离与反作弊边界、IPC 契约、spawn.db / Vault 数据兼容、许可证与文档登记表的影响；是否碰了高风险清单（docs/dev-rules/development-workflow.md#high-risk），碰了就加 risk:high 标签。 -->

风险等级：低风险 / 高风险（选一，删掉另一项；高风险需加 `risk:high` 标签，完成审核前本机 dev 安装并等用户测试后合并）

每个提交都带 `Signed-off-by`（`git commit -s`）。
