# Z Hermes Studio（ZHS）

跨平台、原生 C#（.NET / Avalonia）桌面 AI 工作站。以通用 Agent Runtime（初期复用 [ZCode](https://github.com/zai-org/ZCode)）为执行基础，通过统一的项目、任务、记忆、工具、权限和产物体系，支持软件工程、文学创作、研究分析、游戏开发、UI/UX 设计及自动化工作流。

## 当前状态：M0 已收口 → M1 进行中（垂直切片）

**M1 完成（2026-10-17）→ M2 进行中（长对话性能；首轮指标全档达标）**

- **会话恢复**：点击会话列表 → `session/resume` 恢复为可继续对话（e2e 验证：close → resume → 第二回合完成），失败自动回退只读历史。
- **性能（Server GC + ×30 样本）**：首屏 P95 全档 ≤ 186ms（目标 500）；追加批 P95 ≤ 8.8ms；虚拟化在 50k 规模确认生效。详见 [docs/PERFORMANCE.md](docs/PERFORMANCE.md)。

| 项目 | 内容 |
|---|---|
| `src/ZHS.Protocol` | 线协议模型：JSON-RPC 2.0 信封/错误码、帧分类、会话/事件/历史消息/v4 行 DTO、`IAgentRuntime` 抽象 |
| `src/ZHS.Client` | NDJSON 编解码 + `app-server --stdio` 子进程连接：请求关联、通知流、超时、服务端反向请求自动应答、**断连快速失败** |
| `src/ZHS.RuntimeAdapter.ZCode` | `IAgentRuntime` + `IChatBackend`：create/send/stop/list/close/messages + `SendTurnAsync` 流式轮询 + **v4 `rowsRange` 反向分页（loadOlder，带 hasMore）** |
| `src/ZHS.Chat` | §7.3 数据路径核心：`MessageStreamPipeline` 有界队列（增量合并/可丢、信号绝不丢、计数器） |
| `src/ZHS.Presentation` | 纯 MVVM：流式助手消息、取消、会话切换、**历史时间线（初始页 + 加载更早，只读模式）** |
| `src/ZHS.Desktop` | Avalonia 11 壳：会话列表（点击 **resume 恢复可续聊**，失败回退只读）、虚拟化消息列表、贴底跟随、前插锚点补偿、**会话切换取消过期加载（§7.1）**；三平台发布验证通过 |
| `src/ZHS.Persistence` | **ZHS 自有 SQLite（ADR-0004）**：schema_migration 迁移纪律（id+checksum+app_version，篡改即失败）+ app_setting 表 → SettingsStore；`~/.zhs/settings.sqlite`，首个消费者是工作目录记忆 |
| `benchmarks/ZHS.Benchmarks` | **M2 基准框架（ADR-0003）**：Avalonia.Headless 驱动真实 MainWindow，确定性数据集 1k/10k/50k，P50/95/99 + GC/分配 + 管道吞吐；首轮数据见 [docs/PERFORMANCE.md](docs/PERFORMANCE.md)（核心结论：虚拟化生效、追加 P95 ≤ 6.6ms、50k 首屏达标；首屏 P95 尖峰归因 GC，优化计划在案） |
| 测试 ×5 + CI | 54 单测 + 6 真实 e2e（流式回合+历史读回 / 真实取消 / v4 订阅 / rowsRange / 断连×2）全绿 |

关键契约（详见 [docs/zcode-protocol-notes.md](docs/zcode-protocol-notes.md)）：流式增量 = `model.streaming`+`text_delta`；v4 推送订阅直连无门槛；历史分页 = v4 `rowsRange`（`beforeRowId` 游标 + `hasMore`，rowId 会话内单调）；`session/messages` 为正向 `afterMessageId` 游标。

```bash
dotnet build && dotnet test                                  # 全部测试
ZHS_E2E=1 dotnet test tests/ZHS.RuntimeAdapter.ZCode.Tests  # 真实端到端（含一次模型调用）
ZHS_E2E=1 dotnet test tests/ZHS.Client.Tests                 # v4 订阅冒烟（无模型调用）
dotnet run --project src/ZHS.Desktop                         # 桌面壳
dotnet publish src/ZHS.Desktop -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

M2 剩余：Avalonia.Headless 帧计时基准（1k/10k/50k 数据集，ADR-0003 口径）、`session/resume`（继续既有会话对话，当前历史为只读）、`ZHS.Persistence`。


| 文档 | 说明 |
|---|---|
| [docs/ZHS-Development-Document-v0.1.md](docs/ZHS-Development-Document-v0.1.md) | 开发基线（PRD + TDD v0.1.0-draft） |
| [docs/M0-SOURCE-INVENTORY.md](docs/M0-SOURCE-INVENTORY.md) | M0 源码盘点报告（ZCode 上游、ZCAC；两个插件待补） |
| [docs/adr/ADR-0001-native-desktop.md](docs/adr/ADR-0001-native-desktop.md) | Accepted：C# / .NET 10 / Avalonia 原生桌面 |
| [docs/adr/ADR-0002-runtime-adapter.md](docs/adr/ADR-0002-runtime-adapter.md) | Accepted：Runtime Adapter 双层边界（CLI 子进程 + MCP 契约） |
| [docs/adr/ADR-0003-chat-performance.md](docs/adr/ADR-0003-chat-performance.md) | Proposed：长对话性能基准口径 |
| [docs/adr/ADR-0004-storage-ownership.md](docs/adr/ADR-0004-storage-ownership.md) | Proposed：数据所有权（上游库只读经 Adapter；ZHS 自有库唯一写入） |

## 关键上游事实（速查）

- ZCode 上游：`github.com/zai-org/ZCode`，Apache-2.0 + NOTICE，锁定基线 **v3.15.1 = commit `aac4755`**（2026-10-10 发布，源码快照已在手，当前 main 与其零漂移）；注意上游有多条版本线（release v3.x / CLI schema 0.1x.x / 内嵌 CLI 分发 0.13.x）。运行时接缝：`zcode app-server --stdio`（JSON-RPC 2.0 形态 over NDJSON，38 个 v1 方法 + v4 推送订阅 `v4/conversation/frame`，已活体+源码双重验证，见盘点报告 §2.7/§2.8 与 ADR-0002）。
- Agent 集群：`github.com/silicon0725-dev/ZCAC`（v0.3.0，独立 stdio MCP server + SQLite 事件 journal，要求 ZCode ≥ v3.14.3；Apache-2.0，根 LICENSE 已生成待推送）。
- 手柄控制、上下文管理插件：源码在另一设备，待上传后补入盘点。

## 里程碑

M0 盘点（进行中）→ M1 原生桌面垂直切片 → M2 长对话性能 → M3 统一任务与插件 → M4 Artifact 与项目系统 → M5 Writing Workspace → M6 扩展生态与产品化。详见开发文档 §14。
