namespace ZHS.Domain.Tasks;

/// <summary>
/// ZHS 任务状态机。迁移表采纳 ZCAC 实测设计（docs/M0-SOURCE-INVENTORY.md §3.4），
/// 非法迁移抛错——状态变化必须走 <see cref="TaskStateMachine"/>。
/// </summary>
public enum TaskStatus
{
    Pending,
    Ready,
    Running,
    Blocked,
    Succeeded,
    Failed,
    RetryWait,
    Cancelled,
    Interrupted,
}

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<TaskStatus, TaskStatus[]> Transitions = new Dictionary<TaskStatus, TaskStatus[]>
    {
        [TaskStatus.Pending] = [TaskStatus.Ready, TaskStatus.Blocked, TaskStatus.Cancelled, TaskStatus.Running],
        [TaskStatus.Ready] = [TaskStatus.Running, TaskStatus.Blocked, TaskStatus.Cancelled, TaskStatus.Pending],
        [TaskStatus.Running] = [TaskStatus.Succeeded, TaskStatus.Failed, TaskStatus.RetryWait, TaskStatus.Cancelled, TaskStatus.Interrupted],
        [TaskStatus.Blocked] = [TaskStatus.Ready, TaskStatus.Cancelled, TaskStatus.Pending],
        [TaskStatus.RetryWait] = [TaskStatus.Ready, TaskStatus.Failed, TaskStatus.Cancelled],
        [TaskStatus.Interrupted] = [TaskStatus.Ready, TaskStatus.Failed, TaskStatus.Cancelled],
        [TaskStatus.Succeeded] = [],
        [TaskStatus.Failed] = [TaskStatus.RetryWait],
        [TaskStatus.Cancelled] = [],
    };

    public static bool CanTransition(TaskStatus from, TaskStatus to)
        => Transitions[from].Contains(to);

    public static void AssertTransition(string taskId, TaskStatus from, TaskStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"任务 {taskId} 的非法状态迁移：{from} → {to}");
        }
    }
}

/// <summary>任务记录。产物引用（M4）预留 OutputText 与 session 关联。</summary>
public sealed record TaskRecord(
    string TaskId,
    string Title,
    string Prompt,
    string Workspace,
    TaskStatus Status,
    long CreatedAt,
    long UpdatedAt,
    string? SessionId = null,
    string? OutputText = null,
    string? Error = null);

public enum TaskEventKind
{
    Created,
    Started,
    Delta,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>任务事件（journal-first：状态写入与事件追加同事务提交，序号任务内单调）。</summary>
public sealed record TaskEvent(long Sequence, string TaskId, TaskEventKind Kind, long Timestamp, string? Text = null);
