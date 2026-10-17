using System.Collections.ObjectModel;
using ZHS.Chat.Streaming;
using ZHS.Domain.Artifacts;

namespace ZHS.Presentation.Chat;

/// <summary>把动作调度到 UI 线程的抽象（Avalonia 由 Desktop 注入 Dispatcher.UIThread.Post）。</summary>
public delegate void UiPost(Action action);

public sealed class ChatViewModel : ObservableObject
{
    public const int HistoryPageSize = 60;

    private readonly IChatSession? _session;
    private readonly UiPost _uiPost;
    private readonly ArtifactEngine? _artifacts;
    private readonly string? _workspace;
    private readonly Func<long?, int, CancellationToken, Task<HistoryPage>> _historyLoader;
    private CancellationTokenSource? _turnCancellation;
    private string _inputText = string.Empty;
    private bool _isBusy;
    private bool _hasMoreHistory;
    private long? _oldestRowId;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public string SessionId { get; }

    /// <summary>会话是否可交互（历史模式下为只读：只能翻看，不能发送）。</summary>
    public bool IsInteractive => _session is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasMoreHistory
    {
        get => _hasMoreHistory;
        private set => SetProperty(ref _hasMoreHistory, value);
    }

    public ChatViewModel(IChatSession session, UiPost uiPost, ArtifactEngine? artifacts = null, string? workspace = null)
        : this(uiPost, session.SessionId, (before, limit, ct) => session.ReadOlderAsync(before, limit, ct), artifacts, workspace)
    {
        _session = session;
    }

    /// <summary>历史（只读）模式：既存会话的时间线浏览。</summary>
    public ChatViewModel(
        UiPost uiPost,
        string sessionId,
        Func<long?, int, CancellationToken, Task<HistoryPage>> historyLoader,
        ArtifactEngine? artifacts = null,
        string? workspace = null)
    {
        _uiPost = uiPost;
        SessionId = sessionId;
        _artifacts = artifacts;
        _workspace = workspace;
        _historyLoader = historyLoader;
        SendCommand = new AsyncRelayCommand(SendAsync);
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync);
        SaveArtifactCommand = new AsyncRelayCommand(SaveLastReplyAsArtifactAsync);
    }

    public AsyncRelayCommand SaveArtifactCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand LoadOlderCommand { get; }

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    private async Task SendAsync()
    {
        var text = InputText?.Trim();
        if (_session is null || string.IsNullOrEmpty(text) || IsBusy)
        {
            return;
        }

        InputText = string.Empty;
        Append(new ChatMessageViewModel(ChatMessageRole.User, text));
        IsBusy = true;
        var cancellation = new CancellationTokenSource();
        _turnCancellation = cancellation;
        var streaming = new ChatMessageViewModel(ChatMessageRole.Assistant, string.Empty);

        // §7.3 数据路径：增量 → 有界管道（合并/可丢）→ UI 提交；信号（完成/失败）绝不丢
        await using var pipeline = new MessageStreamPipeline();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in pipeline.ConsumeAsync(cancellation.Token))
                {
                    if (batch.DeltaText is not null)
                    {
                        _uiPost(() => streaming.Text += batch.DeltaText);
                    }
                    else if (batch.FinalText is not null)
                    {
                        _uiPost(() => streaming.Text = batch.FinalText);
                    }
                    else if (batch.Error is not null)
                    {
                        _uiPost(() => streaming.Text += $"（流中断：{batch.Error}）");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            var progress = new InlineProgress(delta => pipeline.TryWriteDelta(delta));
            var replyTask = _session.SendTurnAsync(text, progress, cancellation.Token);
            Append(streaming);
            var reply = await replyTask;
            await pipeline.WriteSignalAsync(new StreamSignal(StreamSignalKind.TurnCompleted, reply), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            pipeline.Complete();
            await pump;
            Append(new ChatMessageViewModel(ChatMessageRole.System, "（已取消）"));
        }
        catch (Exception exception)
        {
            await pipeline.WriteSignalAsync(new StreamSignal(StreamSignalKind.TurnFailed, null, exception.Message), CancellationToken.None);
            pipeline.Complete();
            await pump;
            Append(new ChatMessageViewModel(ChatMessageRole.System, $"（回合失败：{exception.Message}）"));
        }
        finally
        {
            pipeline.Complete();
            try
            {
                await pump;
            }
            catch (Exception)
            {
                // 泵异常不掩盖回合结果
            }

            _turnCancellation = null;
            IsBusy = false;
        }
    }

    private async Task CancelAsync()
    {
        _turnCancellation?.Cancel();
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.CancelAsync();
        }
        catch (Exception)
        {
            // 中断是尽力而为：取消令牌已触发本地回滚
        }
    }

    /// <summary>加载初始历史页（从尾部向前）。历史模式与会话模式通用。</summary>
    public async Task LoadInitialHistoryAsync(CancellationToken cancellationToken = default)
    {
        var page = await _historyLoader(null, HistoryPageSize, cancellationToken);
        ApplyPage(page, prepend: false);
    }

    /// <summary>加载更早的一页并前插；滚动锚点由视图层负责（开发文档 §7.2）。</summary>
    public async Task LoadOlderAsync()
    {
        if (IsBusy && _session is not null)
        {
            return;
        }

        if (_oldestRowId is null && Messages.Count > 0)
        {
            return;
        }

        var page = await _historyLoader(_oldestRowId, HistoryPageSize, CancellationToken.None);
        ApplyPage(page, prepend: true);
    }

    private void ApplyPage(HistoryPage page, bool prepend)
    {
        var views = page.Entries.Select(ToMessageView).ToList();
        _uiPost(() =>
        {
            if (prepend)
            {
                for (var index = views.Count - 1; index >= 0; index--)
                {
                    Messages.Insert(0, views[index]);
                }
            }
            else
            {
                foreach (var view in views)
                {
                    Messages.Add(view);
                }

                if (!IsInteractive)
                {
                    Messages.Add(new ChatMessageViewModel(ChatMessageRole.System, "（历史只读模式：新建会话后可继续对话）"));
                }
            }
        });

        if (page.Entries.Count > 0 && page.Entries[0].RowId is { } first)
        {
            _oldestRowId = first;
        }

        HasMoreHistory = page.HasMore;
    }

    private static ChatMessageViewModel ToMessageView(HistoryEntry entry)
    {
        return entry.Kind switch
        {
            "userInput" => new ChatMessageViewModel(ChatMessageRole.User, entry.Text ?? string.Empty),
            "assistantText" => new ChatMessageViewModel(
                ChatMessageRole.Assistant,
                entry.Text + (entry.State is "interrupted" or "failed" ? $"（{entry.State}）" : string.Empty)),
            _ => new ChatMessageViewModel(ChatMessageRole.System, $"· {entry.Kind}"),
        };
    }

    /// <summary>把最后一条助手回复保存为 document 产物（sourceRef 指回会话）。</summary>
    private async Task SaveLastReplyAsArtifactAsync()
    {
        if (_artifacts is null || IsBusy)
        {
            return;
        }

        var reply = Messages.LastOrDefault(message => message.Role == ChatMessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Text));
        if (reply is null)
        {
            Append(new ChatMessageViewModel(ChatMessageRole.System, "（没有可保存的助手回复）"));
            return;
        }

        try
        {
            var project = await _artifacts.GetOrCreateProjectAsync(_workspace ?? $"zhs://sessions/{SessionId}");
            var title = reply.Text.Length <= 24 ? reply.Text : reply.Text[..24];
            var artifact = await _artifacts.SaveAsync(
                project.ProjectId,
                ArtifactTypes.Document,
                title,
                reply.Text,
                [$"zcode:session:{SessionId}"]);
            Append(new ChatMessageViewModel(ChatMessageRole.System, $"（已存为产物 {artifact.ArtifactId}：{artifact.Title}）"));
        }
        catch (Exception exception)
        {
            Append(new ChatMessageViewModel(ChatMessageRole.System, $"（保存产物失败：{exception.Message}）"));
        }
    }

    private void Append(ChatMessageViewModel message)
    {
        _uiPost(() => Messages.Add(message));
    }

    public async ValueTask DisposeAsync()
    {
        _turnCancellation?.Cancel();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    /// <summary>同步直调的 IProgress：保证增量顺序与最终文本覆盖之间无竞态（Progress&lt;T&gt; 回调走线程池会有乱序风险）。</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
