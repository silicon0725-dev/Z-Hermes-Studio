using System.Text.Json;

namespace ZHS.Protocol.Wire;

/// <summary>会话归属的工作区（上游必填；workspaceKey 实测必须为字符串，ZCAC 曾因缺省触发 ZodError）。</summary>
public sealed record WorkspaceRef(string WorkspaceKey, string WorkspacePath);

/// <summary>session/list 返回的会话条目（字段来自 v3.15.3 探针实测响应）。</summary>
public sealed record SessionInfo(
    string SessionId,
    string? SessionKind,
    string? Status,
    string? Mode,
    string? Title,
    string? TitleSource,
    string? TraceId,
    long? CreatedAt,
    long? UpdatedAt,
    WorkspaceRef? Workspace);

public sealed record SessionListResult(IReadOnlyList<SessionInfo> Sessions);

/// <summary>session/create 请求参数。mode 取上游权限模式（build/plan/yolo…）；ZCAC 集群 Worker 实测用 "yolo" + 工具白名单。</summary>
public sealed record CreateSessionParams(
    WorkspaceRef Workspace,
    string Mode,
    IReadOnlyList<string>? ToolAllowlist = null);

/// <summary>
/// session/create 响应。上游真实形态为嵌套 result.session.sessionId（ZCAC 实测），
/// 兼容平铺 result.sessionId 与纯字符串两种历史形态。
/// </summary>
public sealed record CreatedSession(string? SessionId, SessionInfo? Session)
{
    public string? ResolveSessionId() => SessionId ?? Session?.SessionId;
}

/// <summary>session/send 请求参数（消息字段实测为 content）。</summary>
public sealed record SendSessionParams(string SessionId, string Content);

/// <summary>session/events 请求参数（游标字段实测为 afterSeq，非 afterSequence）。</summary>
public sealed record SessionEventsParams(string SessionId, long AfterSeq, int Limit = 100);

/// <summary>
/// 事件 payload 的宽松绑定：turn.completed 用 Response；turn.failed 用 Error/Message；
/// 流式增量用 Delta/Done/Kind（v3.15.3 实测为 model.streaming + kind:"text_delta"，
/// v3.15.1 源码另有 part.delta 形态，两种都兼容）。
/// </summary>
public sealed record SessionEventPayload(string? Response, string? Error, string? Message, string? Delta, bool? Done, string? Kind);

/// <summary>
/// session/events 返回的事件条目。
/// 信封字段经 v3.15.1 源码 + v3.15.3 实测确认：判别字段为 type（Zod discriminatedUnion），
/// 序号为 seq；ZCAC 在旧版本观察到 kind，作为兼容回退保留。
/// </summary>
public sealed record SessionEventItem(long? Seq, string? Type, string? Kind, SessionEventPayload? Payload)
{
    public string? EventType => Type ?? Kind;
}

public sealed record SessionEventsResult(IReadOnlyList<SessionEventItem>? Events);

/// <summary>session/messages 请求参数（稳定游标 afterMessageId 分页，无 OFFSET —— 开发文档 §7.1 形态）。</summary>
public sealed record ReadMessagesParams(string SessionId, string? AfterMessageId = null, int? Limit = null);

/// <summary>消息 info（宽松绑定）。实测 info.time 是对象（{start,end}），暂不绑定，需要时再按真实结构扩展。</summary>
public sealed record HistoryMessageInfo(string? Id, string? Role, string? Agent, string? Model);

/// <summary>
/// 历史消息（宽松绑定）。info 是 role 判别联合；parts 是大型判别联合，保留原始 JsonElement，
/// 文本提取走 <see cref="ExtractText"/>（跳过工具/子代理/时间线等分片）。
/// </summary>
public sealed record HistoryMessage(HistoryMessageInfo Info, JsonElement? Parts)
{
    public string ExtractText()
    {
        if (Parts is not { ValueKind: JsonValueKind.Array } parts)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }
}

public sealed record SessionMessagesResult(IReadOnlyList<HistoryMessage>? Messages);

/// <summary>v4 rowsRange 行模型的宽松绑定（kind 判别联合：userInput / assistantText / toolCall / …）。</summary>
public sealed record ConversationRow(long? RowId, string? Kind, string? Text, string? State, string? Model);

/// <summary>v4/conversation/rowsRange 请求：loadOlder 反向分页。缺省 beforeRowId = 从尾部向前；limit ≤ 200。</summary>
public sealed record RowsRangeParams(string SessionId, long? BeforeRowId = null, int Limit = 60);

/// <summary>rowsRange 结果：行按 rowId 升序；hasMore 指示 beforeRowId 方向是否还有更早的行。</summary>
public sealed record RowsRangeResult(
    IReadOnlyList<ConversationRow>? Rows,
    long? AtSeq,
    int? AtRevision,
    string? AtLogEpoch,
    bool HasMore);
