using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ZHS.Persistence;

/// <summary>一个已登记的数据库迁移。</summary>
/// <param name="Id">单调递增的迁移号（如 "0001_app_setting"）。</param>
/// <param name="Sql">幂等或首次执行的 DDL/DDL 脚本（多语句以 ; 分隔）。</param>
public sealed record SchemaMigration(string Id, string Sql);

/// <summary>
/// ZHS 自有 SQLite 库（ADR-0004：唯一写入者 ZHS Backend/Desktop；上游 ~/.zcode 库不经此处）。
/// 迁移纪律对齐上游 schema_migration：id + checksum + app_version，重复打开幂等，
/// checksum 不匹配立即失败（防止两套迁移脚本写同一个库）。
/// </summary>
public sealed class ZhsDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _appVersion;

    public const string CurrentSchemaVersion = "0004_artifacts";

    private static readonly SchemaMigration[] Migrations =
    [
        new("0001_app_setting", """
            CREATE TABLE IF NOT EXISTS app_setting (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                time_updated INTEGER NOT NULL
            );
            """),
        new("0002_settings_index", """
            CREATE INDEX IF NOT EXISTS idx_app_setting_time ON app_setting(time_updated);
            """),
        new("0004_artifacts", """
            CREATE TABLE IF NOT EXISTS project (
                project_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                workspace_path TEXT NOT NULL UNIQUE,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifact (
                artifact_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                type TEXT NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                current_version INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                source_refs TEXT NOT NULL DEFAULT '[]'
            );
            CREATE INDEX IF NOT EXISTS idx_artifact_project ON artifact(project_id, type);
            CREATE TABLE IF NOT EXISTS artifact_version (
                artifact_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                content TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                source_refs TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY (artifact_id, version)
            );
            """),
        new("0003_tasks", """
            CREATE TABLE IF NOT EXISTS task (
                task_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                workspace TEXT NOT NULL,
                status TEXT NOT NULL,
                session_id TEXT,
                output_text TEXT,
                error TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_event (
                task_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                text TEXT,
                PRIMARY KEY (task_id, sequence)
            );
            """),
    ];

    private ZhsDatabase(SqliteConnection connection, string appVersion)
    {
        _connection = connection;
        _appVersion = appVersion;
    }

    public static async Task<ZhsDatabase> OpenAsync(string path, string? appVersion = null, CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var database = new ZhsDatabase(connection, appVersion ?? ThisAssemblyVersion());
        await database.EnsureMigrationTableAsync(cancellationToken);
        await database.ApplyMigrationsAsync(cancellationToken);
        return database;
    }

    public SqliteConnection Connection => _connection;

    private static string ThisAssemblyVersion()
        => typeof(ZhsDatabase).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private async Task EnsureMigrationTableAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_migration (
                id TEXT PRIMARY KEY,
                checksum TEXT NOT NULL,
                app_version TEXT NOT NULL,
                time_applied INTEGER NOT NULL
            );
            """, cancellationToken);
    }

    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await foreach (var row in QueryAsync(
            "SELECT id, checksum FROM schema_migration",
            reader => (reader.GetString(0), reader.GetString(1)),
            cancellationToken))
        {
            var (appliedId, appliedChecksum) = row;
            var migration = Migrations.FirstOrDefault(candidate => candidate.Id == appliedId);
            if (migration is null || Checksum(migration) != appliedChecksum)
            {
                throw new InvalidOperationException(
                    $"schema_migration 校验失败：'{appliedId}' 在库内的 checksum 与当前脚本不一致（迁移脚本被改动或来源不明）。");
            }
        }

        foreach (var migration in Migrations)
        {
            var exists = await ScalarAsync(
                "SELECT COUNT(*) FROM schema_migration WHERE id = $id",
                command => command.Parameters.AddWithValue("$id", migration.Id),
                cancellationToken);
            if (Convert.ToInt64(exists) > 0)
            {
                continue;
            }

            await ExecuteAsync(migration.Sql, cancellationToken, transaction);
            await ExecuteAsync(
                "INSERT INTO schema_migration (id, checksum, app_version, time_applied) VALUES ($id, $checksum, $version, $time)",
                command =>
                {
                    command.Parameters.AddWithValue("$id", migration.Id);
                    command.Parameters.AddWithValue("$checksum", Checksum(migration));
                    command.Parameters.AddWithValue("$version", _appVersion);
                    command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                },
                cancellationToken,
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string Checksum(SchemaMigration migration)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(migration.Id + "\n" + migration.Sql));
        return Convert.ToHexString(hash);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await ExecuteAsync(sql, _ => { }, cancellationToken, transaction);
    }

    private async Task ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken, SqliteTransaction? transaction)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<object> ScalarAsync(string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        return await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("scalar query returned no value");
    }

    private async IAsyncEnumerable<T> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return map(reader);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
