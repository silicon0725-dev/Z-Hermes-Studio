namespace ZHS.Domain.Artifacts;

/// <summary>内存版仓储（测试与无持久化降级的参考实现）。</summary>
public sealed class InMemoryArtifactStore : IArtifactRepository
{
    private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Artifact> _artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ArtifactVersion>> _versions = new(StringComparer.Ordinal);

    public Task<Project> GetOrCreateProjectAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var found = _projects.Values.FirstOrDefault(project => project.WorkspacePath == workspacePath);
        if (found is not null)
        {
            return Task.FromResult(found);
        }

        var project = new Project($"proj_{_projects.Count + 1}", Path.GetFileName(workspacePath), workspacePath, 1);
        _projects[project.ProjectId] = project;
        return Task.FromResult(project);
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Project>>(_projects.Values.ToList());

    public Task<Artifact> CreateAsync(Artifact artifact, string content, CancellationToken cancellationToken = default)
    {
        _artifacts[artifact.ArtifactId] = artifact;
        _versions[artifact.ArtifactId] = [new ArtifactVersion(artifact.ArtifactId, 1, content, artifact.CreatedAt, artifact.SourceRefs)];
        return Task.FromResult(artifact);
    }

    public Task<Artifact> AppendVersionAsync(Artifact artifact, string content, CancellationToken cancellationToken = default)
    {
        var existing = _artifacts[artifact.ArtifactId];
        var next = existing.CurrentVersion + 1;
        var updated = existing with { CurrentVersion = next, UpdatedAt = next, SourceRefs = existing.SourceRefs.Concat(artifact.SourceRefs).Distinct().ToList() };
        _artifacts[artifact.ArtifactId] = updated;
        _versions[artifact.ArtifactId].Add(new ArtifactVersion(artifact.ArtifactId, next, content, next, artifact.SourceRefs));
        return Task.FromResult(updated);
    }

    public Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
        => Task.FromResult(_artifacts.GetValueOrDefault(artifactId));

    public Task<ArtifactVersion> ReadVersionAsync(string artifactId, int? version = null, CancellationToken cancellationToken = default)
    {
        var target = version ?? _artifacts[artifactId].CurrentVersion;
        return Task.FromResult(_versions[artifactId].Single(item => item.Version == target));
    }

    public Task<IReadOnlyList<ArtifactVersion>> ListVersionsAsync(string artifactId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ArtifactVersion>>(_versions[artifactId].OrderBy(item => item.Version).ToList());

    public Task<IReadOnlyList<Artifact>> ListByProjectAsync(string projectId, string? type = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Artifact>>(
            _artifacts.Values.Where(a => a.ProjectId == projectId && (type is null || a.Type == type)).ToList());

    public Task<IReadOnlyList<Artifact>> SearchAsync(string keyword, string? projectId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Artifact>>(
            _artifacts.Values.Where(a =>
                (projectId is null || a.ProjectId == projectId) &&
                (a.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                 || _versions[a.ArtifactId].Any(v => v.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))).ToList());
}
