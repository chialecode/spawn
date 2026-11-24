Closes #

## 做了什么

<!-- 行为变化、关联需求 REQ-* 与验收 AC-*；没有行为变化的文档/维护工作直接说明。 -->

## 验证

<!-- 实际命令、结果与证据路径；合成夹具与真实游戏、真实修改器、外部服务分开写；没运行的写 not-run 和原因。 -->

- [ ] `python -B scripts/quick_gate.py`

## 风险

<!-- 进程隔离与反作弊边界、IPC 契约、spawn.db / Vault 数据兼容、许可证与文档登记表的影响；是否碰了高风险清单（docs/dev-rules/development-workflow.md#high-risk），碰了就加 risk:high 标签。 -->

- [ ] 低风险
- [ ] 高风险（已加 `risk:high`，等用户合并）

每个提交都带 `Signed-off-by`（`git commit -s`）。
