using ZHS.Domain.Tasks;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>
/// 真实集群 e2e（ZHS_ZCAC_E2E=1，一次真实模型调用 ≈ 15-60s）：
/// 独立编排器 + protocol Worker（真实 zcode 子进程）→ cluster_create → RUN_COMPLETED。
/// 前置：/tmp/zhs-m0 的 node24 与 orchestrator bundle（重建配方见 docs/zcode-protocol-notes.md）。
/// </summary>
public sealed class ZcacClusterE2eTests
{
    private const string NodePath = "/tmp/zhs-m0/node24/bin/node";
    private const string Orchestrator = "/tmp/zhs-m0/zcac-standalone/dist/orchestrator.cjs";
    private const string CliBundle = "/opt/ZCode/resources/glm/zcode.cjs";
    private const string ModelRef = "43b31d2a-5783-43f0-a128-2b8f70b1887e/deepseek-flash@low";

    [Fact]
    public async Task RealCluster_ProtocolWorker_RunCompletes()
    {
        if (Environment.GetEnvironmentVariable("ZHS_ZCAC_E2E") != "1"
            || !File.Exists(NodePath) || !File.Exists(Orchestrator) || !File.Exists(CliBundle))
        {
            return;
        }

        var dataDir = Path.Combine(Path.GetTempPath(), "zhs-zcac-real-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);
        await using var adapter = ZcacClusterAdapter.LaunchStandalone(
            NodePath,
            Orchestrator,
            new Dictionary<string, string>
            {
                ["ZCAC_WORKER_MODE"] = "protocol",
                ["ZCODE_CLI_BUNDLE"] = CliBundle,
                ["ZCAC_MODEL"] = ModelRef,
                ["ZCAC_DATA_DIR"] = dataDir,
                ["ZCAC_WORKSPACE"] = dataDir,
                ["ZCAC_TASK_TIMEOUT_MS"] = "240000",
            },
            pollInterval: TimeSpan.FromMilliseconds(2000));
        await adapter.InitializeAsync();

        var task = new TaskRecord("task_real", "真实集群验收", "请只回复四个字：集群就绪", dataDir, TaskStatus.Pending, 0, 0);
        var progress = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var output = await adapter.ExecuteAsync(task, new ProgressReporter(progress.Add), timeout.Token);

        Assert.Contains("succeeded", output);
        Assert.Contains(progress, line => line.Contains("RUN_COMPLETED"));
        Assert.Contains(progress, line => line.Contains("ARTIFACT_CREATED"));

        var runId = adapter.GetRunId(task.TaskId);
        Assert.NotNull(runId);
        var status = await adapter.GetStatusAsync(runId!, timeout.Token);
        Assert.False(status.Active);
    }

    private sealed class ProgressReporter(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }
}
