# ZCode app-server --stdio 线协议实测笔记

- 对象：`zcode app-server --stdio`（JSON-RPC 2.0 形态 over NDJSON）
- 证据基线：本机 v3.15.3-8237 活体探针（2026-10-17）+ 上游 v3.15.1 源码（`packages/shared/src/zcode-protocol*`）
- 状态：随 M1 实现持续更新；C# 侧对应实现见 `src/ZHS.Protocol` / `src/ZHS.Client`

## 连接与握手

- 启动：`ELECTRON_RUN_AS_NODE=1 /opt/ZCode/zcode /opt/ZCode/resources/glm/zcode.cjs app-server --stdio`。
- 启动即推送 `startup/storageState` 通知（`schemaVersion:1`、`sequence` 递增、phase: checking → committing → ready、`lastAppliedMigrationId`），ready 后才可发请求。
- `session/create` 响应携带 `protocol: {name: "ZCode Protocol", version: 1}` —— 可用于握手时校验协议版本。

## 服务端反向请求（必须处理，字符串 id 如 "server-1"）

| 方法 | 应答 | 说明 |
|---|---|---|
| `session/requestRuntimePreferences` | `{nativeSearchEnhancementsEnabled:true, memoryEnabled:false, askUserQuestionAutoResolutionEnabled:true}` | 安全默认；回 -32601 agent 有兼容回退 |
| `interaction/requestOfficialMcpAuthHeaders` | `{headers:{}}` | **必须应答**：ZCAC 实测回 -32601 会导致 agent 进程退出 |
| 其他（如 `interaction/requestPermission`） | -32601 | 未来接入 Policy Engine / 审批 UI |

响应帧 id 必须原样回显（字符串保持字符串）。ZCAC 踩坑：`Number("server-1")=NaN` 导致应答错配、session/create 超时。

## session/create

请求：`{workspace:{workspacePath, workspaceKey}, mode}`（workspaceKey 必填字符串；`toolAllowlist` 可省略但**不可为 null**——Zod 报 `expected array, received null`；序列化必须忽略 null 字段）。

响应（v3.15.3 实测，比 ZCAC 记录的 v3.14.3 形态丰富）：

```json
{"messages":[], "projection":{...状态投影...}, "protocol":{"name":"ZCode Protocol","version":1},
 "runtime":{eventSeq, stateRevision, ...}, "session":{sessionId, mode, model:{providerId, modelId},
 status, title, workspace:{...}, ...}, "settings":{mode:{current}, model:{available:[...]}}}
```

sessionId 在 `result.session.sessionId`（兼容平铺/纯字符串历史形态）。`settings.model.available` 可用于 UI 模型选择。

## session/send

请求：`{sessionId, content}`（消息字段是 `content`）。
响应：`{accepted:true, sessionId, stateRevision}` —— 只表示回合受理，不表示完成。

## session/events（v1 层无回合通知流，靠轮询）

请求：`{sessionId, afterSeq, limit}`（游标字段是 **afterSeq**）。响应：`{events:[...]}`。

事件信封（v3.15.1 源码 Zod discriminatedUnion，判别字段 **`type`**；ZCAC 在旧版本观察到 `kind`，兼容回退）：

```json
{"eventId":"...", "sessionId":"...", "turnId":"?", "seq":<int>, "traceId":"?", "timestamp":<ms>,
 "type":"turn.completed", "payload":{...}}
```

类型全集（26 种）：`session.created/resumed/updated/titleUpdated/closed`、`turn.started/steerQueued/steerDrained/completed/failed`、`message.upserted/removed`、`part.started/delta/upserted/removed`、`model.streaming`、`tool.updated`、`permission.requested/resolved`、`userInput.requested/resolved`、`checkpoint.created`、`rewind.triggered`、`streamRecovery.updated`。

- `turn.completed` payload：`{response:string, tokenCount, usage?, toolCallCount, duration, ...}`
- `turn.failed` payload：`{error/message, ...}`
- **流式文本增量是 `model.streaming` 事件**（v3.15.3 实测 + e2e 验证）：payload `{assistantMessageId, delta, done, kind:"text_delta"}`；工具调用的输入增量也是 model.streaming 但 `kind` 不同（如 input_json_delta），UI 过滤只取 text_delta。v3.15.1 源码另有 `part.delta`（payload `{messageId, partId, field, delta}`），ZHS 两种都兼容。
- 序号字段是 `seq`（不存在 `sequence`；ZCAC 实测：游标用错字段名会反复重放旧事件）

## v4 推送订阅（2026-10-17 活体实验 + e2e 冒烟验证 ✅）

源码定位："载体复用现有 JSON-RPC（stdio NDJSON / socket），方法名带 v4/ 前缀与旧协议并存；**旧 session/* 方法删除后，这里就是唯一协议面**"（`packages/shared/src/zcode-protocol-v4/transport.ts` V4_METHODS 注释）。

- 订阅：`v4/conversation/subscribe`，params `{topic: "conversation/<sessionId>", connectionId: <uuid>, clientMode: "desktop-continuous"}`（另有 base 水位续传、visibility QoS 提示可选）。
- **裸 stdio 协议上外部客户端可直接订阅，无 clientHello 门槛**——clientMode/workflowRunDeltas 直接来自 wire params；"可信 host 注入"只发生在上游桌面 UI→Local Host 那一跳（services 层）。
- ACK：`{ack: {subscriptionId, mode: "snapshot"|"resume", logEpoch, openTiming?}}`；初始快照帧随后以 `v4/conversation/frame` **通知推送**（post-response outbox 时序）。
- 帧信封：`{wireVersion: 3, kind: "complete", deliveryKind: "initial"|"online", logicalFrameId, logicalFrameOrdinal, topic, subscriptionId, ...}`；真实回合期间 online 帧按 logicalFrameOrdinal 递增推送，伴随 `v4/telemetry/event` 事实通知。**回合期间不需要轮询。**
- 同族方法：`v4/controller/subscribe|resync|unsubscribe`、`v4/conversation/resync|unsubscribe|rowsRange|plans|fileChanges|workflowRuns|workflowRunEvents|workflowRunArtifacts|...`；背压控制 `v4/connection/flow`（saturated/drained/closed）。
- clientHello（`{kind:"clientHello", protocolVersion: 3, clientId, clientKind, appVersion, capabilities?}`，strict）是连接级注册，desktop Host 服务层消费；直连 CLI 场景未强制。

## v4 rowsRange —— loadOlder 反向分页（M2 历史加载，e2e 验证 ✅）

- 请求：`v4/conversation/rowsRange`，params `{sessionId, beforeRowId?, limit≤200}`（clientMode 由 host 注入，直连省略）；**缺省 beforeRowId = 从当前尾部向前取**。
- 响应：`{rows:[ConversationRow...按 rowId 升序], atSeq, atRevision, atLogEpoch, hasMore}`——**hasMore 直接指示更早方向是否还有行**。
- 行模型：`kind` 判别联合（userInput / assistantText / toolCall / reasoning / artifact / subagent / hookInvocation / turnHeader / timelineMarker），行基字段含 `rowId`（**会话内单调、事件日志的确定性纯函数**——稳定游标）、turnId、createdAt。
- `assistantText` 行带 `state: streaming|complete|interrupted|failed` 与 `model`——时间线 UI 可直接表达中断/失败的生成。
- 与 v1 `session/messages`（正向 afterMessageId 游标）互补：**尾部加载与向上翻页用 rowsRange**。
- 相关限额（`PROTOCOL_V4_LIMITS`）：rowsRangeMaxLimit=200、snapshotTailWindowRows=60、eventRetentionPerSession=2000、conversationQueryTimeoutMs=10s。

## 已验证的完整回合（2026-10-17，C# E2E）

- 普通回合（≈30s）：create →（自动应答 2 个反向请求）→ send accepted → events 轮询（titleUpdated / turn.started / **model.streaming text_delta** / turn.completed）→ `payload.response` 即助手回复 → session/close。
- 取消闭环：长提示词 → 首个 text_delta 到达后 `session/stop`（params 就是 `{sessionId}`，源码 strict schema 确认）→ 回合 30 秒内终结，连接与会话保持可用。
- v4 订阅：subscribe → ACK(subscriptionId) → `v4/conversation/frame` initial 帧推送（topic/subscriptionId/deliveryKind 断言通过）。

## 尚未验证 / 后续

- `session/stop` 的参数形态（M1 接取消功能时核对）
- `session/subscribe`（v1 遗留面，无 unsubscribe）与 **v4 订阅**（`v4/conversation/frame` 推送，clientHello/connectionId 准入要求，见 ADR-0002 开放问题）——事件层从轮询升级为订阅的路径
- `session/resume`、`session/fork`、`session/read`、`session/messages`（历史加载，M2 分页设计输入）

## ZCAC MCP 集成（2026-10-17，独立构建 + 真实集成测试通过 ✅）

- **独立构建配方**（ZCAC 首次脱离 ZCode monorepo 运行）：
  1. 补丁 `src/adapters/zcode/agent-executor.ts` 与 `list-models.ts`：`@zcode/bootstrap` 顶层 import 改为惰性 `await import()`（该路径仅 in-process 模式执行；protocol/fake 模式不触发）——上游贡献候选；
  2. 独立 package.json（deps：zod、@modelcontextprotocol/sdk；devDeps：esbuild）；
  3. `esbuild src/mcp/orchestrator.ts --bundle --platform=node --format=cjs --external:'@zcode/*'`（约 935KB）；
  4. 运行：Node ≥ 24（node:sqlite）；`ZCAC_TEST_FAKE=1` 启用零模型假执行器（AutoFakeExecutor 50ms 自动完成）。
- **MCP 线协议要点**：MCP SDK 严格要求每条消息带 `jsonrpc:"2.0"`（zcode app-server 宽容无此字段——两套组帧不能共用）；初始化握手 `initialize`（protocolVersion "2024-11-05"、clientInfo）→ `notifications/initialized` 通知 → `tools/list` / `tools/call`；工具结果 `content[0].text`（ZCAC 约定为 JSON 字符串）。
- **已验证工具契约**：`cluster_create{task, mode}` → `{runId, taskId, status:"started"}`；`events{runId, afterSequence}` 游标重放（实测 10 事件：RUN_CREATED→TASK_*→AGENT_*→ARTIFACT_CREATED→RUN_COMPLETED）；`cluster_status` → `{status, active, tasks, events 尾巴}`；`task_list` → 任务明细（role/status/attempt/durationMs）；`cluster_stop{runId}`。
- **ZHS 侧实现**：`ZHS.Client.McpStdioClient`（通用 stdio MCP 客户端）+ `ZHS.RuntimeAdapter.ZCode.ZcacClusterAdapter`（ITaskExecutor 的集群实现：create→events 轮询→RUN_COMPLETED，TryCancel→cluster_stop）；集成测试对独立编排器全链路通过。
- **环境事实**：本机 Node 10 过旧，独立 Node 24（/tmp/zhs-m0/node24）+ 编排器 bundle（/tmp/zhs-m0/zcac-standalone）均为临时产物，重启即失；重建配方如上。
