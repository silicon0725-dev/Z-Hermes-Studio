using System.Text.Json;
using ZHS.Domain.Tasks;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

public sealed class FakeMcpClient : ZHS.Client.IMcpClient
{
    public List<(string Tool, string ArgumentsJson)> Calls { get; } = [];

    public Func<string, JsonElement, JsonElement>? Handler { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        if (Handler is not null)
        {
            var result = Handler("tools/list", JsonSerializer.SerializeToElement(new { }, ZHS.Protocol.Wire.ProtocolJson.Options));
            return Task.FromResult<IReadOnlyList<string>>(
                result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array
                    ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToList()
                    : []);
        }

        return Task.FromResult<IReadOnlyList<string>>(["cluster_create", "cluster_status", "cluster_stop", "task_list", "artifact_list", "events"]);
    }

    public Task<JsonElement> CallToolAsync(string toolName, object? arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, JsonSerializer.Serialize(arguments, ZHS.Protocol.Wire.ProtocolJson.Options)));
        if (Handler is null)
        {
            throw new InvalidOperationException($"no handler for {toolName}");
        }

        return Task.FromResult(Handler(toolName, JsonSerializer.SerializeToElement(arguments, ZHS.Protocol.Wire.ProtocolJson.Options)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ZcacClusterAdapterTests
{
    private static TaskRecord Task() => new("task_z1", "标题", "提示词", "/tmp/ws", TaskStatus.Pending, 1, 1);

    [Fact]
    public async Task Initialize_RejectsNonZcacServer()
    {
        var mcp = new FakeMcpClient();
        mcp.Handler = (_, _) => JsonSerializer.SerializeToElement(new { tools = new object[] { new { name = "echo" } } });
        var adapter = new ZcacClusterAdapter(mcp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.InitializeAsync());
    }

    [Fact]
    public async Task Execute_PollsEventsToRunCompleted_AndReturnsTaskSummary()
    {
        var mcp = new FakeMcpClient();
        var call = 0;
        mcp.Handler = (tool, arguments) =>
        {
            if (tool == "tools/list") return JsonSerializer.SerializeToElement(new { tools = new object[] { new { name = "cluster_create" }, new { name = "events" }, new { name = "task_list" } } });
            if (tool == "cluster_create")
            {
                return JsonSerializer.SerializeToElement(new { runId = "run_1", taskId = "task_1", status = "started" });
            }

            if (tool == "events")
            {
                call++;
                if (call == 1)
                {
                    return JsonSerializer.SerializeToElement(new
                    {
                        events = new object[]
                        {
                            new { sequence = 1, type = "RUN_CREATED" },
                            new { sequence = 2, type = "TASK_STARTED" },
                        },
                    });
                }

                return JsonSerializer.SerializeToElement(new
                {
                    events = new object[]
                    {
                        new { sequence = 3, type = "TASK_SUCCEEDED" },
                        new { sequence = 4, type = "RUN_COMPLETED" },
                    },
                });
            }

            if (tool == "task_list")
            {
                return JsonSerializer.SerializeToElement(new
                {
                    tasks = new object[] { new { role = "coder", status = "succeeded", output = "任务输出" } },
                });
            }

            throw new InvalidOperationException(tool);
        };
        var adapter = new ZcacClusterAdapter(mcp, pollInterval: TimeSpan.FromMilliseconds(5));
        await adapter.InitializeAsync();

        var progress = new List<string>();
        var output = await adapter.ExecuteAsync(Task(), new InlineProgress(progress.Add), CancellationToken.None);

        Assert.Contains("任务输出", output);
        Assert.Contains(progress, line => line.Contains("RUN_COMPLETED"));
        // 第二次 events 轮询必须带上游标（afterSequence=2）
        var secondPoll = mcp.Calls.Where(c => c.Tool == "events").Skip(1).First();
        Assert.Contains("\"afterSequence\":2", secondPoll.ArgumentsJson);
    }

    [Fact]
    public async Task Execute_RunFailed_Throws()
    {
        var mcp = new FakeMcpClient();
        mcp.Handler = (tool, _) => tool switch
        {
            "tools/list" => JsonSerializer.SerializeToElement(new { tools = new object[] { new { name = "cluster_create" }, new { name = "events" }, new { name = "task_list" } } }),
            "cluster_create" => JsonSerializer.SerializeToElement(new { runId = "run_1", status = "started" }),
            "events" => JsonSerializer.SerializeToElement(new { events = new object[] { new { sequence = 2, type = "RUN_FAILED" } } }),
            _ => throw new InvalidOperationException(tool),
        };
        var adapter = new ZcacClusterAdapter(mcp, pollInterval: TimeSpan.FromMilliseconds(5));
        await adapter.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(Task(), null, CancellationToken.None));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
