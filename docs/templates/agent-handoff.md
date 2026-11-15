# Agent A / Agent B 阶段交接与授权移交模板

本模板配套 [开发协作流程](../dev-rules/development-workflow.md) 与 [沟通协议](../dev-rules/communication-protocol.md) 使用，内容直接落地于 `docs/delivery/stage-plan.md` 与 `docs/delivery/stage-report.md`，不在仓库堆积零散临时交接文件。

---

## 1. Agent A → Agent B（整阶段实施计划必含要素）

- **里程碑与分支基点**：目标阶段（`M2`~`M6`）、计划版本号、工作分支名（`feat/m<N>-<slug>`）、不可变阶段父提交 40 位完整 SHA。
- **契约与开源依据**：关联 `REQ-*` 需求编号、`D-*`/`ADR-*` 决策、`docs/modules/*` 规格章节及 `Directory.Packages.props` 锁定的开源包版本。
- **全量工作包清单（`M<N>-W01`…）**：所属 `Spawn.sln` 子工程、进程完整性级别（Medium / UIAccess / High IL）、正常与异常恢复路径、严禁越界的反作弊与文件安全红线。
- **门禁与验收判据**：明确区分已启用命令（如 `check_docs.py`）与本阶段需建立/运行的命令（`quick_gate.py`、`Spawn.Architecture.GateTests` `PEReader` IAT 断言、单元测试）。

---

## 2. Agent B → Agent A（连续实现与自检交付必含要素）

- **交付版本锚点**：所依据的阶段计划版本、核验无变的阶段父提交 40 位 SHA、经 `git commit --amend -s` 收束后的当前阶段提交完整 40 位 SHA（带有效 DCO 签名）。
- **工作包映射矩阵**：

| 工作包 / `REQ-*` | 落地子工程与关键文件 | 实际运行验证与退出码 | 状态与未测边界（`not-run`） |
| :--- | :--- | :--- | :--- |
| `M<N>-W01` | `src/Spawn.*/...` | 实际执行的测试/门禁命令 | `passed` 或明确 `not-run` 客观原因 |

- **安全与卫生核对**：确认 `Spawn.Host` / `Spawn.OverlayHost` 零敏感 API 导入、通道数据落于独立临时目录、文档与 `document-registry.json` / `traceability.json` 已同步。

---

## 3. Agent A / `@verifier` 集中审查结论与远端授权移交

- **三维独立结论**：① 实现与自动化自检状态；② 集中技术审查结论（`reviewed` / `changes-requested`）；③ 产品实机验收状态（引用 `USER-ACTIONS.md` 中 `H-*` / `ACT-*`）。
- **返工清单格式（仅在 `changes-requested` 时出具）**：按 `F-01`、`F-02` 编号列出严重度（`P0`~`P3`）、触发场景、实际表现 vs 契约预期、精确文件路径与复验标准。
- **收敛后登记授权**：当 `P0`/`P1` 归零达 `reviewed` 后，在 [USER-ACTIONS.md](../../USER-ACTIONS.md) 登记具体 `ACT-*`（列明分支、父提交、候选 SHA、PR 标题/正文草稿与请求授权的远端动作），不生成空转 Prompt。

---

## 4. 标准可复制跨会话交接 Prompt 范式

### A → B 连续实施交接 Prompt

```text
你是 SPAWN 项目的 Agent B。请先阅读 AGENTS.md、docs/delivery/status.md 与当前阶段唯一计划 docs/delivery/stage-plan.md，核对当前分支 <分支名> 与阶段父提交 <40位父SHA>，保留已有改动。请连续完成本阶段全部可执行工作包、Spawn.Architecture.GateTests IAT 门禁、单元测试修复与受影响正本/注册表同步，严守 Clean Host 零敏感导入、反作弊 15s 冷却与测试前台静音红线，不要按工作包停顿询问。完成后运行全套可执行的阶段门禁，将变更以 git commit --amend -s（保留 DCO 签名且不改写父提交）收束进当前阶段提交，更新 docs/delivery/stage-report.md，如实标注 not-run 边界，并输出最新 40 位完整 SHA 与可直接发给 Agent A 的集中审查 Prompt；未经明确授权严禁执行 git push 或创建 PR。
```

### B → A 集中技术审查交接 Prompt

```text
你是 SPAWN 项目的 Agent A。请阅读 AGENTS.md、REVIEW.md、docs/delivery/stage-plan.md 与 docs/delivery/stage-report.md，针对本地分支 <分支名> 当前候选提交 <40位完整SHA>（父提交 <40位父SHA>）直接核查实际 git diff、Spawn.sln 依赖隔离、PEReader IAT 零污染门禁及测试证据。局部确定性小问题请直接修复、复跑门禁并 git commit --amend -s 收束进同一阶段提交；若存在跨模块 P0/P1 缺口则按 F-01 编号一次性出具返工清单与返工 Prompt；若审查通过收敛，请分别给出实现、技术审查与产品验收三项结论，拟好 PR 草稿并在 USER-ACTIONS.md 登记具体 ACT 编号请求用户授权远端推送与创建 PR，严禁擅自执行未授权远端操作。
```
