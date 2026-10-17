using System.Text.Json;
using ZHS.Protocol.Runtime;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>session/messages 历史读取契约测试（游标 afterMessageId，无 OFFSET）。</summary>
public sealed class ReadMessagesTests
{
    private const string Workspace = "/tmp/zhs-workspace";

    [Fact]
    public async Task ReadMessages_SendsCursorParams_AndExtractsText()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));

        connection.EnqueueFor("session/messages", new
        {
            messages = new object[]
            {
                new { info = new { id = "m1", role = "user" }, parts = new object[] { new { type = "text", text = "请回复好的" } } },
                new
                {
                    info = new { id = "m2", role = "assistant", model = "deepseek-flash" },
                    parts = new object[]
                    {
                        new { type = "text", text = "好的" },
                        new { type = "tool", name = "Bash" },
                        new { type = "text", text = "。" },
                    },
                },
            },
        });

        var messages = await runtime.ReadMessagesAsync(handle, afterMessageId: "m0", limit: 40);

        Assert.Equal(2, messages.Count);
        Assert.Equal("请回复好的", messages[0].ExtractText());
        // 文本提取跳过工具分片，拼接其余 text 分片
        Assert.Equal("好的。", messages[1].ExtractText());
        Assert.Equal("assistant", messages[1].Info.Role);

        var request = connection.Requests.Single(r => r.Method == "session/messages");
        using var parameters = JsonDocument.Parse(request.ParamsJson);
        var root = parameters.RootElement;
        Assert.Equal("sess_1", root.GetProperty("sessionId").GetString());
        Assert.Equal("m0", root.GetProperty("afterMessageId").GetString());
        Assert.Equal(40, root.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ReadMessages_OmittedCursorFields_AreNotSerializedAsNull()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection);
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));
        connection.EnqueueFor("session/messages", new { messages = Array.Empty<object>() });

        await runtime.ReadMessagesAsync(handle);

        var request = connection.Requests.Single(r => r.Method == "session/messages");
        Assert.DoesNotContain("null", request.ParamsJson);
        Assert.DoesNotContain("afterMessageId", request.ParamsJson);
        Assert.DoesNotContain("limit", request.ParamsJson);
    }
}
