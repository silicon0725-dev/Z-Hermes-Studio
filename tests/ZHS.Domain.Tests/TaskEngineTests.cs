using ZHS.Domain.Tasks;
using Xunit;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.Domain.Tests;

public sealed class TaskStateMachineTests
{
    [Theory]
    [InlineData(TaskStatus.Pending, TaskStatus.Running, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Succeeded, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Failed, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Interrupted, true)]
    [InlineData(TaskStatus.Succeeded, TaskStatus.Running, false)]
    [InlineData(TaskStatus.Cancelled, TaskStatus.Running, false)]
    [InlineData(TaskStatus.Pending, TaskStatus.Succeeded, false)]
    public void Transitions_MatchZcacDesign(TaskStatus from, TaskStatus to, bool allowed)
    {
        Assert.Equal(allowed, TaskStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void IllegalTransition_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => TaskStateMachine.AssertTransition("task_1", TaskStatus.Succeeded, TaskStatus.Running));
    }
}

public sealed class InMemoryTaskStore : ITaskRepository, ITaskJournal
{
    private readonly Dictionary<string, TaskRecord> _records = new();
    private readonly Dictionary<string, List<TaskEvent>> _events = new();

    public List<string> ExecutionLog { get; } = [];

    public Task<string> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.TaskId] = record;
        return Task.FromResult(record.TaskId);
    }

    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.GetValueOrDefault(taskId));

    public Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TaskRecord>>(_records.Values.ToList());

    public Task UpdateStatusAsync(string taskId, TaskStatus status, string? sessionId = null, string? outputText = null, string? error = null, CancellationToken cancellationToken = default)
    {
        var record = _records[taskId];
        _records[taskId] = record with { Status = status, SessionId = sessionId ?? record.SessionId, OutputText = outputText ?? record.OutputText, Error = error, UpdatedAt = record.UpdatedAt + 1 };
        return Task.CompletedTask;
    }

    public Task AppendAsync(TaskEvent @event, CancellationToken cancellationToken = default)
    {
        if (!_events.TryGetValue(@event.TaskId, out var list))
        {
            list = [];
            _events[@event.TaskId] = list;
        }

        if (list.Any(existing => existing.Sequence == @event.Sequence))
        {
            throw new InvalidOperationException("duplicate event sequence");
        }

        list.Add(@event);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskEvent>> ReadAsync(string taskId, long afterSequence = 0, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TaskEvent>>(
            _events.TryGetValue(taskId, out var list)
                ? list.Where(item => item.Sequence > afterSequence).OrderBy(item => item.Sequence).ToList()
                : []);
}

public sealed class ScriptedExecutor : ITaskExecutor
{
    public Func<TaskRecord, CancellationToken, Task<string>> Behavior { get; set; } =
        static (_, _) => Task.FromResult("done");

    public Task<string> ExecuteAsync(TaskRecord task, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
        => Behavior(task, cancellationToken);
}

public sealed class TaskEngineTests
{
    private static TaskEngine Create(InMemoryTaskStore store, ScriptedExecutor executor)
        => new(store, store, executor);

    [Fact]
    public async Task Create_JournalsCreatedEvent()
    {
        var store = new InMemoryTaskStore();
        var engine = Create(store, new ScriptedExecutor());

        var task = await engine.CreateAsync("问候", "请回复好的", "/tmp/ws");

        Assert.Equal(TaskStatus.Pending, task.Status);
        var journal = await engine.ReadJournalAsync(task.TaskId);
        var created = Assert.Single(journal);
        Assert.Equal(TaskEventKind.Created, created.Kind);
        Assert.Equal(1, created.Sequence);
    }

    [Fact]
    public async Task Run_CompletesWithOutputAndJournal()
    {
        var store = new InMemoryTaskStore();
        var executor = new ScriptedExecutor
        {
            Behavior = (task, _) => Task.FromResult($"输出：{task.Title}"),
        };
        var engine = Create(store, executor);

        var task = await engine.CreateAsync("翻译", "翻译一句话", "/tmp/ws");
        var final = await engine.RunAsync(task.TaskId);

        Assert.Equal(TaskStatus.Succeeded, final.Status);
        Assert.Equal("输出：翻译", final.OutputText);
        var journal = await engine.ReadJournalAsync(task.TaskId);
        Assert.Equal(3, journal.Count); // created → started → completed
        Assert.Equal([1, 2, 3], journal.Select(item => item.Sequence).ToArray());
        Assert.Equal(TaskEventKind.Completed, journal[^1].Kind);
        Assert.Equal("输出：翻译", journal[^1].Text);
    }

    [Fact]
    public async Task Run_ExecutorThrows_MarksFailedWithJournal()
    {
        var store = new InMemoryTaskStore();
        var executor = new ScriptedExecutor
        {
            Behavior = (_, _) => throw new InvalidOperationException("model error"),
        };
        var engine = Create(store, executor);

        var task = await engine.CreateAsync("会失败", "boom", "/tmp/ws");
        var final = await engine.RunAsync(task.TaskId);

        Assert.Equal(TaskStatus.Failed, final.Status);
        Assert.Equal("model error", final.Error);
        var journal = await engine.ReadJournalAsync(task.TaskId);
        Assert.Equal(TaskEventKind.Failed, journal[^1].Kind);
    }

    [Fact]
    public async Task Run_TerminalTask_Rejected()
    {
        var store = new InMemoryTaskStore();
        var engine = Create(store, new ScriptedExecutor());
        var task = await engine.CreateAsync("一次", "prompt", "/tmp/ws");
        await engine.RunAsync(task.TaskId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(task.TaskId));
    }

    [Fact]
    public async Task TryCancel_DuringExecution_MarksCancelled()
    {
        var store = new InMemoryTaskStore();
        var executor = new ScriptedExecutor
        {
            Behavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            },
        };
        var engine = Create(store, executor);

        var task = await engine.CreateAsync("长任务", "慢慢做", "/tmp/ws");
        var running = engine.RunAsync(task.TaskId);
        await Task.Delay(100);
        Assert.True(engine.TryCancel(task.TaskId));

        var final = await running;
        Assert.Equal(TaskStatus.Cancelled, final.Status);
        var journal = await engine.ReadJournalAsync(task.TaskId);
        Assert.Equal(TaskEventKind.Cancelled, journal[^1].Kind);
    }

    [Fact]
    public async Task JournalRead_WithCursor_ReturnsOnlyNewer()
    {
        var store = new InMemoryTaskStore();
        var engine = Create(store, new ScriptedExecutor());
        var task = await engine.CreateAsync("t", "p", "/w");
        await engine.RunAsync(task.TaskId);

        var tail = await engine.ReadJournalAsync(task.TaskId, afterSequence: 1);

        Assert.Equal(2, tail.Count);
        Assert.All(tail, item => Assert.True(item.Sequence > 1));
    }
}
