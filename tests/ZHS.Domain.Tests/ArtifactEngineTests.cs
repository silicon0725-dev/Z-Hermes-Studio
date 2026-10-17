using ZHS.Domain.Artifacts;
using Xunit;

namespace ZHS.Domain.Tests;

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

public sealed class ArtifactDifferTests
{
    [Fact]
    public void Diff_IdentifiesAddedAndRemovedLines()
    {
        var diff = ArtifactDiffer.Diff("第一行\n共同行\n旧的三行", "第一行\n共同行\n新的三行\n新增行");

        var added = diff.Where(line => line.Kind == DiffLineKind.Added).Select(line => line.Text).ToList();
        var removed = diff.Where(line => line.Kind == DiffLineKind.Removed).Select(line => line.Text).ToList();
        Assert.Equal(["旧的三行"], removed);
        Assert.Equal(["新的三行", "新增行"], added);
        Assert.Contains(diff, line => line.Kind == DiffLineKind.Unchanged && line.Text == "共同行");
    }

    [Fact]
    public void Diff_IdenticalContent_AllUnchanged()
    {
        var diff = ArtifactDiffer.Diff("a\nb", "a\nb");
        Assert.All(diff, line => Assert.Equal(DiffLineKind.Unchanged, line.Kind));
    }

    [Fact]
    public void Display_UsesDiffPrefixes()
    {
        var diff = ArtifactDiffer.Diff("x", "y");
        Assert.Contains("+ y", diff.Select(line => line.Display));
        Assert.Contains("- x", diff.Select(line => line.Display));
    }
}
