using System.Collections.ObjectModel;

namespace ZHS.Presentation.Chat;

public sealed class MainViewModel : ObservableObject
{
    private readonly IChatBackend _backend;
    private readonly UiPost _uiPost;
    private readonly IAppSettings? _settings;
    private readonly ZHS.Domain.Artifacts.ArtifactEngine? _artifacts;
    private CancellationTokenSource? _openCancellation;

    public ObservableCollection<SessionSummary> Sessions { get; } = [];

    public ChatViewModel? CurrentChat
    {
        get => _currentChat;
        set => SetProperty(ref _currentChat, value);
    }

    private ChatViewModel? _currentChat;

    /// <summary>会话列表选中项：切换即打开该会话的历史时间线（只读）。</summary>
    public SessionSummary? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value) && value is not null)
            {
                _ = OpenSessionAsync(value);
            }
        }
    }

    private SessionSummary? _selectedSession;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _statusText = "就绪";

    /// <summary>新建会话使用的工作目录（M1 切片用文本框，后续换成目录选择器）。</summary>
    public string WorkspacePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "zhs-workspace");

    public MainViewModel(IChatBackend backend, UiPost uiPost, IAppSettings? settings = null, ZHS.Domain.Artifacts.ArtifactEngine? artifacts = null)
    {
        _backend = backend;
        _uiPost = uiPost;
        _settings = settings;
        _artifacts = artifacts;
        LoadSessionsCommand = new AsyncRelayCommand(LoadSessionsAsync);
        CreateSessionCommand = new AsyncRelayCommand(CreateSessionAsync);
    }

    /// <summary>启动时恢复持久化设置（当前仅工作目录；失败不阻塞启动）。</summary>
    public async Task InitializeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            var saved = await _settings.GetAsync("workspace.path");
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _uiPost(() => WorkspacePath = saved);
            }
        }
        catch (Exception)
        {
            // 设置读取失败：保持默认值
        }
    }

    /// <summary>取消上一次未完成的会话打开（§7.1：切换后旧结果不得写入/覆盖当前界面）。</summary>
    private CancellationToken BeginOpen()
    {
        _openCancellation?.Cancel();
        _openCancellation = new CancellationTokenSource();
        return _openCancellation.Token;
    }

    public AsyncRelayCommand LoadSessionsCommand { get; }

    public AsyncRelayCommand CreateSessionCommand { get; }

    public async Task LoadSessionsAsync()
    {
        try
        {
            var sessions = await _backend.ListSessionsAsync();
            _uiPost(() =>
            {
                Sessions.Clear();
                foreach (var session in sessions)
                {
                    Sessions.Add(session);
                }

                StatusText = $"已连接 · {sessions.Count} 个会话";
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"加载失败：{exception.Message}");
        }
    }

    public async Task CreateSessionAsync()
    {
        var workspace = WorkspacePath?.Trim();
        if (string.IsNullOrEmpty(workspace))
        {
            _uiPost(() => StatusText = "请先填写工作目录");
            return;
        }

        BeginOpen();
        try
        {
            var previous = CurrentChat;
            var session = await _backend.CreateSessionAsync(workspace);
            _uiPost(() =>
            {
                CurrentChat = new ChatViewModel(session, _uiPost)
                {
                    // 空会话提示
                };
                CurrentChat.Messages.Add(new ChatMessageViewModel(
                    ChatMessageRole.System,
                    $"会话 {session.SessionId} 已创建（工作目录 {workspace}）"));
                StatusText = $"当前会话：{session.SessionId}";
            });
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            if (_settings is not null)
            {
                await _settings.SetAsync("workspace.path", workspace);
            }
        }
        catch (OperationCanceledException)
        {
            // 切换导致的取消：静默
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"创建会话失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 打开既存会话：优先 session/resume（可继续对话），失败则回退只读历史（v4 rowsRange）。
    /// </summary>
    public async Task OpenSessionAsync(SessionSummary summary)
    {
        var previous = CurrentChat;
        var cancellation = BeginOpen();
        try
        {
            var session = await _backend.OpenSessionAsync(summary.SessionId, cancellation);
            var chat = new ChatViewModel(session, _uiPost, _artifacts, WorkspacePath);
            _uiPost(() =>
            {
                CurrentChat = chat;
                StatusText = $"已恢复会话：{summary.Title}";
            });
            await chat.LoadInitialHistoryAsync(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return; // 会话已切换：本次打开作废（取消 ≠ 恢复失败，不触发回退）
        }
        catch (Exception resumeException)
        {
            // 恢复失败（如会话属于其他 workspace）：回退只读历史
            try
            {
                var chat = new ChatViewModel(
                    _uiPost,
                    summary.SessionId,
                    (before, limit, ct) => _backend.ReadSessionHistoryAsync(summary.SessionId, before, limit, ct),
                    _artifacts,
                    WorkspacePath);
                _uiPost(() =>
                {
                    CurrentChat = chat;
                    StatusText = $"历史会话：{summary.Title}（只读，恢复失败：{resumeException.Message}）";
                });
                await chat.LoadInitialHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                return; // 已切换到别的会话：静默丢弃本次打开
            }
            catch (Exception historyException)
            {
                _uiPost(() => StatusText = $"打开会话失败：{historyException.Message}");
                return;
            }
        }

        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (CurrentChat is not null)
        {
            await CurrentChat.DisposeAsync();
        }

        await _backend.DisposeAsync();
    }
}
