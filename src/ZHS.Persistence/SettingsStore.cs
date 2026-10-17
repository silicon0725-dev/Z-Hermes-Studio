using Microsoft.Data.Sqlite;
using ZHS.Presentation;

namespace ZHS.Persistence;

/// <summary>app_setting 表的 KV 存取（IAppSettings 的持久化实现）。</summary>
public sealed class SettingsStore : IAppSettings
{
    private readonly ZhsDatabase _database;

    public SettingsStore(ZhsDatabase database)
    {
        _database = database;
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task SetAsync(string key, string value)
    {
        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_setting (key, value, time_updated) VALUES ($key, $value, $time)
            ON CONFLICT(key) DO UPDATE SET value = $value, time_updated = $time
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
    }
}
