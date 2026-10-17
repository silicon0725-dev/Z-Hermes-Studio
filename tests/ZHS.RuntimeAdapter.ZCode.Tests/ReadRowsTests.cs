using System.Text.Json;
using ZHS.Protocol.Runtime;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>v4 rowsRange 反向分页契约测试。</summary>
public sealed class ReadRowsTests
{
    private const string Workspace = "/tmp/zhs-workspace";

    [Fact]
    public async Task ReadRows_SendsV4Params_AndParsesAscendingRows()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));

        connection.EnqueueFor("v4/conversation/rowsRange", new
        {
            rows = new object[]
            {
                new { rowId = 1, kind = "userInput", text = "你好" },
                new { rowId = 2, kind = "assistantText", text = "好的", state = "complete", model = "deepseek-flash" },
                new { rowId = 3, kind = "toolCall", state = "complete" },
            },
            atSeq = 12,
            atRevision = 3,
            atLogEpoch = "epoch-1",
            hasMore = true,
        });

        var page = await runtime.ReadRowsAsync(handle.SessionId, beforeRowId: 7, limit: 60);

        Assert.NotNull(page.Rows);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(1, page.Rows[0].RowId);
        Assert.Equal("userInput", page.Rows[0].Kind);
        Assert.Equal("好的", page.Rows[1].Text);
        Assert.Equal("complete", page.Rows[1].State);
        Assert.True(page.HasMore);
        Assert.Equal("epoch-1", page.AtLogEpoch);

        var request = connection.Requests.Single(r => r.Method == "v4/conversation/rowsRange");
        using var parameters = JsonDocument.Parse(request.ParamsJson);
        var root = parameters.RootElement;
        Assert.Equal("sess_1", root.GetProperty("sessionId").GetString());
        Assert.Equal(7, root.GetProperty("beforeRowId").GetInt64());
        Assert.Equal(60, root.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ReadRows_TailPage_OmitsBeforeRowId()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));
        connection.EnqueueFor("v4/conversation/rowsRange", new { rows = Array.Empty<object>(), atSeq = 0, atRevision = 0, atLogEpoch = "e", hasMore = false });

        await runtime.ReadRowsAsync(handle.SessionId);

        var request = connection.Requests.Single(r => r.Method == "v4/conversation/rowsRange");
        Assert.DoesNotContain("beforeRowId", request.ParamsJson);
    }
}

public sealed class ResumeSessionTests
{
    [Fact]
    public async Task Resume_SendsMinimalParams_AndResolvesSnapshotSessionId()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_existing"},"protocol":{"name":"ZCode Protocol","version":1}}""");

        var handle = await runtime.ResumeSessionAsync("sess_existing");

        Assert.Equal("sess_existing", handle.SessionId);
        var request = Assert.Single(connection.Requests);
        Assert.Equal("session/resume", request.Method);
        Assert.Equal("""{"sessionId":"sess_existing"}""", request.ParamsJson);
    }

    [Fact]
    public async Task Resume_FallsBackToRequestedId_WhenSnapshotOmitsIt()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"messages":[]}""");

        var handle = await runtime.ResumeSessionAsync("sess_kept");

        Assert.Equal("sess_kept", handle.SessionId);
    }
}
