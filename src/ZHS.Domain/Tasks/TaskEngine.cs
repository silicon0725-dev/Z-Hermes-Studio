namespace ZHS.Domain.Tasks;

/// <summary>任务与事件的持久化边界（ZHS.Persistence 提供 SQLite 实现；测试用内存版）。</summary>
public interface ITaskRepository
{
    Task<string> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default);

    Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(string taskId, TaskStatus status, string? sessionId = null, string? outputText = null, string? error = null, CancellationToken cancellationToken = default);
}

public interface ITaskJournal
{
    Task AppendAsync(TaskEvent @event, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskEvent>> ReadAsync(string taskId, long afterSequence = 0, CancellationToken cancellationToken = default);
}

/// <summary>任务执行边界：M3 用单会话回合执行；M3+ 由 ZCAC 集群 Adapter 实现（ADR-0002 L2）。</summary>
public interface ITaskExecutor
{
    /// <summary>执行一个任务回合：返回输出文本；delta 经 progress 实时上报；取消抛 OperationCanceledException。</summary>
    Task<string> ExecuteAsync(TaskRecord task, IProgress<string>? deltaProgress, CancellationToken cancellationToken);
}

/// <summary>
/// Task Engine 最小版：创建 → 执行（journal-first 记录事件）→ 完成/失败/取消。
/// 执行中的取消：先请求执行器取消，再把任务置为 Cancelled（不等待执行器自然终止）。
/// </summary>
public sealed class TaskEngine
{
    private readonly ITaskRepository _repository;
    private readonly ITaskJournal _journal;
    private readonly ITaskExecutor _executor;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    public TaskEngine(ITaskRepository repository, ITaskJournal journal, ITaskExecutor executor, Func<long>? clock = null)
    {
        _repository = repository;
        _journal = journal;
        _executor = executor;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<TaskRecord> CreateAsync(string title, string prompt, string workspace, CancellationToken cancellationToken = default)
    {
        var record = new TaskRecord(
            $"task_{Guid.NewGuid().ToString("N")[..12]}",
            title,
            prompt,
            workspace,
            TaskStatus.Pending,
            _clock(),
            _clock());
        await _repository.CreateAsync(record, cancellationToken);
        await _journal.AppendAsync(new TaskEvent(1, record.TaskId, TaskEventKind.Created, _clock(), title), cancellationToken);
        return record;
    }

    /// <summary>执行任务到终态。已在运行中的任务抛 InvalidOperationException。</summary>
    public async Task<TaskRecord> RunAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var task = await RequireAsync(taskId, cancellationToken);
        var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[taskId] = execution;

        try
        {
            await TransitionAsync(task, TaskStatus.Running);
            await _journal.AppendAsync(new TaskEvent(NextSequence(taskId), taskId, TaskEventKind.Started, _clock()));
            task = task with { Status = TaskStatus.Running };

            var output = await _executor.ExecuteAsync(task, null, execution.Token);
            var completed = await TransitionAsync(task, TaskStatus.Succeeded, outputText: output);
            await _journal.AppendAsync(new TaskEvent(NextSequence(taskId), taskId, TaskEventKind.Completed, _clock(), output));
            return completed;
        }
        catch (OperationCanceledException)
        {
            var cancelled = await TransitionAsync(task, TaskStatus.Cancelled, error: "cancelled");
            await _journal.AppendAsync(new TaskEvent(NextSequence(taskId), taskId, TaskEventKind.Cancelled, _clock()));
            return cancelled;
        }
        catch (Exception exception)
        {
            var failed = await TransitionAsync(task, TaskStatus.Failed, error: exception.Message);
            await _journal.AppendAsync(new TaskEvent(NextSequence(taskId), taskId, TaskEventKind.Failed, _clock(), exception.Message));
            return failed;
        }
        finally
        {
            _running.Remove(taskId);
            execution.Dispose();
        }
    }

    /// <summary>请求取消运行中的任务（不等待其终结；终结由 RunAsync 的调用方观察）。</summary>
    public bool TryCancel(string taskId)
    {
        if (_running.TryGetValue(taskId, out var execution))
        {
            execution.Cancel();
            return true;
        }

        return false;
    }

    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
        => _repository.GetAsync(taskId, cancellationToken);

    public Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken cancellationToken = default)
        => _repository.ListAsync(cancellationToken);

    public Task<IReadOnlyList<TaskEvent>> ReadJournalAsync(string taskId, long afterSequence = 0, CancellationToken cancellationToken = default)
        => _journal.ReadAsync(taskId, afterSequence, cancellationToken);

    private async Task<TaskRecord> RequireAsync(string taskId, CancellationToken cancellationToken)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException($"任务不存在：{taskId}");
        TaskStateMachine.AssertTransition(taskId, record.Status, TaskStatus.Running);
        return record;
    }

    private async Task<TaskRecord> TransitionAsync(
        TaskRecord task,
        TaskStatus to,
        string? sessionId = null,
        string? outputText = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        TaskStateMachine.AssertTransition(task.TaskId, task.Status, to);
        await _repository.UpdateStatusAsync(task.TaskId, to, sessionId, outputText, error, cancellationToken);
        return task with { Status = to, OutputText = outputText ?? task.OutputText, Error = error };
    }

    private long NextSequence(string taskId)
    {
        // 事件序号任务内单调；引擎内单任务串行执行，取当前日志最大序号 + 1
        var events = _journal.ReadAsync(taskId).GetAwaiter().GetResult();
        return (events.Count == 0 ? 0 : events.Max(item => item.Sequence)) + 1;
    }
}
