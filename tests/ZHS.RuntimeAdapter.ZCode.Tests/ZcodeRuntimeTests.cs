using System.Text.Json;
using ZHS.Protocol.Runtime;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

public sealed class ZcodeRuntimeTests
{
    private static readonly string Workspace = "/tmp/zhs-workspace";

    private static (ZcodeRuntime Runtime, FakeProtocolConnection Connection) Create()
    {
        var connection = new FakeProtocolConnection();
        return (new ZcodeRuntime(connection), connection);
    }

    private static AgentTaskRequest SampleRequest(string? mode = null, IReadOnlyList<string>? tools = null) =>
        new(Workspace, "do something", Mode: mode, ToolAllowlist: tools);

    [Fact]
    public async Task StartAsync_UsesWorkspaceKeyEqualsPath_AndDefaultYoloMode()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_nested"}}""");

        var handle = await runtime.StartAsync(SampleRequest());

        Assert.Equal("sess_nested", handle.SessionId);
        var (method, paramsJson) = Assert.Single(connection.Requests);
        Assert.Equal("session/create", method);
        using var document = JsonDocument.Parse(paramsJson);
        var root = document.RootElement;
        Assert.Equal(Workspace, root.GetProperty("workspace").GetProperty("workspaceKey").GetString());
        Assert.Equal(Workspace, root.GetProperty("workspace").GetProperty("workspacePath").GetString());
        Assert.Equal("yolo", root.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task StartAsync_PassesModeAndToolAllowlist()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"sessionId":"sess_flat"}""");

        await runtime.StartAsync(SampleRequest(mode: "build", tools: ["Read", "Grep"]));

        var (_, paramsJson) = Assert.Single(connection.Requests);
        using var document = JsonDocument.Parse(paramsJson);
        Assert.Equal("build", document.RootElement.GetProperty("mode").GetString());
        var allowlist = document.RootElement.GetProperty("toolAllowlist");
        Assert.Equal(2, allowlist.GetArrayLength());
    }

    [Fact]
    public async Task StartAsync_WithoutSessionId_Throws()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"other":true}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(SampleRequest()));
    }

    [Fact]
    public async Task SendMessageAsync_SendsSessionIdAndContent()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(SampleRequest());

        connection.EnqueueResult(new { ok = true });
        await runtime.SendMessageAsync(handle, "hello world");

        var send = connection.Requests.Single(request => request.Method == "session/send");
        using var document = JsonDocument.Parse(send.ParamsJson);
        Assert.Equal("sess_1", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("hello world", document.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task InterruptAsync_CallsSessionStop()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(SampleRequest());

        connection.EnqueueResult(new { ok = true });
        await runtime.InterruptAsync(handle);

        Assert.Contains(connection.Requests, request => request.Method == "session/stop");
    }

    [Fact]
    public async Task GetStatusAsync_MapsIdleStatus()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(SampleRequest());

        connection.EnqueueResult(new
        {
            sessions = new object[]
            {
                new { sessionId = "sess_other", status = "running" },
                new { sessionId = "sess_1", status = "idle" },
            },
        });
        var status = await runtime.GetStatusAsync(handle);

        Assert.Equal(AgentStatus.Idle, status);
    }

    [Fact]
    public async Task GetStatusAsync_UnknownSession_ReturnsUnknown()
    {
        var (runtime, connection) = Create();
        connection.EnqueueRaw("""{"session":{"sessionId":"sess_1"}}""");
        var handle = await runtime.StartAsync(SampleRequest());

        connection.EnqueueResult(new { sessions = Array.Empty<object>() });
        var status = await runtime.GetStatusAsync(handle);

        Assert.Equal(AgentStatus.Unknown, status);
    }
}
