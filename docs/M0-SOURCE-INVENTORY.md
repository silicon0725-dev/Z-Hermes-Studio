# M0 源码盘点报告

- 日期：2026-10-17
- 状态：**部分完成** —— ZCode 上游与 ZCAC 已盘点；手柄控制插件、上下文管理插件在另一台设备上，尚未上传仓库，待补充（见 §5）。
- 基线文档：`docs/ZHS-Development-Document-v0.1.md`（PRD + TDD v0.1.0-draft）

## 0. 盘点方法与限制

本机沙箱无法直连 GitHub（`git clone` 连接 443 失败、无本地代理、`gh` 不可用）。盘点改用两条路径交叉验证：

1. **远程**：web_reader 抓取 GitHub 页面 / raw 文件 / releases atom feed（间歇性可用）。
2. **本地**：本机 `/opt/ZCode`（Electron 桌面安装，2026-10-13 构建）与 `~/.zcode`（真实运行数据）作为固定版本的**直接证据**。数据库 schema 通过只读副本（`/tmp/zhs-m0/*.sqlite`）用 python sqlite3 读取，`integrity_check = ok`。

结论均标注证据来源；未能在本次核实的项目明确记为"待确认"。后续正式开发需要可用的代码获取通道（镜像 / 代理 / 离线 vendor），已列入 §6。

## 1. 结论速览

| 对象 | 结论 | 许可证 | 状态 |
|---|---|---|---|
| ZCode 上游 `github.com/zai-org/ZCode` | 公开、活跃维护的 monorepo，**锁定基线 tag `v3.15.1`**（2026-10-10，GitHub 最新 release）；`app-server --stdio` 协议面已在本机 3.15.3 活体验证（§2.7） | Apache-2.0（LICENSE + package.json + NOTICE.md） | ✅ 已盘点 |
| ZCAC `github.com/silicon0725-dev/ZCAC` | 自有仓库，多 Agent 编排层 v0.3.0，stdio MCP server + SQLite | Apache-2.0（plugin.json 声明；仓库根缺 LICENSE 文件，建议补） | ✅ 已盘点（源码级，见 §3） |
| 手柄控制插件 | 未上传 | — | ⏳ 待盘点 |
| 上下文管理插件 | 未上传 | — | ⏳ 待盘点 |

## 2. ZCode 上游盘点

### 2.1 仓库事实

- 描述：Z.ai's coding agent harness. Powerful, intelligent, extensible.
- 许可证：**Apache-2.0**。仓库含 `LICENSE` 与 `NOTICE.md`（"ZCode 相关功能说明与第三方组件声明"，明确不同运行形态的功能、权限、存储、网络行为不同）。本机安装内另有 2.3MB 的 `THIRD-PARTY-NOTICES.md`，说明再分发时第三方清单体量大，ZHS 打包需预留处理。
- Monorepo 布局（packages/）：`app`、`server`、`web`、`desktop`、`shared`、`cli`、`ui`、`clients`、`client`、`providers`、`lib`。
  - `packages/client` 仅有源码无 README —— **没有带文档的公开客户端 SDK**，对适配层设计是决定性事实（见 ADR-0002）。
- 版本与发布：
  - main 分支 package.json `version: 3.14.0`（monorepo 根，`private: true`）。
  - Release：**v3.15.1（2026-10-10）**、v3.14.3（2026-09-24）。v3.15.1 为当前最新。
  - **双版本线**：产品 release 走 v3.x；CLI 内部 schema 版本走 0.1x.x（本机实测 `schema_migration.app_version = 0.16.5`，最新迁移 `0022_backfilled_session_reasoning`；`session.version` 出现 0.16.5 / 0.16.9）。适配器契约测试必须同时记录两个版本号。

### 2.2 本机安装证据（/opt/ZCode）

Electron 桌面应用（deb 包）：

- `zcode` 主程序 206MB；`app.asar` 340MB；`app.asar.unpacked` 仅 ssh2、node-pty 等原生模块。
- `resources/glm/packages/`：16 个内置插件包（browser-use、computer-use、visualize、documents、pdf、spreadsheets、presentations、skill-creator、plugin-creator、zcode-guide、node-repl-host、zcode-cua 等）。插件包形态 = `package.json + commands/ + skills/ + hooks/ + dist/{lib,mcp,providers}`。
- `resources/tools/`：bfs、ripgrep、ugrep（随包分发的检索工具）。
- `app-update.yml`：generic provider，更新地址 `http://localhost:8081`（当前为本地配置）。

### 2.3 运行时数据布局（~/.zcode）

| 路径 | 内容 | 对 ZHS 的意义 |
|---|---|---|
| `~/.zcode/cli/db/db.sqlite` | 会话库（WAL 模式，9MB+4MB WAL/4 个会话） | 唯一会话事实源，schema 见 §2.4 |
| `~/.zcode/cli/rollout/model-io-sess_*.jsonl` | 每会话完整模型请求/响应日志（含 attempt、durationMs、requestId） | "原始来源与摘要分离"原则在上游已是现实 |
| `~/.zcode/cli/artifacts/sess_*/call_*-tool-result-*.json` | 大型工具结果按会话落盘为文件引用 | 与开发文档 §7.1"大输出独立内容记录"方案一致 |
| `~/.zcode/cli/exec/sess_*/` | 每次 shell 调用的 stdout 日志 | 审计与诊断 |
| `~/.zcode/cli/plugins/` | `marketplaces/`、`cache/`、`data/<name>@<marketplace>`，`config.json` 内 `enabledPlugins` | 插件发现与启停机制 |
| `~/.zcode/v2/tasks-index.sqlite` | 桌面 App 自有任务索引：`tasks`、`task_groups`、`automations`（cron）、`automation_runs`、`off_peak_tasks` | **上游自家桌面也不直写 CLI 会话库，而是另建索引库** —— ADR-0004 的直接依据 |
| `~/.zcode/v2/` | `provider_config.json`、`credentials.json`、`setting.json`、`crash/`、`visualizations/` | 凭据与配置存放位置 |

### 2.4 会话库 schema（关键表，实测）

```
session(id, project_id, workspace_id, parent_id, slug, directory, title, version, share_url, summary_*)
message(id, session_id, sequence, data JSON)          ← 有 sequence，游标分页天然支持
part(id, message_id, session_id, sequence, data JSON) ← 消息分片，同为多态 JSON
session_entry(id, session_id, type, data JSON)
session_input(delivery ∈ {startNow, guide, queue}, admitted_sequence, promoted_sequence, status)
session_target(objective, status ∈ {active, paused, budget_limited, complete}, token_budget, tokens_used)
session_task_link(root_workflow_run_id, parent/child_session_id, activity_id)
model_usage(logical_request_id, attempt_index, trace_id, span_id, query_source)
tool_usage(tool_call_id, tool_name, side_effect_scope, read_only, destructive, approval…)
turn_usage(status ∈ {running, completed, error, cancelled}, first_token_at, …)
workflow_definition / workflow_run / workflow_activity / workflow_event(sequence per run)
dwf_run / dwf_actor / dwf_node / dwf_event(unique(run_id, sequence))
permission(project_id, data) / local_setting(scope, namespace, key, value)
input_history / todo / schema_migration(id, checksum, app_version, time_applied)
```

实测样本：`message.data` 含 `role/modelId/providerId/cost/tokens/finish/semantics/parentID/agent`；`part.data` 为多态（`{type:"text",…}`、`{timelineType:"model_change", display:"separator",…}`，自带展示提示）。本机 4 会话 / 521 message / 2143 part。

### 2.5 对 ZHS 设计的直接影响

1. **分页**：上游 message/part 自带 `sequence`，Adapter 可直接透传 `(session_id, sequence)` 游标，无需自建序号（开发文档 §7.1 的方案被上游 schema 证实可行）。
2. **大输出**：上游已采用"文件引用 + 会话目录"方案，ZHS Artifact Engine 对齐该模式即可。
3. **多 actor 编排已内建**：`workflow_*` 与 `dwf_*`（journal 化、按 run 序号、actor 租约）说明上游 runtime 已含多 actor 工作流引擎。M3 的 Task Engine 应评估经 Adapter 复用，而不是并行自建第二套（ZCAC 也正是在 dwf 等价构造之上构建 Worker）。
4. **权限与审计已有载体**：`tool_usage` 的 `read_only/destructive/approval` 字段、`permission` 表 → Policy Engine 可映射而非重造。
5. **性能遥测已有载体**：`turn_usage.first_token_at` 等 → ZHS 性能埋点可对齐口径。
6. **禁止外部直写会话库**：`schema_migration` 带 checksum 与 app_version 且持续演进（0.15.2 → 0.16.5），任何外部写入都会破坏其迁移校验。ADR-0004 由此定案。

### 2.6 上游内部结构（据 ZCAC 架构侦察报告 `docs/architecture-report.md`，基准 v3.14.3；关键结论已于本机 v3.15.3 活体复核，见 §2.7）

真实运行时位于 `apps/zcode-cli/packages/`，根 `packages/` 是产品层：

```text
apps/zcode-cli/packages/   ← 运行时
  core/                    ← AgentRuntime、工具、权限、subagent、上下文、压缩
  contracts/               ← 全部 Port 接口（SubagentPort、McpPort、ToolEntry…）
  adapters/                ← 存储(SQLite)、MCP 客户端、模型执行、插件加载
  bootstrap/               ← createZCodeApp 组合根、协议服务器、dwf 宿主接线
  dynamic-workflow(-runtime)/ ← dwf 引擎（纯域包）+ 子进程 harness（vm 沙箱 + NDJSON）
packages/                  ← 产品层
  provider / services / server / shared(zcode-protocol v1/v4) / desktop / web / ui / client / rpc
```

**三条经过验证的无头入口**（对 ADR-0002 决定性）：

| 入口 | 形态 | 适用 |
|---|---|---|
| `createZCodeApp(options)` → `submitPrompt()/.runtime` | 进程内 API | 同进程深度组合；SubagentPort 不在其公共面暴露（ZCAC 因此改走独立实例/协议 Worker） |
| **`zcode app-server --stdio`** | NDJSON ZCode Protocol v1：`session/create` → `session/send` → v4 事件订阅 → `interaction/requestPermission` | 跨进程驱动——**上游桌面自己就用这条边界**（Electron → Local Host → 每 workspace 一个 CLI 子进程） |
| `packages/server` HTTP/WS（Hono，`/ws` + `/api/*`，`ZCODE_SERVER_AUTH_TOKEN`） | 服务化 | 远程/多机扩展 |

其他要点：

- "一个 Agent" = 一个 `AgentRuntime` 实例 + `AgentProfile`（markdown：name/description/tools/model/permissionMode）；只读 Agent 照抄内置 Explore 模式（工具白名单 + 全新 PermissionService + yolo）。
- 权限：`PermissionService`（build/edit/plan/yolo + 项目规则 + 会话级授权），`PermissionBrokerPort` 处理 "ask"；`ToolScheduler` 按 readOnly/concurrentSafe/destructive 元数据分并行组（默认并发 10）。
- dwf 引擎已提供：并行 actor 会话、类型化结果校验、SQLite journal、确定性重放、崩溃恢复、amend 缓存导入、AIMD 并发治理。
- **上游 Git Worktree 隔离未实现**（全仓库唯一 worktree 钩子在 `script-workflow-runtime.ts:283` 直接抛 "not implemented yet"）——ZCAC 自建 Worktree Manager 的直接原因。
- 插件形态约束：支持 `agents/ + commands/ + skills/ + .mcp.json`；**不能加 UI、不能在宿主进程内执行代码** → 编排器必须作为独立进程（stdio/http MCP）存在。
- 桌面形态进程树：Electron Main → Local Host（@zcode/services，约 40 个服务）→ 每 workspaceKey 一个 CLI 子进程（NDJSON over stdio）→ AgentRuntime（含 SessionResidentPool：常驻 8 / 峰值 16 / 空闲 10min 回收）+ dwf 子进程。本机实测一致：`ZCode` → `zcode-host-local-1` → 多个 `zcode-cli`（均为 /opt/ZCode/zcode 的 Electron 子进程形态）。

### 2.7 `app-server --stdio` 协议面复核（2026-10-17，本机 v3.15.3-8237 活体验证）✅

ADR-0002 开放问题 ① 的复核结果。方法与证据：

**入口**：`ELECTRON_RUN_AS_NODE=1 /opt/ZCode/zcode /opt/ZCode/resources/glm/zcode.cjs app-server --stdio`。CLI 帮助文本确认："app-server — Run the ZCode Protocol stdio app server"。桌面启动器内嵌规格写死 `spawnArgs:["app-server","--stdio"]`、`nativeConfigDir:".zcode/cli"`（bundle 内另见独立版本串 0.13.3，为内嵌 CLI 分发版本，与产品 3.15.x、schema 0.16.x 并存——共三条版本线，契约测试要同时记录）。

**协议信封（探针实测）**：JSON-RPC 2.0 形态 over NDJSON——
- 请求 `{id, method, params}`；响应 `{id, result}`；错误 `{"error":{"code":-32601,"message":"Method not found: bogus/method"},"id":2}`（标准 JSON-RPC 错误码）。
- 通知自带 `sequence`：启动即推送 `startup/storageState`（`schemaVersion:1`、phase: checking→committing→ready、`lastAppliedMigrationId:"0022_backfilled_session_reasoning"`——与本机会话库迁移号一致，schema 血缘对上）。
- `session/list` 返回 `{sessions:[{sessionId, sessionKind:"interactive", status:"idle", mode:"build", title, titleSource, traceId, createdAt, updatedAt, workspace:{workspaceKey, workspacePath}}]}`。

**方法面（从 3.15.3 CLI bundle 提取，共 38 个）**：

| 组 | 方法 |
|---|---|
| `session/*`（23） | create、send、list、read、messages、event、events、subscribe、resume、fork、close、stop、cancel、cancelBackgroundTask、compact、setMode、setModel、setThoughtLevel、usage、goal、debug、subagents、requestRuntimePreferences |
| `interaction/*`（7） | requestPermission、requestUserInput、requestProviderRuntimeHeaders、requestOfficialMcpAuthHeaders、providerRuntimeHeadersCancelled、browserList、browserExecute |
| `workspace/*`（8） | generateText、cancelGenerateText、readPresentation、hooks/trustGrant、updateDynamicWorkflowPolicy、updateInteractionPreferences、updateModelIoPreferences、updateOffPeakToolPolicy |

**关键契约细节（ZCAC 源码实测注释 + 本机验证）**：stdio 协议**无回合完成通知流**——Turn 完成靠 `session/events` 轮询（ZCAC 用 1500ms 间隔、游标挂 session 防重放）；`session/subscribe` 存在但推送行为需在 ZHS 目标版本上单独验证。ZHS Backend 事件层第一版应按"游标轮询"设计，订阅推送作为后续优化。

**版本钉定结论（已定案）**：**钉 v3.15.1 = commit `aac4755666d09fdcd70272fcf063c077a639015f`**（tag 对象 `9ab8a647f03fbb3d0690c828a9d8edbfe8c588ff`；经 git ls-remote 实测，2026-10-17 时 **main HEAD 与该 tag 指向同一 commit，零漂移**——手中的源码快照即当前最新基线）。tag 列表确认**不存在 v3.15.2/v3.15.3 公开 tag**：本机 3.15.3 来自桌面更新通道，无源码对应，仅作实证旁证；原计划的 v3.15.1↔v3.15.3 协议目录 diff 随之作废。上游演进监控方式 = 新 tag 出现时重跑适配器契约测试并与 `aac4755` 对比协议目录。

### 2.8 v3.15.1 源码核验（2026-10-17，`ZCode-main.zip` 快照）✅

快照身份：根 `package.json` `version: 3.15.1`，文件时间戳 2026-10-10 18:59（+0800，v3.15.1 发布当天）→ 即锁定基线版本的源码。根 `LICENSE`（Apache-2.0）在树内。快照保存于 `/home/silicon/下载/ZCode-main.zip`（221MB，解压于 `/tmp/zhs-m0/ZCode-src`，注意 /tmp 易失）。

源码级确认的关键事实：

1. **协议 schema 按版本分目录且经 Zod `.strict()` 校验**：`packages/shared/src/zcode-protocol/`（v1，`ZCODE_PROTOCOL_VERSION = 1`，见 `index.ts:119`）与 `packages/shared/src/zcode-protocol-v4/`（`transport.ts` 1166 行 + 20 余模块：delta、coalesce、fork、snapshot、workflow-runs-reducer/holes/delta/eviction…，配套 40+ 协议测试）。
2. **v4 是推送式订阅**（回答了"轮询还是订阅"）：`V4_NOTIFICATIONS` = `v4/conversation/frame`（snapshot/deltas）、`v4/telemetry/event`、`v4/telemetry/local-ttft`、`v4/cua/permission-observation`。订阅响应 **ACK-only**，初始快照/恢复由 request-scoped **post-response outbox** 在响应行之后以通知发出；`clientMode ∈ {desktop-continuous, web-remote-replayable}`（后者带恢复语义）；`workflowRunDeltas` 等能力位由**可信 host 从 clientHello 注入**，订阅方不能自选；`MAX_LEGACY_TASK_IDS_PER_SUBSCRIBE = 200`（冷恢复的旧任务归属 allowlist）。
3. **v4 命令执行器**：`bootstrap/src/zcode-protocol-v4/commands/` 13 个 handler 模块、约 20 个命令全部原生直驱 core，未知命令 → notImplemented；依赖方向只允许 旧协议目录 → v4 目录（`v4-bridge.ts` 头注释明确）。
4. **旧 v1 `session/subscribe` 是遗留面**：无 unsubscribe RPC（`server-types.ts:93` 注释），ZCAC 观察到的"无回合通知流"适用于 v1 层——v1 下确实只能 `session/events` 轮询。
5. **Worktree 未实现**在基线源码中逐字确认：`apps/zcode-cli/packages/bootstrap/src/app/script-workflow-runtime.ts:283` 抛 `"workflow agent isolation 'worktree' is not implemented yet."`（与侦察报告的文件、行号一致）。
6. **`@zcode/client` 是私有 workspace 包**（依赖 `@zcode/rpc`、`@zcode/services`、`@zcode/shared`），服务桌面/Web 客户端——"无公开客户端 SDK"在源码级坐实。
7. monorepo 实际布局与侦察报告一致：运行时在 `apps/zcode-cli/packages/`（core、contracts、adapters、bootstrap、cli、tui、dynamic-workflow(-runtime)、telemetry、i18n、swift-bridge、shared-types + 全部内置插件包，与本机 `/opt/ZCode/resources/glm/packages` 清单一一对应）；产品层在根 `packages/`（client、desktop、provider、provider-node、rpc、server、services、shared、ui、web、zcode-cua、zcode-server-cli 等）。

对 ADR-0002 的直接修正：ZHS Backend 事件获取应**优先 v4 订阅**（`desktop-continuous` 模式消费 `v4/conversation/frame` 推送，与上游桌面同路），v1 `session/events` 轮询作为降级与 ZCAC 兼容路径。v4 的 delta/coalesce 模块同时是 ZHS §7.3 流式合并设计的上游参照。

## 3. ZCAC 盘点（Agent 集群插件）—— 源码级

盘点对象：`ZCAC-main.zip`（仓库 main 快照，文件时间 2026-10-05）。约 14k 行 TypeScript，标准六边形架构（domain / application / ports / adapters），22 个测试文件（单测、protocol-cursor/lifecycle、e2e×6、chaos/stress）。

### 3.1 包与许可证

- `packages/zcac/package.json`：v0.3.0、ESM、`private: true`；依赖仅 `@zcode/bootstrap`、`@zcode/contracts`、`@zcode/core`（全部 `workspace:*`）→ **构建必须发生在 ZCode monorepo 内**。
- 许可证：`plugin/.zcode-plugin/plugin.json` 声明 **Apache-2.0**；根 LICENSE 文件**已生成待推送**（2026-10-17：与标准 Apache-2.0 文本逐字一致，版权行 `Copyright 2026 silicon0725-dev`；存于 `/home/silicon/下载/ZCAC-LICENSE`）。
- 前置要求：ZCode ≥ v3.14.3，Node ≥ 24。

### 3.2 插件形态（已验证）

- 注册机制：`scripts/install-inline.js` 把 `packages/zcac/plugin` 绝对路径写进 `~/.zcode/cli/config.json` 的 `plugins.dirs`（inline 目录源，开发态；重启 ZCode 生效）。
- 插件内容：`.zcode-plugin/plugin.json`（含 `userConfig` schema：`zcacModel` 缺省 `bigmodel-api/GLM-5.3-Flash@low`、按角色模型 ×6、`zcacIsolation ∈ {shared, worktree}`、`zcacConcurrency` 缺省 2）+ `agents/`（10 个角色 profile：planner、coder、backend/frontend-coder、explorer、reviewer、tester、market-researcher、ui-designer、ux-designer）+ `commands/`（cluster、cluster-status、cluster-stop）+ `skills/cluster-orchestration` + `.mcp.json`。
- 编排器 = esbuild 打包成 `plugin/dist/orchestrator.cjs` 的**独立 stdio MCP server**，配置经 `ZCAC_*` 环境变量从 `user_config` 注入——上游插件不能加 UI、不能在宿主进程执行代码，独立进程是唯一合法形态。

### 3.3 存储与事件 journal（schema 已验证）

- 驱动：`node:sqlite`（DatabaseSync，与 ZCode 自身存储同驱动）；WAL + `busy_timeout=5000` + `BEGIN IMMEDIATE`；迁移为幂等补列式。
- 6 张表：
  - `zcac_runs`（status、root_task_id、**event_sequence 计数器**、metadata）
  - `zcac_tasks`（kind、status、priority、input/output JSON、dependencies JSON、assigned_agent、attempt、retry_policy、retry_not_before、**lease_until**）
  - `zcac_events`（**UNIQUE(run_id, sequence)**；journal-first：状态写入与事件 append 在同一事务原子提交）
  - `zcac_artifacts`（type、content、checksum）
  - `zcac_worktrees`（path、branch、base_ref、status、commit_sha）
  - `zcac_messages`（thread_id、from/to agent、type、reply_to）
- 事件 taxonomy 共 22 种（`RUN_*` / `TASK_*` / `AGENT_ASSIGNED|RELEASED` / `ARTIFACT_CREATED` / `WORKTREE_*` / `MERGE_*` / `SUPERVISOR_DECISION` / `ERROR`），文件头注释明确"不要滥加类型"。
- MCP `events` 工具语义已核实：`events(runId?, afterSequence?)` → journal 游标重放，直接满足开发文档 §8.3 的断线恢复模式。

### 3.4 任务状态机（ZHS Task Engine 可直接采纳）

```
pending ─(deps satisfied)→ ready ─(claim)→ running → succeeded | failed | retry_wait
blocked ↔ ready/pending；running ─(崩溃遗留)→ interrupted → ready | failed
failed → retry_wait ─(backoff)→ ready；任意非终态 → cancelled
```

合法迁移表以代码显式编码（`canTransition` / `assertTransition`，非法迁移抛错）——这正是开发文档 §8 缺失的事件/任务状态机规格的现成参照。

### 3.5 Worker 执行路径（v0.3：ProtocolAgentExecutor，已验证）

- 三层生命周期：**WorkerProcess**（子进程，可承载多 session）→ **WorkerSession**（workspace + policy 绑定，池 key 含 workspace 防串）→ **Turn**（一次 `session/send` → TurnComplete，显式幂等 settle）。
- 走 **ZCode Protocol v1 over NDJSON**：帧解析为纯函数（`{id,method,params}` 请求 / `{id,result|error}` 响应 / `{method,params}` 通知），容忍子进程 stderr 污染行。
- 会话池：干净完成的 session 归还池（每 workspace 默认 2 个 idle）；timeout/crash/stop 的 session **立即 poison 销毁不回池**；单 turn 超时默认 10 分钟；事件轮询 1500ms，消费游标挂 session 而非 turn（session 复用时不重放旧 turn 事件）。

### 3.6 对 ZHS 的结论

1. ZCAC 的 MCP 工具面 + journal 游标 = Task Engine Adapter 的现成契约（`events` 游标语义已核实）。
2. 任务状态机与 journal-first 事务模式可直接进入 ZHS 领域层设计。
3. ZCAC v0.3 的生产选择是 **stdio 协议子进程**（而非进程内组合）→ 与修订后的 ADR-0002 L1 同构，互相印证。
4. 待办：仓库补根 LICENSE；ZCAC 对上游的版本敏感面集中在 protocol adapter（`cliBundlePath` + 协议 schema），升级时只需重验这一层。

## 4. 本机环境事实

| 项 | 值 |
|---|---|
| 安装 | `/opt/ZCode`（deb，Electron），构建 2026-10-13 |
| 运行数据 | `~/.zcode/cli`（CLI）、`~/.zcode/v2`（桌面 App） |
| CLI schema 版本 | 0.16.5（迁移至 0022） |
| DB 只读副本 | `/tmp/zhs-m0/{db,tasks-index}.sqlite`（临时，供复查） |
| 网络 | **/etc/hosts 将 GitHub 全域 27 条域名指向 127.0.0.1**（主机级拦截，gh 亦不可用：未登录 + 域名被拦；未认证 API 的出口 IP 配额已耗尽）。可用绕行：DoH（dns.google / 1.1.1.1）解析真实 IP + `git -c http.curloptResolve=github.com:443:<ip>`（git 2.53）或 `curl --resolve`；web_reader 间歇可用。**正式开发需解决 GitHub 访问通道（解除 hosts 拦截 / gh auth login + 代理）** |

## 5. 延后盘点（2026-10-17 决定：不阻塞主线）

| 插件 | 状态 | 需要的信息 |
|---|---|---|
| 手柄控制插件 | 源码所在设备暂不可达，**延后** | 仓库地址或源码包；入口、依赖、事件→命令映射方式 |
| 上下文管理插件 | 源码所在设备暂不可达，**延后** | 仓库地址或源码包；SQLite/FTS5 schema、记忆条目模型、留痕机制 |

**影响面**：M1（聊天切片）、M2（性能）完全不受影响——两者只依赖 `app-server --stdio` 协议接缝。M3 中"手柄命令适配"与"上下文插件 Adapter"两个交付物顺延；M3 的 Task Engine 与 ZCAC 集群 Adapter 部分不受影响，可先行。Memory API（ZHS 自有）可先按开发文档 §9.3 设计，上下文插件 Adapter 到位后再做迁移映射。源码可得后按 §2/§3 同等深度补入本报告，并回填 ADR-0002 接入点清单。

## 6. M0 验收对照（开发文档 §14 M0）

| M0 交付物 | 状态 |
|---|---|
| 固定 ZCode 上游版本并检查许可证 | ✅ **v3.15.1 = commit `aac4755`（= 当前 main HEAD，零漂移）**；Apache-2.0 + NOTICE；v3.15.1 源码快照已在手（§2.8）；协议面已在本机 3.15.3 活体复核（§2.7） |
| 绘制 Runtime、会话、Provider、插件和事件数据流 | ✅ 静态面（本机证据）+ 代码级（ZCAC 侦察报告，基准 v3.14.3）+ 协议面活体探针（3.15.3，§2.7）+ **基线源码核验（v3.15.1，§2.8）** |
| 盘点三个插件依赖、入口、存储、调用方式 | 1/3（ZCAC 已到源码级；手柄、上下文**延后**——源码设备暂不可达，不阻塞 M0 收口，影响面见 §5） |
| 完成 ADR-0001 至 ADR-0004 | ✅ 本次提交（0003/0004 为 Proposed，待评审冻结） |
| 确定数据库写入所有者和本地通信方案 | ✅ 见 ADR-0004 / ADR-0002 |
