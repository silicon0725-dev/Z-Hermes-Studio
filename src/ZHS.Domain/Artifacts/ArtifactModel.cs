namespace ZHS.Domain.Artifacts;

/// <summary>ZHS 项目（工作空间的组织单元）。按工作目录自动 get-or-create。</summary>
public sealed record Project(string ProjectId, string Name, string WorkspacePath, long CreatedAt);

/// <summary>产物类型（开发文档 §10.2；新类型以字符串扩展，不锁枚举）。</summary>
public static class ArtifactTypes
{
    public const string Document = "document";
    public const string CodeChange = "code-change";
    public const string Research = "research";
    public const string NarrativeEntity = "narrative-entity";
    public const string DesignSpec = "design-spec";
    public const string WorkflowRun = "workflow-run";
}

public static class ArtifactStatus
{
    public const string Draft = "draft";
    public const string Final = "final";
    public const string Deprecated = "deprecated";
}

/// <summary>产物元数据。内容在版本表中（contentRef 供大内容外置，M4 首片内联）。</summary>
public sealed record Artifact(
    string ArtifactId,
    string ProjectId,
    string Type,
    string Title,
    string Status,
    int CurrentVersion,
    long CreatedAt,
    long UpdatedAt,
    IReadOnlyList<string> SourceRefs);

/// <summary>产物的一个版本快照。</summary>
public sealed record ArtifactVersion(
    string ArtifactId,
    int Version,
    string Content,
    long CreatedAt,
    IReadOnlyList<string> SourceRefs);

/// <summary>产物与版本的持久化边界（SQLite 实现 + 测试内存版）。</summary>
public interface IArtifactRepository
{
    Task<Project> GetOrCreateProjectAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>新建产物（版本 1）。</summary>
    Task<Artifact> CreateAsync(Artifact artifact, string content, CancellationToken cancellationToken = default);

    /// <summary>追加新版本并推进 CurrentVersion。</summary>
    Task<Artifact> AppendVersionAsync(Artifact artifact, string content, CancellationToken cancellationToken = default);

    Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default);

    Task<ArtifactVersion> ReadVersionAsync(string artifactId, int? version = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactVersion>> ListVersionsAsync(string artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListByProjectAsync(string projectId, string? type = null, CancellationToken cancellationToken = default);

    /// <summary>关键词检索（标题 + 内容；FTS5 属 Memory Engine 阶段，此为 LIKE 基线）。</summary>
    Task<IReadOnlyList<Artifact>> SearchAsync(string keyword, string? projectId = null, CancellationToken cancellationToken = default);
}
