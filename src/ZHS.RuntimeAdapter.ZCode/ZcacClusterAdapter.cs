using System.Text.Json;
using ZHS.Client;
using ZHS.Domain.Tasks;
using ZHS.Protocol.Wire;

namespace ZHS.RuntimeAdapter.ZCode;

/// <summary>ZCAC 集群运行的状态视图（cluster_status 的最小映射）。</summary>
public sealed record ClusterRunStatus(string RunId, string Status, bool Active);

/// <summary>
/// ITaskExecutor 的 ZCAC 集群实现（ADR-0002 L2）：
/// ExecuteAsync = cluster_create（single 模式）+ events 游标轮询到 RUN_COMPLETED/RUN_FAILED；
/// 取消 = cluster_stop。事件类型与游标语义来自 M0 盘点 §3.3 与活体验证。
/// </summary>
public sealed class ZcacClusterAdapter : ITaskExecutor
{
    private readonly IMcpClient _mcp;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, string> _runByTask = [];

    public ZcacClusterAdapter(IMcpClient mcp, TimeSpan? pollInterval = null)
    {
        _mcp = mcp;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1000);
    }

    /// <summary>用独立编排器（standalone orchestrator.cjs）构造。env 至少包含 ZCAC_WORKSPACE；测试用 ZCAC_TEST_FAKE=1。</summary>
    public static ZcacClusterAdapter LaunchStandalone(
        string nodePath,
        string orchestratorPath,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan? pollInterval = null)
    {
        var options = new McpStdioOptions
        {
            ExecutablePath = nodePath,
            Arguments = [orchestratorPath],
            Environment = environment,
        };
        return new ZcacClusterAdapter(new McpStdioClient(options), pollInterval);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mcp.InitializeAsync(cancellationToken);
        var tools = await _mcp.ListToolsAsync(cancellationToken);
        if (!tools.Contains("cluster_create") || !tools.Contains("events"))
        {
            throw new InvalidOperationException($"目标 MCP server 不是 ZCAC（缺少 cluster_create/events）：{string.Join(",", tools)}");
        }
    }

    public async Task<string> ExecuteAsync(TaskRecord task, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
    {
        var created = await _mcp.CallToolAsync(
            "cluster_create",
            new { task = task.Prompt, mode = "single" },
            cancellationToken);
        var runId = created.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String
            ? run.GetString()
            : null;
        if (runId is null)
        {
            throw new InvalidOperationException($"cluster_create 未返回 runId：{created.GetRawText()}");
        }
        _runByTask[task.TaskId] = runId;
        deltaProgress?.Report($"[cluster] run {runId} started");

        var cursor = 0L;
        while (true)
        {
            var result = await _mcp.CallToolAsync(
                "events",
                new { runId, afterSequence = cursor },
                cancellationToken);
            if (result.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in events.EnumerateArray())
                {
                    var sequence = item.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt64() : cursor;
                    cursor = Math.Max(cursor, sequence);
                    var type = item.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                    deltaProgress?.Report($"[cluster] #{sequence} {type}");
                    if (type == "RUN_COMPLETED")
                    {
                        return await ReadRunOutputAsync(runId, cancellationToken);
                    }

                    if (type == "RUN_FAILED")
                    {
                        throw new InvalidOperationException($"ZCAC run {runId} failed（events 游标 {cursor}）");
                    }
                }
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    public async Task<ClusterRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        var result = await _mcp.CallToolAsync("cluster_status", new { runId }, cancellationToken);
        var runs = result.TryGetProperty("runs", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list[0]
            : throw new InvalidOperationException($"cluster_status 无该 run：{runId}");
        return new ClusterRunStatus(
            runId,
            runs.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
            runs.TryGetProperty("active", out var active) && active.GetBoolean());
    }

    /// <summary>取消集群运行（cluster_stop）。未启动或不存在的任务静默返回 false。</summary>
    public async Task<bool> TryCancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!_runByTask.TryGetValue(taskId, out var runId))
        {
            return false;
        }

        await _mcp.CallToolAsync("cluster_stop", new { runId }, cancellationToken);
        return true;
    }

    /// <summary>任务对应的 ZCAC runId（ExecuteAsync 之后可查）。</summary>
    public string? GetRunId(string taskId) => _runByTask.TryGetValue(taskId, out var runId) ? runId : null;

    private async Task<string> ReadRunOutputAsync(string runId, CancellationToken cancellationToken)
    {
        var tasks = await _mcp.CallToolAsync("task_list", new { runId }, cancellationToken);
        if (!tasks.TryGetProperty("tasks", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return $"run {runId} completed";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var item in list.EnumerateArray())
        {
            var status = item.TryGetProperty("status", out var s) ? s.GetString() : "?";
            var role = item.TryGetProperty("role", out var r) ? r.GetString() : "?";
            builder.Append($"[{role}:{status}] ");
            if (item.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            {
                builder.Append(output.GetString());
            }
        }

        return builder.ToString().TrimEnd();
    }

    public async ValueTask DisposeAsync() => await _mcp.DisposeAsync();
}
