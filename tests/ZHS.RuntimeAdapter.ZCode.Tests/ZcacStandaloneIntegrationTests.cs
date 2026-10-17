using ZHS.Domain.Tasks;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>
/// 对独立构建的 ZCAC 编排器（ZCAC_TEST_FAKE=1，零模型调用）的真实集成：
/// MCP 握手 → cluster_create → events 轮询 → RUN_COMPLETED → task_list 输出。
/// 前置：/tmp/zhs-m0/zcac-standalone/dist/orchestrator.cjs 与 node24 存在，否则自动跳过。
/// </summary>
public sealed class ZcacStandaloneIntegrationTests
{
    private const string NodePath = "/tmp/zhs-m0/node24/bin/node";
    private const string Orchestrator = "/tmp/zhs-m0/zcac-standalone/dist/orchestrator.cjs";

    public static bool Available => File.Exists(NodePath) && File.Exists(Orchestrator);

    [Fact]
    public async Task StandaloneOrchestrator_FakeCluster_RunCompletes()
    {
        if (!Available)
        {
            return;
        }

        var dataDir = Path.Combine(Path.GetTempPath(), "zhs-zcac-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);
        await using var adapter = ZcacClusterAdapter.LaunchStandalone(
            NodePath,
            Orchestrator,
            new Dictionary<string, string>
            {
                ["ZCAC_TEST_FAKE"] = "1",
                ["ZCAC_DATA_DIR"] = dataDir,
                ["ZCAC_WORKSPACE"] = dataDir,
            },
            pollInterval: TimeSpan.FromMilliseconds(200));

        await adapter.InitializeAsync();

        var task = new TaskRecord("task_it", "集成测试", "集成测试任务：验证独立编排器", dataDir, TaskStatus.Pending, 0, 0);
        var progress = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = await adapter.ExecuteAsync(task, new ProgressReporter(progress.Add), timeout.Token);

        Assert.Contains("succeeded", output);
        Assert.Contains(progress, line => line.Contains("RUN_COMPLETED"));

        var status = await adapter.GetStatusAsync(
            adapter.GetRunId("task_it") ?? throw new InvalidOperationException("runId not tracked"),
            timeout.Token);
        Assert.Equal("completed", status.Status);
    }

    private sealed class ProgressReporter(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }
}
