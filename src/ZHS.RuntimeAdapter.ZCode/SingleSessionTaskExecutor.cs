using ZHS.Domain.Tasks;
using ZHS.Protocol.Runtime;

namespace ZHS.RuntimeAdapter.ZCode;

/// <summary>
/// ITaskExecutor 的单会话实现（M3）：任务 = 独立 zcode 会话中的一个回合。
/// M3+ 换成 ZCAC 集群 Adapter（ADR-0002 L2：cluster_create/task_list/events 游标契约）。
/// </summary>
public sealed class SingleSessionTaskExecutor : ITaskExecutor
{
    private readonly ZcodeRuntime _runtime;

    public SingleSessionTaskExecutor(ZcodeRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string> ExecuteAsync(TaskRecord task, IProgress<string>? deltaProgress, CancellationToken cancellationToken)
    {
        var handle = await _runtime.StartAsync(new AgentTaskRequest(task.Workspace, task.Prompt), cancellationToken);
        try
        {
            var result = await _runtime.SendTurnAsync(handle, task.Prompt, deltaProgress, cancellationToken);
            return result.Outcome switch
            {
                TurnOutcome.Completed => result.Response,
                _ => throw new InvalidOperationException(result.Error ?? "turn failed"),
            };
        }
        finally
        {
            try
            {
                await _runtime.CloseSessionAsync(handle, CancellationToken.None);
            }
            catch (Exception)
            {
                // 尽力关闭
            }
        }
    }
}
