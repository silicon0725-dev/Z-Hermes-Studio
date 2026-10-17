# ADR-0002：Runtime Adapter —— 与 ZCode 的通信边界

- 状态：**Accepted**（含 3 项开放问题，见文末；2026-10-17 依据 ZCAC 源码快照修订）
- 依据：开发文档 v0.1 §4.2、§8、§13.1；M0 盘点 §2、§3（含源码级核实）

## 背景（M0 证据）

1. **三条经过验证的无头入口**（ZCAC 架构侦察报告，基准 v3.14.3）：
   - 进程内：`createZCodeApp(options)` → `submitPrompt()/.runtime`；
   - **stdio 协议：`zcode app-server --stdio`**（NDJSON ZCode Protocol v1：`session/create` → `session/send` → v4 事件订阅 → `interaction/requestPermission` 权限交互）；
   - HTTP/WS：`packages/server`（Hono，`/ws` + `/api/*`，`ZCODE_SERVER_AUTH_TOKEN`）。
2. **上游桌面自己就以 stdio 协议为进程边界**：Electron Main → Local Host（@zcode/services）→ 每 workspaceKey 一个 CLI 子进程（NDJSON over stdio）。这是上游已规模化运行的接缝，不是逆向猜测。本机进程树实测一致（`zcode-host-local-1` → 多个 `zcode-cli`）。
3. **协议面活体复核**（2026-10-17，本机 v3.15.3-8237，盘点报告 §2.7）：`app-server --stdio` 为 JSON-RPC 2.0 形态 over NDJSON（响应 `{id,result}`、错误用标准码如 `-32601`）；启动即推送带 `sequence` 的 `startup/storageState` 通知（含 schemaVersion 与迁移号）；方法面 38 个（`session/*` 23、`interaction/*` 7、`workspace/*` 8，含 `session/resume`、`session/fork`、`session/events`、`interaction/requestPermission`）。
4. **基线源码核验**（v3.15.1 快照，盘点报告 §2.8）：协议 schema 按版本分目录（`zcode-protocol/` v1、`zcode-protocol-v4/`）且经 Zod `.strict()` 校验；**v4 是推送式订阅**——`v4/conversation/frame`（snapshot/deltas）、订阅 ACK-only + post-response outbox 初始快照、`clientMode ∈ {desktop-continuous, web-remote-replayable}`；旧 v1 `session/subscribe` 为遗留面（无 unsubscribe），ZCAC 观察到的"无回合通知流"仅适用 v1 层。
3. ZCAC v0.3 的 ProtocolAgentExecutor 生产化了这条路径：WorkerProcess → WorkerSession（按 workspace 池化、poison 不回池）→ Turn（幂等 settle、10 分钟超时、事件轮询游标挂 session）。
4. `SubagentPort` 在 `contracts` 中是程序化 API，但**不在 `createZCodeApp` 公共面暴露**——ZCAC 的应对即改走独立协议 Worker。
5. 上游会话库带 `schema_migration`（checksum + app_version，双版本线 v3.x / 0.1x.x）；插件形态不能加 UI、不能在宿主进程执行代码。

## 决策

Adapter 采用**两层边界**，ZHS 的 C# 侧永远只依赖稳定协议，不接触上游私有类型：

### L1 —— 进程边界（M1）

- ZHS Backend Host 以**子进程**驱动 `zcode app-server --stdio`（NDJSON 协议）：会话创建、消息下发、事件获取、权限审批交互（`interaction/requestPermission` ↔ ZHS `approval.required`）。
- 协议信封为 JSON-RPC 2.0 形态（实测 `-32601` 等标准错误码）→ ZHS 自身的"版本化 JSON-RPC 风格协议"（开发文档 §5）与上游原生对齐，Desktop ↔ Backend 协议可直接采用同构设计。
- **事件获取：v1 `session/events` 游标轮询为已实现基线**（M1 聊天切片，含 `model.streaming`/`text_delta` 流式增量与取消闭环）；**v4 推送订阅已活体验证**（2026-10-17：直连 CLI 无 clientHello 门槛，ACK + `v4/conversation/frame` 推送，e2e 冒烟通过）——从轮询升级到 v4 订阅列为 M2 项，帧 schema（rows/delta/coalesce，ZHS §7.3 流式合并的上游参照）建模随 M2 虚拟化时间线一并做。
- 会话池策略沿用 ZCAC 验证过的规则：按 workspace 池化、超时/崩溃即 poison 销毁、干净 session 归还（每 workspace 默认 2 idle）。

### L2 —— 能力边界（M3+；2026-10-17 已用真实 Worker + 真实模型全链路验证 ✅）

- 任务编排与集群复用 **ZCAC 的 MCP 工具面与事件 journal**（16 个工具；`events(runId?, afterSequence?)` 游标重放语义已核实）。ZHS Task Engine 的领域模型映射到该契约；任务状态机直接采纳 ZCAC 的显式迁移表（pending/ready/running/blocked/succeeded/failed/retry_wait/cancelled/interrupted）。
- 若需 ZCAC 尚未暴露的能力，走"monorepo 内嵌自有包"路径（参照 ZCAC `install-inline.js` 的 `plugins.dirs` 注册模式），由 ZHS 维护该内嵌包，不试图导出上游内部端口。

### 传输与安全

- ZHS Desktop ↔ ZHS Backend：**Unix domain socket（Linux/macOS）+ Named Pipe（Windows）**，认证用 Backend 启动时生成、经进程继承传递的随机 token。
- 否决本机 WebSocket：需要自建认证且可能暴露到非预期网络接口（开发文档 §12），相比 UDS/管道无收益。
- ZHS Backend ↔ ZCode worker：stdio（NDJSON），继承 ZCAC 的帧容忍策略（跳过污染行）。

## 备选方案与否决理由

| 备选 | 否决理由 |
|---|---|
| 进程内组合（createZCodeApp） | SubagentPort 不在公共面；与上游进程模型强耦合；ZCAC 已放弃该路线转协议 Worker |
| C# 直接调用/移植上游内部 API | contracts 未作为公共 SDK 发布；双版本线演进，耦合面不可控 |
| HTTP/WS server 接入 | 留作多机/远程扩展选项；单机桌面场景下 stdio 更简单且无网络暴露面 |
| 为 ZHS 给上游提 PR 导出 SDK | 不可控的第三方决策路径，阻塞 M1 |

## 后果

- 必须建立**适配器契约测试**：钉住 `v3.15.1`（及对应 CLI schema 版本）的 `app-server --stdio` 方法面、v4 事件 schema、`interaction/requestPermission` 语义；上游每次升级重跑。ZCAC 的 protocol-cursor/protocol-lifecycle 测试是现成范式。
- 事件映射表（上游 v4 事件 → ZHS §8 事件）成为协议层第一份规格，M1 与垂直切片同步产出。
- ZCAC 需保持可独立升级：ZHS 只依赖其 MCP 契约，不 fork 其内部实现；ZCAC 对上游的版本敏感面集中在 protocol adapter，升级成本可控。

## 开放问题

1. ~~在锁定版本上复核 `app-server --stdio` 方法面与协议信封~~ → **已完成并定案**（2026-10-17：本机 3.15.3 活体探针 + v3.15.1 源码核验；**v3.15.1 = `aac4755666d0` = 当前 main HEAD**）。~~M1 摸清 v4 订阅准入要求~~ → **已回答**（2026-10-17 活体实验 + e2e 冒烟：直连 CLI 无 clientHello 门槛，`clientMode` 取自 wire params；详见协议笔记 v4 节）。后续跟踪项：上游出新 tag 时重跑契约测试并与 `aac4755` 对比 `packages/shared`、`apps/zcode-cli`；v4 帧 schema（rows/delta/coalesce）在 M2 建模。
2. ZCAC 仓库补根 LICENSE 文件 → **已生成待推送**：2026-10-17 产出与标准 Apache-2.0 逐字一致的 LICENSE（版权行 `Copyright 2026 silicon0725-dev`，可自行替换为真实姓名/组织），存于 `/home/silicon/下载/ZCAC-LICENSE`；推送到 `silicon0725-dev/ZCAC` 后本项关闭。
3. 手柄、上下文两个插件的接入点盘点 → **延后**（2026-10-17：源码设备暂不可达）。不阻塞 M1/M2；仅顺延 M3 中"手柄命令适配"与"上下文插件 Adapter"两个交付物。
