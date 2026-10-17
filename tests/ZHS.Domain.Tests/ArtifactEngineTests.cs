using ZHS.Domain.Artifacts;
using Xunit;

namespace ZHS.Domain.Tests;

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

public sealed class ArtifactEngineTests
{
    private static ArtifactEngine Create(InMemoryArtifactStore store) => new(store);

    [Fact]
    public async Task Save_CreatesVersionOne_WithSourceRefs()
    {
        var engine = Create(new InMemoryArtifactStore());
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");

        var artifact = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "架构笔记", "第一版内容", ["zcode:sess_1"]);

        Assert.Equal(1, artifact.CurrentVersion);
        var content = await engine.ReadAsync(artifact.ArtifactId);
        Assert.Equal("第一版内容", content.Content);
        Assert.Equal(["zcode:sess_1"], artifact.SourceRefs);
    }

    [Fact]
    public async Task Update_AppendsVersion_AndMergesSourceRefs()
    {
        var engine = Create(new InMemoryArtifactStore());
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var artifact = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "标题", "v1", ["src:a"]);

        var updated = await engine.UpdateAsync(artifact.ArtifactId, "v2 内容", ["src:b"]);

        Assert.Equal(2, updated.CurrentVersion);
        Assert.Equal(["src:a", "src:b"], updated.SourceRefs);
        Assert.Equal("v2 内容", (await engine.ReadAsync(artifact.ArtifactId)).Content);
        Assert.Equal("v1", (await engine.ReadAsync(artifact.ArtifactId, version: 1)).Content);
        Assert.Equal(2, (await engine.ListVersionsAsync(artifact.ArtifactId)).Count);
    }

    [Fact]
    public async Task Restore_OldVersion_AppendsAsNewVersion_WithTrace()
    {
        var engine = Create(new InMemoryArtifactStore());
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var artifact = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "标题", "原始内容");
        await engine.UpdateAsync(artifact.ArtifactId, "改坏了的内容");

        var restored = await engine.RestoreAsync(artifact.ArtifactId, 1);

        Assert.Equal(3, restored.CurrentVersion);
        Assert.Equal("原始内容", (await engine.ReadAsync(artifact.ArtifactId)).Content);
        Assert.Contains("restored-from:v1", restored.SourceRefs); // 恢复留痕（历史不可变）
    }

    [Fact]
    public async Task Search_MatchesTitleAndContent()
    {
        var engine = Create(new InMemoryArtifactStore());
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        await engine.SaveAsync(project.ProjectId, ArtifactTypes.Research, "调研：分词方案", "正文不含关键词");
        var target = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Research, "另一个标题", "正文包含 trigram 方案说明");

        var byContent = await engine.SearchAsync("trigram");
        var byTitle = await engine.SearchAsync("分词");

        Assert.Equal(target.ArtifactId, Assert.Single(byContent).ArtifactId);
        Assert.Single(byTitle);
        Assert.NotEqual(target.ArtifactId, byTitle[0].ArtifactId);
    }

    [Fact]
    public async Task GetOrCreateProject_IsIdempotentByWorkspace()
    {
        var engine = Create(new InMemoryArtifactStore());
        var first = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var second = await engine.GetOrCreateProjectAsync("/tmp/ws");

        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Single(await engine.ListProjectsAsync());
    }
}
