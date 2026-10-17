using System.Text.Json;
using ZHS.Client;
using ZHS.Protocol.Runtime;
using ZHS.Protocol.Wire;

namespace ZHS.RuntimeAdapter.ZCode;

/// <summary>单次回合的结果。</summary>
public enum TurnOutcome
{
    Completed,
    Failed,
}

public sealed record TurnResult(TurnOutcome Outcome, string Response, string? Error = null);

/// <summary>
/// IAgentRuntime 的 ZCode 首版实现（M1 切片）：create/send/stop/list 映射 + 回合轮询。
/// v1 层无回合完成通知流（ZCAC 实测），Turn 完成靠 session/events 游标轮询（ADR-0002）。
/// 会话池化与 v4 订阅在后续切片接入。
/// </summary>
public sealed class ZcodeRuntime : IAgentRuntime
{
    private static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(10);
    private readonly IZcodeProtocolConnection _connection;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _turnTimeout;
    private long _sequence;

    public ZcodeRuntime(IZcodeProtocolConnection connection, TimeSpan? pollInterval = null, TimeSpan? turnTimeout = null)
    {
        _connection = connection;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
        _turnTimeout = turnTimeout ?? DefaultTurnTimeout;
    }

    /// <summary>启动子进程连接并包装为 Runtime。调用方负责 DisposeAsync。</summary>
    public static ZcodeRuntime Launch(ZcodeAppServerOptions? options = null)
    {
        return new ZcodeRuntime(ZcodeAppServerConnection.Start(options));
    }

    public async Task<AgentHandle> StartAsync(AgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        // workspaceKey 必填且实测必须为字符串：ZCAC 用工作目录本身作 key
        var workspace = new WorkspaceRef(request.WorkingDirectory, request.WorkingDirectory);
        var parameters = new CreateSessionParams(workspace, request.Mode ?? "yolo", request.ToolAllowlist);
        var result = await _connection.SendRequestAsync("session/create", parameters, cancellationToken)
            ?? throw new InvalidOperationException("session/create returned empty result");

        var created = result.Deserialize<CreatedSession>(ProtocolJson.Options)
            ?? throw new InvalidOperationException("session/create returned unparsable result");
        var sessionId = created.ResolveSessionId()
            ?? throw new InvalidOperationException($"session/create returned no sessionId: {result.GetRawText()}");

        return new AgentHandle(
            $"zc-{Interlocked.Increment(ref _sequence):000}",
            sessionId,
            request.Model,
            request.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    public Task SendMessageAsync(AgentHandle handle, string message, CancellationToken cancellationToken = default)
    {
        return _connection.SendRequestAsync("session/send", new SendSessionParams(handle.SessionId, message), cancellationToken);
    }

    /// <summary>发送一条消息并轮询 session/events 直到本回合终结（turn.completed / turn.failed）。</summary>
    public Task<TurnResult> SendTurnAsync(AgentHandle handle, string message, CancellationToken cancellationToken = default)
    {
        return SendTurnAsync(handle, message, null, cancellationToken);
    }

    /// <summary>
    /// 发送一条消息并轮询 session/events 直到本回合终结。
    /// 事件信封：判别字段 type（26 种），序号 seq；part.delta 事件（payload.delta）经 progress 实时上报。
    /// </summary>
    public async Task<TurnResult> SendTurnAsync(AgentHandle handle, string message, IProgress<string>? deltaProgress, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(handle, message, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_turnTimeout);
        long cursor = 0;
        while (true)
        {
            var result = await _connection.SendRequestAsync(
                "session/events",
                new { sessionId = handle.SessionId, afterSeq = cursor, limit = 100 },
                timeout.Token) ?? throw new InvalidOperationException("session/events returned empty result");

            var batch = result.Deserialize<SessionEventsResult>(ProtocolJson.Options);
            foreach (var item in batch?.Events ?? [])
            {
                if (item.Seq is > 0)
                {
                    cursor = Math.Max(cursor, item.Seq.Value);
                }

                if (item.EventType is "part.delta" or "model.streaming")
                {
                    // model.streaming 的 kind 区分文本增量与工具调用增量；文本才上报 UI
                    var isTextDelta = item.Payload?.Kind is null or "text_delta";
                    if (isTextDelta && !string.IsNullOrEmpty(item.Payload?.Delta))
                    {
                        deltaProgress?.Report(item.Payload.Delta!);
                    }
                }
                else if (item.EventType == "turn.completed")
                {
                    return new TurnResult(TurnOutcome.Completed, item.Payload?.Response ?? "");
                }
                else if (item.EventType == "turn.failed")
                {
                    return new TurnResult(TurnOutcome.Failed, "", item.Payload?.Error ?? item.Payload?.Message ?? "turn failed");
                }
            }

            await Task.Delay(_pollInterval, timeout.Token);
        }
    }

    /// <summary>
    /// 恢复既存会话（session/resume，最小参数 {sessionId}；返回与 create 同款快照，sessionId 在 result.session.sessionId）。
    /// 冷恢复沿用既存上下文；可在恢复后的会话上继续 session/send。
    /// </summary>
    public async Task<AgentHandle> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync("session/resume", new { sessionId }, cancellationToken)
            ?? throw new InvalidOperationException("session/resume returned empty result");
        var resumed = result.Deserialize<CreatedSession>(ProtocolJson.Options)
            ?? throw new InvalidOperationException("session/resume returned unparsable result");
        var resolved = resumed.ResolveSessionId() ?? sessionId;
        return new AgentHandle($"zc-{Interlocked.Increment(ref _sequence):000}", resolved);
    }

    /// <summary>尽力关闭会话（session/close）；失败由调用方决定是否忽略。</summary>
    public Task CloseSessionAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        return _connection.SendRequestAsync("session/close", new { sessionId = handle.SessionId }, cancellationToken);
    }

    /// <summary>
    /// v4 行分页（loadOlder）：缺省 beforeRowId 从尾部向前取，行按 rowId 升序返回，hasMore 指示还有更早的行。
    /// rowId 会话内单调、事件日志的确定性纯函数 —— 稳定游标。
    /// </summary>
    public async Task<RowsRangeResult> ReadRowsAsync(
        string sessionId,
        long? beforeRowId = null,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync(
            "v4/conversation/rowsRange",
            new RowsRangeParams(sessionId, beforeRowId, limit),
            cancellationToken) ?? throw new InvalidOperationException("rowsRange returned empty result");
        return result.Deserialize<RowsRangeResult>(ProtocolJson.Options)
            ?? throw new InvalidOperationException("rowsRange returned unparsable result");
    }

    /// <summary>
    /// 读取会话历史消息（session/messages，稳定游标 afterMessageId 分页，无 OFFSET）。
    /// 返回顺序按上游默认（时间正序）；翻页用最后一条的 Id 作下一页游标。
    /// </summary>
    public async Task<IReadOnlyList<HistoryMessage>> ReadMessagesAsync(
        AgentHandle handle,
        string? afterMessageId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync(
            "session/messages",
            new ReadMessagesParams(handle.SessionId, afterMessageId, limit),
            cancellationToken) ?? throw new InvalidOperationException("session/messages returned empty result");
        var parsed = result.Deserialize<SessionMessagesResult>(ProtocolJson.Options);
        return parsed?.Messages ?? [];
    }

    public Task InterruptAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        // session/stop 的参数形态（sessionId 字段名）尚未在本机验证，M1 聊天切片接入取消功能时核对
        return _connection.SendRequestAsync("session/stop", new { sessionId = handle.SessionId }, cancellationToken);
    }

    public async Task<AgentStatus> GetStatusAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionSummariesAsync(cancellationToken);
        var status = sessions.FirstOrDefault(candidate => candidate.SessionId == handle.SessionId)?.Status;
        return status switch
        {
            "running" => AgentStatus.Running,
            "idle" => AgentStatus.Idle,
            "complete" or "completed" => AgentStatus.Completed,
            "failed" or "error" => AgentStatus.Failed,
            "cancelled" or "canceled" => AgentStatus.Cancelled,
            _ => AgentStatus.Unknown,
        };
    }

    public async Task<IReadOnlyList<Presentation.Chat.SessionSummary>> ListSessionSummariesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync("session/list", null, cancellationToken)
            ?? throw new InvalidOperationException("session/list returned empty result");
        var list = result.Deserialize<SessionListResult>(ProtocolJson.Options);
        return list?.Sessions
            .Select(session => new Presentation.Chat.SessionSummary(
                session.SessionId,
                string.IsNullOrEmpty(session.Title) ? "(未命名)" : session.Title!,
                session.Status ?? string.Empty))
            .ToList() ?? [];
    }

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var notification in _connection.ReadNotificationsAsync(cancellationToken))
        {
            yield return new AgentEvent(AgentEventKind.Raw, null, notification.Params);
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
