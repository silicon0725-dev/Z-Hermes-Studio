namespace ZHS.Protocol.Wire;

public static class StartupNotificationMethods
{
    /// <summary>app-server 启动即推送的存储状态通知（实测 sequence 递增，phase: checking→committing→ready）。</summary>
    public const string StorageState = "startup/storageState";
}

public sealed record StorageStateMigration(string? Kind, int? ExecutedCount, int? CommittedCount, string? LastAppliedMigrationId);

public sealed record StorageStateParams(
    int SchemaVersion,
    string AttemptId,
    long Sequence,
    string DatabaseId,
    string DatabaseKind,
    string Phase,
    StorageStateMigration? Migration,
    long? ElapsedMs);
