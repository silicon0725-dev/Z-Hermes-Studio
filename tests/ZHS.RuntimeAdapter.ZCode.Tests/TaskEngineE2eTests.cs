using ZHS.Client;
using ZHS.Domain.Tasks;
using ZHS.Persistence;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;
using TaskStatus = ZHS.Domain.Tasks.TaskStatus;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>
/// M3 验收 e2e（ZHS_E2E=1，一次真实模型调用）：
/// TaskEngine + SQLite 仓储 + 单会话执行器全链路：创建 → 执行 → Succeeded + journal 落库。
/// </summary>
public sealed class TaskEngineE2eTests
{
    private const string DefaultBundle = "/opt/ZCode/resources/glm/zcode.cjs";

    [Fact]
    public async Task Task_CreateRunComplete_PersistedWithJournal()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        var databasePath = Path.Combine(Path.GetTempPath(), "zhs-task-e2e-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var workspace = Path.Combine(Path.GetTempPath(), "zhs-task-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        await using (var database = await ZhsDatabase.OpenAsync(databasePath, "e2e"))
        {
            var store = new TaskSqliteStore(database);
            await using var runtime = ZcodeRuntime.Launch(new ZcodeAppServerOptions { RequestTimeout = TimeSpan.FromSeconds(120) });
            var engine = new TaskEngine(store, store, new SingleSessionTaskExecutor(runtime));

            var task = await engine.CreateAsync("验收任务", "请只回复四个字：任务完成", workspace);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            var final = await engine.RunAsync(task.TaskId, timeout.Token);

            Assert.Equal(TaskStatus.Succeeded, final.Status);
            Assert.False(string.IsNullOrWhiteSpace(final.OutputText));

            // 从持久层重新读取：状态与 journal 都已落库（M3 验收：可查看结果）
            var reloaded = await engine.GetAsync(task.TaskId);
            Assert.NotNull(reloaded);
            Assert.Equal(TaskStatus.Succeeded, reloaded!.Status);
            var journal = await engine.ReadJournalAsync(task.TaskId);
            Assert.Equal(3, journal.Count);
            Assert.Equal(TaskEventKind.Completed, journal[^1].Kind);
        }

        try
        {
            File.Delete(databasePath);
        }
        catch (Exception)
        {
        }
    }
}
