using ZHS.Presentation.Chat;
using Xunit;

namespace ZHS.Presentation.Tests;

public sealed class FakeChatBackend : IChatBackend
{
    public List<string> CreatedWorkspaces { get; } = [];

    public List<FakeChatSession> Sessions { get; } = [];

    public IReadOnlyList<SessionSummary> SessionsToReturn { get; set; } = [];

    public Func<string, long?, int, HistoryPage>? HistoryReader { get; set; }

    public Func<string, IChatSession>? SessionOpener { get; set; }

    /// <summary>按会话挂起历史读取（用于测试切换取消：挂起直到 ct 取消）。</summary>
    public Func<string, CancellationToken, Task>? HistoryGate { get; set; }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SessionsToReturn);
    }

    public Task<IChatSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (SessionOpener is not null)
        {
            return Task.FromResult(SessionOpener(sessionId));
        }

        throw new InvalidOperationException("resume unavailable in fake");
    }

    public Task<IChatSession> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        CreatedWorkspaces.Add(workingDirectory);
        var session = new FakeChatSession($"sess_fake_{Sessions.Count + 1}");
        Sessions.Add(session);
        return Task.FromResult<IChatSession>(session);
    }

    public async Task<HistoryPage> ReadSessionHistoryAsync(string sessionId, long? beforeRowId, int limit, CancellationToken cancellationToken = default)
    {
        if (HistoryGate is not null)
        {
            await HistoryGate(sessionId, cancellationToken);
        }

        return HistoryReader?.Invoke(sessionId, beforeRowId, limit)
            ?? new HistoryPage([], HasMore: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeChatSession(string sessionId) : IChatSession
{
    public string SessionId { get; } = sessionId;

    public List<string> Received { get; } = [];

    public string NextReply { get; set; } = "这是助手的回复。";

    public string[] Deltas { get; set; } = [];

    public Queue<HistoryPage> HistoryPages { get; } = new();

    /// <summary>历史读取门（测试挂起点：挂起直到 ct 取消）。</summary>
    public Func<CancellationToken, Task>? ReadOlderGate { get; set; }

    public int CancelCount { get; private set; }

    public Task<string> SendTurnAsync(string message, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
    {
        Received.Add(message);
        foreach (var delta in Deltas)
        {
            deltaProgress?.Report(delta);
        }

        return Task.FromResult(NextReply);
    }

    public Task CancelAsync()
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public async Task<HistoryPage> ReadOlderAsync(long? beforeRowId, int limit, CancellationToken cancellationToken = default)
    {
        if (ReadOlderGate is not null)
        {
            await ReadOlderGate(cancellationToken);
        }

        return HistoryPages.Count > 0
            ? HistoryPages.Dequeue()
            : new HistoryPage([], HasMore: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MainViewModelTests
{
    private static UiPost Inline => action => action();

    [Fact]
    public async Task LoadSessions_PopulatesCollectionAndStatus()
    {
        var backend = new FakeChatBackend
        {
            SessionsToReturn =
            [
                new SessionSummary("sess_1", "修复启动错误", "idle"),
                new SessionSummary("sess_2", "调研", "running"),
            ],
        };
        var viewModel = new MainViewModel(backend, Inline);

        await viewModel.LoadSessionsAsync();

        Assert.Equal(2, viewModel.Sessions.Count);
        Assert.Contains("2 个会话", viewModel.StatusText);
    }

    [Fact]
    public async Task CreateSession_SwitchesCurrentChat_AndClosesPrevious()
    {
        var backend = new FakeChatBackend();
        var viewModel = new MainViewModel(backend, Inline) { WorkspacePath = "/tmp/ws" };

        await viewModel.CreateSessionAsync();
        var first = viewModel.CurrentChat;
        Assert.NotNull(first);

        await viewModel.CreateSessionAsync();
        var second = viewModel.CurrentChat;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, backend.CreatedWorkspaces.Count);
    }

    [Fact]
    public async Task CreateSession_EmptyWorkspace_ShowsStatus()
    {
        var backend = new FakeChatBackend();
        var viewModel = new MainViewModel(backend, Inline) { WorkspacePath = " " };

        await viewModel.CreateSessionAsync();

        Assert.Null(viewModel.CurrentChat);
        Assert.Contains("工作目录", viewModel.StatusText);
    }
}

public sealed class ChatViewModelTests
{
    private static UiPost Inline => action => action();

    [Fact]
    public async Task Send_AppendsUserThenAssistant_AndClearsInput()
    {
        var session = new FakeChatSession("sess_1") { NextReply = "好的" };
        var viewModel = new ChatViewModel(session, Inline) { InputText = "你好" };

        await viewModel.SendCommand.ExecuteAndWaitAsync();

        Assert.Equal(["你好"], session.Received);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal(ChatMessageRole.User, viewModel.Messages[0].Role);
        Assert.Equal(ChatMessageRole.Assistant, viewModel.Messages[1].Role);
        Assert.Equal("好的", viewModel.Messages[1].Text);
        Assert.Equal(string.Empty, viewModel.InputText);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Send_StreamsDeltasThenFinalizesText()
    {
        var session = new FakeChatSession("sess_1")
        {
            Deltas = ["你", "好"],
            NextReply = "你好",
        };
        var viewModel = new ChatViewModel(session, Inline) { InputText = "hi" };

        await viewModel.SendCommand.ExecuteAndWaitAsync();

        // 流式消息就是助手消息（第二条）：最终文本以回合结果为准
        Assert.Equal(2, viewModel.Messages.Count);
        var assistant = viewModel.Messages[1];
        Assert.Equal(ChatMessageRole.Assistant, assistant.Role);
        Assert.Equal("你好", assistant.Text);
    }

    [Fact]
    public async Task Send_Failure_ShowsSystemMessageWithoutThrowing()
    {
        var session = new FailingSession();
        var viewModel = new ChatViewModel(session, Inline) { InputText = "触发失败" };

        await viewModel.SendCommand.ExecuteAndWaitAsync();

        Assert.Equal(ChatMessageRole.System, viewModel.Messages[^1].Role);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Cancel_CancelsLocalToken_AndNotifiesSession()
    {
        var session = new FakeChatSession("sess_1");
        var viewModel = new ChatViewModel(session, Inline);

        await viewModel.CancelCommand.ExecuteAndWaitAsync();

        Assert.Equal(1, session.CancelCount);
    }

    private sealed class FailingSession : IChatSession
    {
        public string SessionId => "sess_fail";

        public Task<string> SendTurnAsync(string message, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }

        public Task CancelAsync() => Task.CompletedTask;

        public Task<HistoryPage> ReadOlderAsync(long? beforeRowId, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HistoryPage([], HasMore: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class HistoryViewModelTests
{
    private static UiPost Inline => action => action();

    private static HistoryPage Page(bool hasMore, params (long rowId, string kind, string text)[] entries)
    {
        return new HistoryPage(
            entries.Select(entry => new HistoryEntry(entry.rowId, entry.kind, entry.text, "complete", null)).ToList(),
            hasMore);
    }

    [Fact]
    public async Task InitialHistory_PopulatesMessages_InAscendingOrder()
    {
        var session = new FakeChatSession("sess_1");
        session.HistoryPages.Enqueue(Page(
            hasMore: true,
            (1, "userInput", "第一句"),
            (2, "userInput", "你好"),
            (3, "assistantText", "好的")));
        var viewModel = new ChatViewModel(session, Inline);

        await viewModel.LoadInitialHistoryAsync();

        Assert.Equal(3, viewModel.Messages.Count);
        Assert.Equal("第一句", viewModel.Messages[0].Text);
        Assert.Equal(ChatMessageRole.Assistant, viewModel.Messages[^1].Role);
        Assert.True(viewModel.HasMoreHistory);
    }

    [Fact]
    public async Task LoadOlder_PrependsOlderPage_PreservesExisting()
    {
        var session = new FakeChatSession("sess_1");
        session.HistoryPages.Enqueue(Page(hasMore: true, (2, "userInput", "你好"), (3, "assistantText", "好的")));
        session.HistoryPages.Enqueue(Page(hasMore: false, (1, "userInput", "更早的一句")));
        var viewModel = new ChatViewModel(session, Inline);
        await viewModel.LoadInitialHistoryAsync();

        await viewModel.LoadOlderCommand.ExecuteAndWaitAsync();

        Assert.Equal(3, viewModel.Messages.Count);
        Assert.Equal("更早的一句", viewModel.Messages[0].Text);
        Assert.Equal("好的", viewModel.Messages[^1].Text);
        Assert.False(viewModel.HasMoreHistory);
    }

    [Fact]
    public async Task ReadOnlyHistoryMode_CannotSend()
    {
        var backend = new FakeChatBackend
        {
            HistoryReader = (_, _, _) => Page(hasMore: false, (1, "userInput", "旧消息")),
        };
        var main = new MainViewModel(backend, Inline);
        await main.OpenSessionAsync(new SessionSummary("sess_old", "旧会话", "idle"));

        Assert.NotNull(main.CurrentChat);
        Assert.False(main.CurrentChat.IsInteractive);
        main.CurrentChat.InputText = "不应该发送";
        await main.CurrentChat.SendCommand.ExecuteAndWaitAsync();
        Assert.Equal(1, main.CurrentChat.Messages.Count(m => m.Role == ChatMessageRole.User));
        Assert.Contains("只读", main.CurrentChat.Messages[^1].Text);
    }

    [Fact]
    public async Task OpenSession_ResumesInteractive_WithHistoryPreloaded()
    {
        var session = new FakeChatSession("sess_old");
        session.HistoryPages.Enqueue(Page(hasMore: false, (1, "userInput", "旧消息")));
        var backend = new FakeChatBackend { SessionOpener = _ => session };
        var main = new MainViewModel(backend, Inline);

        await main.OpenSessionAsync(new SessionSummary("sess_old", "旧会话", "idle"));

        Assert.NotNull(main.CurrentChat);
        Assert.True(main.CurrentChat.IsInteractive);            // 恢复成功 → 可继续对话
        Assert.Single(main.CurrentChat.Messages);               // 只含历史条目，无只读提示
        Assert.Equal("旧消息", main.CurrentChat.Messages[0].Text);
    }

    [Fact]
    public async Task OpenSession_ResumeFails_FallsBackToReadOnly()
    {
        var backend = new FakeChatBackend
        {
            HistoryReader = (_, _, _) => Page(hasMore: false, (1, "userInput", "旧消息")),
        };
        var main = new MainViewModel(backend, Inline);

        await main.OpenSessionAsync(new SessionSummary("sess_old", "旧会话", "idle"));

        Assert.NotNull(main.CurrentChat);
        Assert.False(main.CurrentChat.IsInteractive);           // 恢复失败 → 只读回退
        Assert.Contains("只读", main.StatusText);
    }
}

internal static class CommandExtensions
{
    public static async Task ExecuteAndWaitAsync(this AsyncRelayCommand command)
    {
        command.Execute(null);
        if (command.LastExecution is not null)
        {
            await command.LastExecution;
        }
    }
}

public sealed class SettingsAndCancellationTests
{
    private static UiPost Inline => action => action();

    [Fact]
    public async Task Initialize_RestoresSavedWorkspacePath()
    {
        var settings = new InMemoryAppSettings();
        await settings.SetAsync("workspace.path", "/tmp/saved-ws");
        var main = new MainViewModel(new FakeChatBackend(), Inline, settings);

        await main.InitializeAsync();

        Assert.Equal("/tmp/saved-ws", main.WorkspacePath);
    }

    [Fact]
    public async Task CreateSession_PersistsWorkspacePath()
    {
        var settings = new InMemoryAppSettings();
        var backend = new FakeChatBackend();
        var main = new MainViewModel(backend, Inline, settings) { WorkspacePath = "/tmp/persist-me" };

        await main.CreateSessionAsync();

        Assert.Equal("/tmp/persist-me", await settings.GetAsync("workspace.path"));
    }

    [Fact]
    public async Task OpenSession_SwitchCancelsPreviousOpen()
    {
        // 同一后端：慢会话的历史加载挂起；切换到快会话必须取消慢加载且不写界面
        var backend = new FakeChatBackend
        {
            SessionOpener = id =>
            {
                var session = new FakeChatSession(id);
                session.HistoryPages.Enqueue(new HistoryPage(
                    [new HistoryEntry(1, "userInput", $"{id} 的历史", "complete", null)],
                    HasMore: false));
                if (id == "sess_slow")
                {
                    session.ReadOlderGate = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct); // 挂起直到被取消
                }

                return session;
            },
        };
        var main = new MainViewModel(backend, Inline);
        var firstOpen = main.OpenSessionAsync(new SessionSummary("sess_slow", "慢会话", "idle"));
        await Task.Delay(100); // 确保 firstOpen 进入历史加载挂起

        await main.OpenSessionAsync(new SessionSummary("sess_fast", "快会话", "idle"));

        Assert.Equal("sess_fast", main.CurrentChat?.SessionId); // 界面已是新会话
        Assert.Single(main.CurrentChat!.Messages);               // 只有快会话的历史，慢会话结果未写入
        await firstOpen;                                         // 被取消后静默完成，不抛异常
    }
}
