using ZHS.Client;
using ZHS.Presentation.Chat;
using ZHS.Protocol.Runtime;

namespace ZHS.RuntimeAdapter.ZCode;

/// <summary>IChatBackend 的 ZCode 实现：包装 ZcodeRuntime（连接生命周期归后端管理）。</summary>
public sealed class ZcodeChatBackend : IChatBackend
{
    private readonly ZcodeRuntime _runtime;

    public ZcodeChatBackend(ZcodeRuntime runtime)
    {
        _runtime = runtime;
    }

    public static ZcodeChatBackend LaunchDefault(ZcodeAppServerOptions? options = null)
    {
        return new ZcodeChatBackend(ZcodeRuntime.Launch(options));
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        // session/list 只取元数据；借一个临时 handle 走 GetStatusAsync 不合适，这里直接用底层连接语义：
        // ZcodeRuntime 暴露的会话枚举封装在 ListSessionsAsync 辅助方法中。
        return await _runtime.ListSessionSummariesAsync(cancellationToken);
    }

    public async Task<IChatSession> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var handle = await _runtime.StartAsync(
            new AgentTaskRequest(workingDirectory, string.Empty),
            cancellationToken);
        return new ZcodeChatSession(_runtime, handle);
    }

    public async Task<IChatSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var handle = await _runtime.ResumeSessionAsync(sessionId, cancellationToken);
        return new ZcodeChatSession(_runtime, handle);
    }

    public async Task<HistoryPage> ReadSessionHistoryAsync(string sessionId, long? beforeRowId, int limit, CancellationToken cancellationToken = default)
    {
        var result = await _runtime.ReadRowsAsync(sessionId, beforeRowId, limit, cancellationToken);
        return new HistoryPage(
            (result.Rows ?? [])
                .Select(row => new HistoryEntry(row.RowId, row.Kind ?? "unknown", row.Text, row.State, row.Model))
                .ToList(),
            result.HasMore);
    }

    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync();
    }

    private sealed class ZcodeChatSession(ZcodeRuntime runtime, AgentHandle handle) : IChatSession
    {
        public string SessionId => handle.SessionId;

        public async Task<string> SendTurnAsync(string message, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
        {
            var result = await runtime.SendTurnAsync(handle, message, deltaProgress, cancellationToken);
            return result.Outcome switch
            {
                TurnOutcome.Completed => result.Response,
                _ => throw new InvalidOperationException(result.Error ?? "turn failed"),
            };
        }

        public Task CancelAsync() => runtime.InterruptAsync(handle);

        public async Task<HistoryPage> ReadOlderAsync(long? beforeRowId, int limit, CancellationToken cancellationToken = default)
        {
            var result = await runtime.ReadRowsAsync(handle.SessionId, beforeRowId, limit, cancellationToken);
            return new HistoryPage(
                (result.Rows ?? [])
                    .Select(row => new HistoryEntry(row.RowId, row.Kind ?? "unknown", row.Text, row.State, row.Model))
                    .ToList(),
                result.HasMore);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await runtime.CloseSessionAsync(handle);
            }
            catch (Exception)
            {
                // 尽力关闭：进程级清理由 runtime 释放兜底
            }
        }
    }
}
