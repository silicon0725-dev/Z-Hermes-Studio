using ZHS.Protocol.Runtime;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>part.delta 流式增量经 IProgress 上报的契约测试。</summary>
public sealed class SendTurnStreamingTests
{
    private const string Workspace = "/tmp/zhs-workspace";

    [Fact]
    public async Task PartDeltaEvents_AreReportedInOrder_BeforeCompletion()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection, pollInterval: TimeSpan.FromMilliseconds(5));
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));

        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new
        {
            events = new object[]
            {
                new { seq = 1, type = "part.delta", payload = new { delta = "你", done = false, kind = "text_delta" } },
                new { seq = 2, type = "part.delta", payload = new { delta = "好", done = true, kind = "text_delta" } },
                new { seq = 3, type = "turn.completed", payload = new { response = "你好" } },
            },
        });

        var deltas = new List<string>();
        var result = await runtime.SendTurnAsync(handle, "hi", new InlineProgress(deltas.Add));

        Assert.Equal(["你", "好"], deltas);
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("你好", result.Response);
    }

    [Fact]
    public async Task DeltasAcrossPollBatches_StayOrdered()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection, pollInterval: TimeSpan.FromMilliseconds(5));
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));

        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new { events = new object[] { new { seq = 1, type = "part.delta", payload = new { delta = "第一" } } } });
        connection.EnqueueFor("session/events", new { events = new object[] { new { seq = 2, type = "part.delta", payload = new { delta = "第二" } }, new { seq = 3, type = "turn.completed", payload = new { response = "第一第二" } } } });

        var deltas = new List<string>();
        await runtime.SendTurnAsync(handle, "hi", new InlineProgress(deltas.Add));

        Assert.Equal(["第一", "第二"], deltas);
    }

    [Fact]
    public async Task ModelStreamingTextDeltas_AreReported_ToolDeltasSkipped()
    {
        var connection = new FakeProtocolConnection();
        var runtime = new ZcodeRuntime(connection, pollInterval: TimeSpan.FromMilliseconds(5));
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));

        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new
        {
            events = new object[]
            {
                new { seq = 1, type = "model.streaming", payload = new { assistantMessageId = "m1", delta = "你好", done = false, kind = "text_delta" } },
                new { seq = 2, type = "model.streaming", payload = new { toolCallId = "t1", delta = "{\"file\":", done = false, kind = "input_json_delta" } },
                new { seq = 3, type = "turn.completed", payload = new { response = "你好" } },
            },
        });

        var deltas = new List<string>();
        var result = await runtime.SendTurnAsync(handle, "hi", new InlineProgress(deltas.Add));

        // v3.15.3 实测：流式文本增量走 model.streaming + kind:"text_delta"；工具调用增量不上报 UI
        Assert.Equal(["你好"], deltas);
        Assert.Equal("你好", result.Response);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
