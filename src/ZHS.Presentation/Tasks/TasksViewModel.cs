using System.Collections.ObjectModel;
using ZHS.Domain.Tasks;
using ZHS.Presentation.Chat;

namespace ZHS.Presentation.Tasks;

public sealed class TaskItemViewModel : ObservableObject
{
    private string _status = string.Empty;

    public string TaskId { get; }

    public string Title { get; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string? Output { get; set; }

    public TaskItemViewModel(string taskId, string title, string status, string? output)
    {
        TaskId = taskId;
        Title = title;
        _status = status;
        Output = output;
    }
}

/// <summary>M3 任务面板：创建并执行任务（单会话执行器），查看状态与执行日志。</summary>
public sealed class TasksViewModel : ObservableObject
{
    private readonly TaskEngine _engine;
    private readonly UiPost _uiPost;
    private readonly Func<string> _workspaceProvider;
    private TaskItemViewModel? _selectedTask;
    private string _newPrompt = "请只回复四个字：任务完成";
    private string _journalText = string.Empty;
    private string _statusText = "任务就绪";

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];

    public TasksViewModel(TaskEngine engine, UiPost uiPost, Func<string> workspaceProvider)
    {
        _engine = engine;
        _uiPost = uiPost;
        _workspaceProvider = workspaceProvider;
        CreateAndRunCommand = new AsyncRelayCommand(CreateAndRunAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public AsyncRelayCommand CreateAndRunCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public string NewPrompt
    {
        get => _newPrompt;
        set => SetProperty(ref _newPrompt, value);
    }

    public TaskItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value) && value is not null)
            {
                _ = LoadJournalAsync(value.TaskId);
            }
        }
    }

    public string JournalText
    {
        get => _journalText;
        private set => SetProperty(ref _journalText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var records = await _engine.ListAsync();
            _uiPost(() =>
            {
                Tasks.Clear();
                foreach (var record in records)
                {
                    Tasks.Add(new TaskItemViewModel(record.TaskId, record.Title, record.Status.ToString(), record.OutputText));
                }

                StatusText = $"共 {records.Count} 个任务";
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"任务列表加载失败：{exception.Message}");
        }
    }

    private async Task CreateAndRunAsync()
    {
        var prompt = NewPrompt?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            _uiPost(() => StatusText = "请先填写任务提示词");
            return;
        }

        var workspace = _workspaceProvider();
        try
        {
            var task = await _engine.CreateAsync(prompt[..Math.Min(20, prompt.Length)], prompt, workspace);
            _uiPost(() =>
            {
                Tasks.Insert(0, new TaskItemViewModel(task.TaskId, task.Title, task.Status.ToString(), null));
                StatusText = $"任务 {task.TaskId} 执行中…";
            });
            _ = RunAndReportAsync(task.TaskId);
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"创建任务失败：{exception.Message}");
        }
    }

    private async Task RunAndReportAsync(string taskId)
    {
        try
        {
            var final = await _engine.RunAsync(taskId);
            _uiPost(() =>
            {
                var item = Tasks.FirstOrDefault(candidate => candidate.TaskId == taskId);
                if (item is not null)
                {
                    item.Status = final.Status.ToString();
                    item.Output = final.OutputText;
                }

                StatusText = $"任务 {taskId} → {final.Status}";
                _ = LoadJournalAsync(taskId);
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"任务执行异常：{exception.Message}");
        }
    }

    private async Task LoadJournalAsync(string taskId)
    {
        try
        {
            var events = await _engine.ReadJournalAsync(taskId);
            var lines = events.Select(item => $"#{item.Sequence} [{item.Timestamp}] {item.Kind}{(item.Text is null ? "" : $"：{Truncate(item.Text, 120)}")}");
            _uiPost(() => JournalText = string.Join("\n", lines));
        }
        catch (Exception exception)
        {
            _uiPost(() => JournalText = $"日志读取失败：{exception.Message}");
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
