# ADR-0004：数据持久化与所有权

- 状态：**Proposed**（建议直接接受；涉及与既有数据的兼容策略，待评审冻结）
- 日期：2026-10-17
- 依据：开发文档 v0.1 §11；M0 盘点 §2.3–§2.5

## 背景（M0 证据）

1. 上游会话库 `~/.zcode/cli/db/db.sqlite` 带 `schema_migration`（id、checksum、app_version），内部版本线 0.15.2 → 0.16.5 持续演进 —— 外部写入会破坏迁移校验，属于不可直写的库。
2. 上游**自家桌面 App**也不直写 CLI 会话库，而是另建 `~/.zcode/v2/tasks-index.sqlite` 作为索引 —— "单一写入者 + 旁路索引"是上游自身实践的模式。
3. 上游已把大型工具结果按会话落盘为文件引用（`artifacts/sess_*/call_*-tool-result-*.json`），库内只存元数据。

## 决策

### 所有权表

| 数据 | 唯一所有者 | ZHS 访问方式 |
|---|---|---|
| ZCode 会话库、rollout、artifacts、exec 日志（`~/.zcode/**`） | ZCode Runtime | **只读，且仅经 Runtime Adapter 暴露的 API**；C# 侧不打开该库文件，更不写入 |
| ZHS 项目 / Workspace 元数据、Task Graph 与检查点、Artifact 元数据与版本、Memory 与 FTS5 索引、审批与审计记录 | ZHS Backend 自有 SQLite | Desktop 经版本化 RPC 访问 |
| 大型 Artifact 内容 | ZHS Backend 管理的内容存储（文件 + 引用） | 沿用上游"文件引用 + 按会话/项目分目录"模式 |
| Desktop UI 临时状态与缓存 | Desktop 进程 | 可随时丢弃重建，不作为权威数据 |
| API 凭据 | 操作系统安全存储 | 不入库、不入日志、不入 Artifact |

### 实施规则

1. **ZHS 自有库采用与上游同等的迁移纪律**：`schema_migration` 表（id + checksum + app_version），每次变更提供前进迁移与失败恢复说明（开发文档 §18）。
2. ZHS 与上游数据之间的关联只用**引用**（文件路径、session id、artifact id），不共享表、不跨库外键。
3. Desktop 在 MVP **不直读任何 SQLite 文件**（含只读 WAL 连接）；所有读取经 RPC。此决定可在 M2 用基准数据复议（若翻页延迟证明 RPC 成为瓶颈，再评估只读 WAL 方案并修订本 ADR）。
4. 数据增长治理：MVP 起记录库与内容存储总量指标，提供手动清理；正式保留/压缩策略在 M6 前定稿。

## 备选方案与否决理由

| 备选 | 否决理由 |
|---|---|
| Desktop 直连上游会话库读（WAL 只读） | 上游 schema 随 0.1x 版本线演进，读取方需追 schema；且 MVP 阶段翻页量未证明 RPC 不够用 |
| ZHS 迁移/接管上游会话库 | 破坏单一写入者；上游 NOTICE 明示不同运行形态存储行为不同；迁移风险与收益不成比例 |
| 双写（Desktop 与 Backend 各写一份） | 开发文档 §11 明令禁止，无新增证据支持推翻 |

## 后果

- Backend 成为 ZHS 全部自有数据的唯一写入点；Desktop 崩溃不产生数据修复问题。
- 会话历史展示延迟多一跳 RPC；由 M2 基准验证是否达标（ADR-0003 口径）。

## 开放问题

1. FTS5 中文分词方案（简易二元分词 vs trigram vs 外部分词器）在 M2 前用真实语料评测后定。
2. 上游 rollout/model-io JSONL 是否需要 ZHS 侧索引（研究"原始来源回溯"场景）—— 随 M4 Artifact 的 sourceRefs 设计一并定。
