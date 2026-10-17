using Microsoft.Data.Sqlite;
using ZHS.Domain.Tasks;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.Persistence;

/// <summary>task / task_event 表的仓储实现（journal-first：UpdateStatus 与 Append 由调用方按序提交，表内约束保证序号唯一）。</summary>
public sealed class TaskSqliteStore : ITaskRepository, ITaskJournal
{
    private static readonly Dictionary<TaskStatus, string> StatusNames = new()
    {
        [TaskStatus.Pending] = "pending",
        [TaskStatus.Ready] = "ready",
        [TaskStatus.Running] = "running",
        [TaskStatus.Blocked] = "blocked",
        [TaskStatus.Succeeded] = "succeeded",
        [TaskStatus.Failed] = "failed",
        [TaskStatus.RetryWait] = "retry_wait",
        [TaskStatus.Cancelled] = "cancelled",
        [TaskStatus.Interrupted] = "interrupted",
    };

    private static readonly Dictionary<string, TaskStatus> StatusesByName =
        StatusNames.ToDictionary(pair => pair.Value, pair => pair.Key);

    private readonly ZhsDatabase _database;

    public TaskSqliteStore(ZhsDatabase database)
    {
        _database = database;
    }

    public async Task<string> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        await Execute(
            "INSERT INTO task (task_id, title, prompt, workspace, status, session_id, output_text, error, created_at, updated_at) VALUES ($id, $title, $prompt, $workspace, $status, NULL, NULL, NULL, $created, $updated)",
            command =>
            {
                command.Parameters.AddWithValue("$id", record.TaskId);
                command.Parameters.AddWithValue("$title", record.Title);
                command.Parameters.AddWithValue("$prompt", record.Prompt);
                command.Parameters.AddWithValue("$workspace", record.Workspace);
                command.Parameters.AddWithValue("$status", StatusNames[record.Status]);
                command.Parameters.AddWithValue("$created", record.CreatedAt);
                command.Parameters.AddWithValue("$updated", record.UpdatedAt);
            },
            cancellationToken);
        return record.TaskId;
    }

    public async Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return await QueryOne(
            "SELECT task_id, title, prompt, workspace, status, session_id, output_text, error, created_at, updated_at FROM task WHERE task_id = $id",
            command => command.Parameters.AddWithValue("$id", taskId),
            Map,
            cancellationToken);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<TaskRecord>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT task_id, title, prompt, workspace, status, session_id, output_text, error, created_at, updated_at FROM task ORDER BY created_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(Map(reader));
        }

        return records;
    }

    public async Task UpdateStatusAsync(string taskId, TaskStatus status, string? sessionId = null, string? outputText = null, string? error = null, CancellationToken cancellationToken = default)
    {
        await Execute(
            "UPDATE task SET status = $status, session_id = COALESCE($session, session_id), output_text = COALESCE($output, output_text), error = $error, updated_at = $updated WHERE task_id = $id",
            command =>
            {
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$status", StatusNames[status]);
                command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
                command.Parameters.AddWithValue("$output", (object?)outputText ?? DBNull.Value);
                command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            },
            cancellationToken);
    }

    public async Task AppendAsync(TaskEvent @event, CancellationToken cancellationToken = default)
    {
        await Execute(
            "INSERT INTO task_event (task_id, sequence, kind, timestamp, text) VALUES ($task, $seq, $kind, $time, $text)",
            command =>
            {
                command.Parameters.AddWithValue("$task", @event.TaskId);
                command.Parameters.AddWithValue("$seq", @event.Sequence);
                command.Parameters.AddWithValue("$kind", @event.Kind.ToString().ToLowerInvariant());
                command.Parameters.AddWithValue("$time", @event.Timestamp);
                command.Parameters.AddWithValue("$text", (object?)@event.Text ?? DBNull.Value);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TaskEvent>> ReadAsync(string taskId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        var events = new List<TaskEvent>();
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT sequence, kind, timestamp, text FROM task_event WHERE task_id = $task AND sequence > $after ORDER BY sequence";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$after", afterSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new TaskEvent(
                reader.GetInt64(0),
                taskId,
                Enum.Parse<TaskEventKind>(reader.GetString(1), ignoreCase: true),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
    }

    private static TaskRecord Map(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            StatusesByName[reader.GetString(4)],
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private async Task Execute(string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<T?> QueryOne<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }

        return map(reader);
    }
}
