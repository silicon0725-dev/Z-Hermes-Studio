# ADR-0001：原生跨平台桌面（C# / .NET / Avalonia）

- 状态：**Accepted**（方向冻结，依赖版本随 M1 锁定）
- 日期：2026-10-17
- 依据：开发文档 v0.1 §1、§5；M0 盘点（docs/M0-SOURCE-INVENTORY.md）

## 背景

ZHS 的首要动机之一是解决长会话打开慢、滚动卡顿、流式输出导致 UI 卡顿。上游 ZCode 已有三种 UI 形态：Electron 桌面（本机 /opt/ZCode 即是）、Web（packages/web）、CLI。若沿用 Electron/Web 壳，等于在同样的渲染与状态模型上继续叠加，无法兑现"原生桌面体验"的产品定位。

## 决策

1. 桌面客户端使用 **C# / .NET 10（LTS）/ Avalonia UI 11.x / MVVM**，Windows、Linux、macOS 共用主要 UI 代码。
2. 核心聊天界面不使用 WebView；Markdown/代码高亮使用 Avalonia 原生渲染管线。
3. 平台差异（剪贴板、深链、文件对话框、安全存储、输入设备）统一经 `IPlatformServices` 抽象隔离，三平台各提供实现。
4. 依赖版本集中在 `Directory.Packages.props` 统一管理，升级单独评估。

## 备选方案与否决理由

| 备选 | 否决理由 |
|---|---|
| 复用/包装上游 Electron 桌面 | 正是要逃离的性能与形态；且该产品已存在，ZHS 无增量价值 |
| Tauri（Rust + 系统 WebView） | 技术栈偏离 C# 主线；系统 WebView 差异引入跨平台不一致 |
| Web 应用（本地 server + 浏览器） | 上游已有 packages/web；不满足"原生工作站"定位与手柄等桌面交互 |

## 后果

- 需要自建三平台打包与 CI（M1 交付物），成本高于复用，换来渲染线程可控与长列表虚拟化的主动权。
- Avalonia 虚拟化面板在 50k 消息规模下的实际表现未经本团队验证：M2 基准（ADR-0003）设为硬验收点。若不达标，降级路径是自研虚拟化面板而非更换框架。
- .NET 10 与 Avalonia 11.x 的具体小版本在 M1 建立 `Directory.Packages.props` 时锁定并记录支持周期。

## 开放问题

- 参考机型分级（低/中/高端各一）随 ADR-0003 基准环境确定。
