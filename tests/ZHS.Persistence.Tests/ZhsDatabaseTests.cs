using ZHS.Domain.Tasks;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;
using ZHS.Persistence;
using Xunit;

namespace ZHS.Persistence.Tests;

public sealed class ZhsDatabaseTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "zhs-test-" + Guid.NewGuid().ToString("N") + ".sqlite");

    [Fact]
    public async Task Open_MigratesAndIsIdempotent()
    {
        await using (var database = await ZhsDatabase.OpenAsync(_path, "0.1.0"))
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_migration";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.True(count >= 2, $"至少应用 0001/0002 两个迁移，实际 {count}");
        }

        // 二次打开：幂等，不重复应用、不报错
        await using (var database = await ZhsDatabase.OpenAsync(_path, "0.1.0"))
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_migration";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.True(count >= 2);
        }
    }

    [Fact]
    public async Task Open_TamperedMigrationChecksum_FailsFast()
    {
        await using (var database = await ZhsDatabase.OpenAsync(_path, "0.1.0"))
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "UPDATE schema_migration SET checksum = 'deadbeef' WHERE id = '0001_app_setting'";
            await command.ExecuteNonQueryAsync();
        }

        // checksum 与脚本不一致：立即失败（防止别处写入同库）
        await Assert.ThrowsAsync<InvalidOperationException>(() => ZhsDatabase.OpenAsync(_path, "0.1.0"));
    }

    [Fact]
    public async Task SettingsStore_RoundTripsAndOverwrites()
    {
        await using var database = await ZhsDatabase.OpenAsync(_path, "0.1.0");
        var store = new SettingsStore(database);

        Assert.Null(await store.GetAsync("workspace.path"));

        await store.SetAsync("workspace.path", "/tmp/a");
        await store.SetAsync("workspace.path", "/tmp/b");

        Assert.Equal("/tmp/b", await store.GetAsync("workspace.path"));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception)
        {
            // 临时文件清理尽力而为
        }

        await ValueTask.CompletedTask;
    }
}

public sealed class TaskStoreTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "zhs-task-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly ZhsDatabase _database;

    public TaskStoreTests()
    {
        _database = ZhsDatabase.OpenAsync(_path, "0.1.0").GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Task_RoundTrip_AndJournalSequenceUnique()
    {
        var store = new TaskSqliteStore(_database);
        var record = new TaskRecord("task_abc", "标题", "提示", "/tmp/ws", TaskStatus.Pending, 1, 1);

        await store.CreateAsync(record);
        await store.UpdateStatusAsync("task_abc", TaskStatus.Running);
        await store.UpdateStatusAsync("task_abc", TaskStatus.Succeeded, outputText: "结果文本");
        await store.AppendAsync(new TaskEvent(1, "task_abc", TaskEventKind.Created, 10, "标题"));
        await store.AppendAsync(new TaskEvent(2, "task_abc", TaskEventKind.Completed, 20, "结果文本"));

        var loaded = await store.GetAsync("task_abc");
        Assert.NotNull(loaded);
        Assert.Equal(TaskStatus.Succeeded, loaded!.Status);
        Assert.Equal("结果文本", loaded.OutputText);

        var journal = await store.ReadAsync("task_abc");
        Assert.Equal([1, 2], journal.Select(item => item.Sequence).ToArray());

        // 序号唯一由主键保证：重复追加必须失败
        await Assert.ThrowsAnyAsync<Exception>(
            () => store.AppendAsync(new TaskEvent(2, "task_abc", TaskEventKind.Completed, 20, "dup")));
    }

    [Fact]
    public async Task List_OrdersByNewestFirst()
    {
        var store = new TaskSqliteStore(_database);
        await store.CreateAsync(new TaskRecord("task_old", "旧", "p", "/w", TaskStatus.Pending, 1, 1));
        await store.CreateAsync(new TaskRecord("task_new", "新", "p", "/w", TaskStatus.Pending, 2, 2));

        var list = await store.ListAsync();

        Assert.Equal("task_new", list[0].TaskId);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        try
        {
            File.Delete(_path);
        }
        catch (Exception)
        {
        }
    }
}
