namespace ZHS.Domain.Artifacts;

/// <summary>
/// Artifact Engine（M4 首片）：项目 get-or-create、产物保存（自动版本化）、
/// 版本读取与恢复、关键词检索。产物是工作成果的持久形态，独立于聊天文本存在。
/// </summary>
public sealed class ArtifactEngine
{
    private readonly IArtifactRepository _repository;
    private readonly Func<long> _clock;

    public ArtifactEngine(IArtifactRepository repository, Func<long>? clock = null)
    {
        _repository = repository;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public Task<Project> GetOrCreateProjectAsync(string workspacePath, CancellationToken cancellationToken = default)
        => _repository.GetOrCreateProjectAsync(workspacePath, cancellationToken);

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
        => _repository.ListProjectsAsync(cancellationToken);

    public Task<Artifact> SaveAsync(
        string projectId,
        string type,
        string title,
        string content,
        IReadOnlyList<string>? sourceRefs = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = new Artifact(
            $"art_{Guid.NewGuid().ToString("N")[..12]}",
            projectId,
            type,
            title,
            ArtifactStatus.Draft,
            CurrentVersion: 1,
            CreatedAt: _clock(),
            UpdatedAt: _clock(),
            sourceRefs ?? []);
        return _repository.CreateAsync(artifact, content, cancellationToken);
    }

    public Task<Artifact> UpdateAsync(
        string artifactId,
        string content,
        IReadOnlyList<string>? additionalSourceRefs = null,
        CancellationToken cancellationToken = default)
        => _repository.AppendVersionAsync(new Artifact(artifactId, "", "", "", "", 0, 0, 0, additionalSourceRefs ?? []), content, cancellationToken);

    public Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
        => _repository.GetAsync(artifactId, cancellationToken);

    /// <summary>读取指定版本内容；version=null 读当前版。</summary>
    public Task<ArtifactVersion> ReadAsync(string artifactId, int? version = null, CancellationToken cancellationToken = default)
        => _repository.ReadVersionAsync(artifactId, version, cancellationToken);

    /// <summary>恢复旧版本 = 以旧内容追加新版本（历史不可变，恢复留痕）。</summary>
    public async Task<Artifact> RestoreAsync(string artifactId, int version, CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.ReadVersionAsync(artifactId, version, cancellationToken);
        return await _repository.AppendVersionAsync(
            new Artifact(artifactId, "", "", "", "", 0, 0, 0, [$"restored-from:v{version}"]),
            snapshot.Content,
            cancellationToken);
    }

    public Task<IReadOnlyList<ArtifactVersion>> ListVersionsAsync(string artifactId, CancellationToken cancellationToken = default)
        => _repository.ListVersionsAsync(artifactId, cancellationToken);

    public Task<IReadOnlyList<Artifact>> ListByProjectAsync(string projectId, string? type = null, CancellationToken cancellationToken = default)
        => _repository.ListByProjectAsync(projectId, type, cancellationToken);

    public Task<IReadOnlyList<Artifact>> SearchAsync(string keyword, string? projectId = null, CancellationToken cancellationToken = default)
        => _repository.SearchAsync(keyword, projectId, cancellationToken);

    /// <summary>两个版本的行级差异（M4 基本差异查看；fromVersion=null 表示当前版的前一版语义由调用方决定）。</summary>
    public async Task<IReadOnlyList<DiffLine>> DiffAsync(string artifactId, int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        var left = await _repository.ReadVersionAsync(artifactId, fromVersion, cancellationToken);
        var right = await _repository.ReadVersionAsync(artifactId, toVersion, cancellationToken);
        return ArtifactDiffer.Diff(left.Content, right.Content);
    }
}
