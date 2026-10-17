using System.Text.Json;
using ZHS.Client;
using ZHS.Protocol.Runtime;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>SendTurnAsync 轮询契约测试：事件形状按 ZCAC 实测（seq / kind / payload.response）。</summary>
public sealed class SendTurnAsyncTests
{
    private const string Workspace = "/tmp/zhs-workspace";

    private static async Task<AgentHandle> CreateHandleAsync(ZcodeRuntime runtime, FakeProtocolConnection connection)
    {
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        return await runtime.StartAsync(new AgentTaskRequest(Workspace, "hi"));
    }

    private static ZcodeRuntime CreateRuntime(FakeProtocolConnection connection)
    {
        return new ZcodeRuntime(connection, pollInterval: TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task CompletedTurn_ReturnsResponseText()
    {
        var connection = new FakeProtocolConnection();
        var runtime = CreateRuntime(connection);
        var handle = await CreateHandleAsync(runtime, connection);
        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new
        {
            events = new object[]
            {
                new { seq = 1, type = "message.delta", payload = new { } },
                new { seq = 2, type = "turn.completed", payload = new { response = "好的" } },
            },
        });

        var result = await runtime.SendTurnAsync(handle, "请回复好的");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("好的", result.Response);

        var send = connection.Requests.Single(request => request.Method == "session/send");
        using var sendParams = JsonDocument.Parse(send.ParamsJson);
        Assert.Equal("请回复好的", sendParams.RootElement.GetProperty("content").GetString());

        var events = connection.Requests.Single(request => request.Method == "session/events");
        using var eventsParams = JsonDocument.Parse(events.ParamsJson);
        Assert.Equal(0, eventsParams.RootElement.GetProperty("afterSeq").GetInt64());
        Assert.Equal(100, eventsParams.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task PollsAcrossEmptyBatches_AndAdvancesCursor()
    {
        var connection = new FakeProtocolConnection();
        var runtime = CreateRuntime(connection);
        var handle = await CreateHandleAsync(runtime, connection);
        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new { events = new object[] { new { seq = 3, type = "message.delta", payload = new { } } } });
        connection.EnqueueFor("session/events", new { events = new object[] { new { seq = 7, type = "turn.completed", payload = new { response = "第二轮回合完成" } } } });

        var result = await runtime.SendTurnAsync(handle, "hello");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);

        var eventCalls = connection.Requests.Where(request => request.Method == "session/events").ToList();
        Assert.Equal(2, eventCalls.Count);
        using var second = JsonDocument.Parse(eventCalls[1].ParamsJson);
        Assert.Equal(3, second.RootElement.GetProperty("afterSeq").GetInt64());
    }

    [Fact]
    public async Task FailedTurn_ReturnsError()
    {
        var connection = new FakeProtocolConnection();
        var runtime = CreateRuntime(connection);
        var handle = await CreateHandleAsync(runtime, connection);
        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new
        {
            events = new object[] { new { seq = 1, type = "turn.failed", payload = new { error = "model error" } } },
        });

        var result = await runtime.SendTurnAsync(handle, "hi");

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("model error", result.Error);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var connection = new FakeProtocolConnection();
        var runtime = CreateRuntime(connection);
        var handle = await CreateHandleAsync(runtime, connection);
        connection.EnqueueFor("session/send", new { ok = true });
        connection.EnqueueFor("session/events", new { events = Array.Empty<object>() });
        connection.Handler = (_, _) => null; // 后续 events 轮询走 Handler 返回 null 不再断言

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        connection.Handler = (method, _) => method == "session/events"
            ? JsonSerializer.SerializeToElement(new { events = Array.Empty<object>() })
            : null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.SendTurnAsync(handle, "hi", cancellation.Token));
    }
}
