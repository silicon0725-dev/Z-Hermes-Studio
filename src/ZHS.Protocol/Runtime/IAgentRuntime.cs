using System.Text.Json;

namespace ZHS.Protocol.Runtime;

/// <summary>启动一个 Agent 会话的请求。</summary>
/// <param name="Mode">上游权限模式（build/plan/yolo…）；缺省由适配器决定（ZCAC Worker 惯例为 yolo + 白名单）。</param>
public sealed record AgentTaskRequest(
    string WorkingDirectory,
    string Prompt,
    string? Model = null,
    string? Mode = null,
    IReadOnlyList<string>? ToolAllowlist = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentHandle(
    string AgentId,
    string SessionId,
    string? Model = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public enum AgentStatus
{
    Created,
    Running,
    AwaitingInput,
    Idle,
    Completed,
    Failed,
    Cancelled,
    Unknown,
}

public enum AgentEventKind
{
    Started,
    MessageDelta,
    ToolStarted,
    ToolCompleted,
    PermissionRequested,
    TurnCompleted,
    Failed,
    Cancelled,
    /// <summary>尚未归类映射的上游原始事件（M1 切片期透传，载荷为原始 params）。</summary>
    Raw,
}

public sealed record AgentEvent(AgentEventKind Kind, string? SessionId, JsonElement? Payload = null, long? Sequence = null);

/// <summary>
/// 与具体 Runtime 无关的 Agent 控制面（开发文档 §4.2 的接口 + 事件出口与生命周期）。
/// ZHS 的 UI / Application 层只依赖此抽象，不接触 ZCode 私有类型。
/// </summary>
public interface IAgentRuntime : IAsyncDisposable
{
    Task<AgentHandle> StartAsync(AgentTaskRequest request, CancellationToken cancellationToken = default);

    Task SendMessageAsync(AgentHandle handle, string message, CancellationToken cancellationToken = default);

    Task InterruptAsync(AgentHandle handle, CancellationToken cancellationToken = default);

    Task<AgentStatus> GetStatusAsync(AgentHandle handle, CancellationToken cancellationToken = default);

    /// <summary>读取该 Runtime 的下游事件流（通知与未归类的服务端请求）。</summary>
    IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}
