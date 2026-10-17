using ZHS.Domain.Artifacts;
using ZHS.Presentation.Artifacts;
using ZHS.Presentation.Chat;
using Xunit;

namespace ZHS.Presentation.Tests;

public sealed class ArtifactsViewModelTests
{
    private static UiPost Inline => action => action();

    private static ArtifactEngine Engine() => new(new InMemoryArtifactStore());

    [Fact]
    public async Task Refresh_ListsProjectArtifacts()
    {
        var engine = Engine();
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "文档A", "内容");
        var viewModel = new ArtifactsViewModel(engine, Inline, () => "/tmp/ws");

        await viewModel.RefreshAsync();

        Assert.Single(viewModel.Items);
        Assert.Contains("文档A", viewModel.Items[0].Display);
    }

    [Fact]
    public async Task Search_FiltersByKeyword()
    {
        var engine = Engine();
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "架构决策", "正文一");
        await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "调研记录", "正文包含 Quantum 关键词");
        var viewModel = new ArtifactsViewModel(engine, Inline, () => "/tmp/ws") { SearchText = "Quantum" };

        await viewModel.SearchCommand.ExecuteAndWaitAsync();

        Assert.Single(viewModel.Items);
        Assert.Equal("调研记录", viewModel.Items[0].Title);
    }

    [Fact]
    public async Task Select_LoadsContentAndVersions()
    {
        var engine = Engine();
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var artifact = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "文档", "第一版");
        await engine.UpdateAsync(artifact.ArtifactId, "第二版");
        var viewModel = new ArtifactsViewModel(engine, Inline, () => "/tmp/ws");
        await viewModel.RefreshAsync();

        viewModel.SelectedArtifact = viewModel.Items[0];
        await Task.Yield();
        await Task.Delay(50); // LoadDetailAsync 异步完成

        Assert.Equal("第二版", viewModel.ContentText);
        Assert.Equal(2, viewModel.Versions.Count);
    }

    [Fact]
    public async Task RestoreSelectedVersion_AppendsNewVersion()
    {
        var engine = Engine();
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var artifact = await engine.SaveAsync(project.ProjectId, ArtifactTypes.Document, "文档", "原始版");
        await engine.UpdateAsync(artifact.ArtifactId, "改坏版");
        var viewModel = new ArtifactsViewModel(engine, Inline, () => "/tmp/ws");
        await viewModel.RefreshAsync();
        viewModel.SelectedArtifact = viewModel.Items[0];
        await Task.Delay(50);
        viewModel.SelectedVersion = viewModel.Versions[0]; // v1 原始版
        await Task.Delay(20);

        await viewModel.RestoreSelectedVersionCommand.ExecuteAndWaitAsync();
        await Task.Delay(50);

        Assert.Equal("原始版", viewModel.ContentText);
        Assert.Equal(3, viewModel.Versions.Count);
        Assert.Contains("恢复", viewModel.StatusText);
    }
}

public sealed class SaveAsArtifactTests
{
    private static UiPost Inline => action => action();

    [Fact]
    public async Task SaveLastReply_CreatesDocumentWithSessionRef()
    {
        var engine = new ArtifactEngine(new InMemoryArtifactStore());
        var session = new FakeChatSession("sess_1") { NextReply = "这是一份值得保存的回复。" };
        var viewModel = new ChatViewModel(session, Inline, engine, "/tmp/ws") { InputText = "请写点东西" };
        await viewModel.SendCommand.ExecuteAndWaitAsync();

        await viewModel.SaveArtifactCommand.ExecuteAndWaitAsync();

        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        var artifacts = await engine.ListByProjectAsync(project.ProjectId, ArtifactTypes.Document);
        var artifact = Assert.Single(artifacts);
        Assert.Equal("zcode:session:sess_1", Assert.Single(artifact.SourceRefs));
        Assert.Equal("这是一份值得保存的回复。", (await engine.ReadAsync(artifact.ArtifactId)).Content);
        Assert.Contains(viewModel.Messages, message => message.Text.Contains(artifact.ArtifactId));
    }

    [Fact]
    public async Task SaveLastReply_NoReply_ShowsHint()
    {
        var engine = new ArtifactEngine(new InMemoryArtifactStore());
        var viewModel = new ChatViewModel(new FakeChatSession("sess_1"), Inline, engine, "/tmp/ws");

        await viewModel.SaveArtifactCommand.ExecuteAndWaitAsync();

        Assert.Contains(viewModel.Messages, message => message.Text.Contains("没有可保存"));
        var project = await engine.GetOrCreateProjectAsync("/tmp/ws");
        Assert.Empty(await engine.ListByProjectAsync(project.ProjectId));
    }
}
