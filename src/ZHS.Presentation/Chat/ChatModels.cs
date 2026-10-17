namespace ZHS.Presentation.Chat;

public sealed record SessionSummary(string SessionId, string Title, string Status);

/// <summary>时间线历史条目（来自 v4 行模型的映射）。</summary>
public sealed record HistoryEntry(long? RowId, string Kind, string? Text, string? State, string? Model);

/// <summary>一页历史：条目按 rowId 升序；HasMore 指示还有更早的行。</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, bool HasMore);

/// <summary>聊天后端抽象：ViewModel 只依赖此接口，具体 Runtime 由适配器实现。</summary>
public interface IChatBackend : IAsyncDisposable
{
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<IChatSession> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>恢复既存会话使其可继续对话（session/resume）。失败由调用方决定是否回退只读。</summary>
    Task<IChatSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>读取既有会话的历史（v4 rowsRange 反向分页；beforeRowId=null 从尾部向前）。</summary>
    Task<HistoryPage> ReadSessionHistoryAsync(string sessionId, long? beforeRowId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>单个聊天会话：一次 SendTurnAsync = 一问一答的完整回合。</summary>
public interface IChatSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>发送消息并等待本回合完成，返回助手回复文本；deltaProgress 实时上报流式增量；回合失败抛异常，取消抛 OperationCanceledException。</summary>
    Task<string> SendTurnAsync(string message, IProgress<string>? deltaProgress, CancellationToken cancellationToken);

    /// <summary>请求中断当前回合（尽力而为）。</summary>
    Task CancelAsync();

    /// <summary>加载更早的历史（反向分页；游标为当前最早 rowId，null = 从尾部开始）。</summary>
    Task<HistoryPage> ReadOlderAsync(long? beforeRowId, int limit, CancellationToken cancellationToken = default);
}

public enum ChatMessageRole
{
    User,
    Assistant,
    System,
}

/// <summary>消息项。生成期间 Assistant 消息的 Text 随流式增量更新（INPC）。</summary>
public sealed class ChatMessageViewModel(ChatMessageRole role, string text) : ObservableObject
{
    private string _text = text;

    public ChatMessageRole Role { get; } = role;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                RaiseChanged(nameof(Display));
            }
        }
    }

    public string Display => Role switch
    {
        ChatMessageRole.User => $"你：{Text}",
        ChatMessageRole.Assistant => $"助手：{Text}",
        _ => Text,
    };
}
