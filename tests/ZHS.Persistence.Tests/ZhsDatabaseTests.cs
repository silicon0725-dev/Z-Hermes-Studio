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
