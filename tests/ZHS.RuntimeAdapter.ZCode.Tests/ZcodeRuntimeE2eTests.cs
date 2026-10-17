using ZHS.Client;
using ZHS.Protocol.Runtime;
using ZHS.RuntimeAdapter.ZCode;
using Xunit;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

/// <summary>
/// 端到端冒烟：真实拉起本机 zcode app-server（v3.15.3），创建会话并发送一个最小提示词，
/// 走完 create → send → events 轮询 → turn.completed 闭环（同时隐式验证服务端反向请求自动应答）。
/// 默认跳过；设置环境变量 ZHS_E2E=1 启用（会消耗一次真实模型调用，并在本机会话库留下一条记录）。
/// </summary>
public sealed class ZcodeRuntimeE2eTests
{
    private const string DefaultBundle = "/opt/ZCode/resources/glm/zcode.cjs";

    [Fact]
    public async Task LiveChatTurn_CompletesWithReply()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "zhs-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));

        await using var runtime = ZcodeRuntime.Launch(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(120),
        });

        var handle = await runtime.StartAsync(new AgentTaskRequest(workspace, string.Empty), timeout.Token);
        try
        {
            var deltas = new List<string>();
            var turn = await runtime.SendTurnAsync(handle, "请只回复两个字：好的", new InlineProgress(deltas.Add), timeout.Token);

            Assert.Equal(TurnOutcome.Completed, turn.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(turn.Response), $"turn.Response 为空，错误信息：{turn.Error}");
            // 流式增量应先于完成到达（轮询批次内）
            Assert.Contains(deltas, delta => delta.Contains("好的"));

            // 历史读取（session/messages，游标分页）：本回合的用户消息与助手回复都应可读回
            var history = await runtime.ReadMessagesAsync(handle, limit: 20, cancellationToken: timeout.Token);
            Assert.True(history.Count >= 2, $"历史消息不足 2 条，实际 {history.Count}");
            Assert.Contains(history, message => message.ExtractText().Contains("请只回复两个字"));
            Assert.Contains(history, message => message.ExtractText().Contains("好的"));

            // 关闭后恢复（session/resume）并继续第二个回合 —— 验证恢复的会话真正可用
            await runtime.CloseSessionAsync(handle, timeout.Token);
            var resumed = await runtime.ResumeSessionAsync(handle.SessionId, timeout.Token);
            Assert.Equal(handle.SessionId, resumed.SessionId);
            var secondTurn = await runtime.SendTurnAsync(resumed, "请只回复三个字：继续吧", new InlineProgress(_ => { }), timeout.Token);
            Assert.Equal(TurnOutcome.Completed, secondTurn.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(secondTurn.Response), $"第二回合回复为空：{secondTurn.Error}");
        }
        finally
        {
            try
            {
                await runtime.CloseSessionAsync(handle, CancellationToken.None);
            }
            catch (Exception)
            {
                // 尽力关闭
            }
        }
    }

    /// <summary>真实取消闭环：长提示词 → 首个增量到达后 session/stop → 回合应终结（failed 或抢先 completed）。</summary>
    [Fact]
    public async Task LiveCancel_InterruptsActiveTurn()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "zhs-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        await using var runtime = ZcodeRuntime.Launch(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(120),
        });

        var handle = await runtime.StartAsync(new AgentTaskRequest(workspace, string.Empty), timeout.Token);
        try
        {
            var firstDelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deltas = new List<string>();
            var progress = new InlineProgress(delta =>
            {
                deltas.Add(delta);
                firstDelta.TrySetResult();
            });

            var turnTask = runtime.SendTurnAsync(
                handle,
                "请从 1 慢慢数到 40，每行一个数字，不要省略。",
                progress,
                timeout.Token);

            // 首个流式增量到达后立即中断
            await firstDelta.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await runtime.InterruptAsync(handle, timeout.Token);

            var turn = await turnTask.WaitAsync(TimeSpan.FromSeconds(30));

            // 中断后回合必须终结（30 秒窗口内返回即通过；outcome 可能是 failed 也可能抢先 completed）
            Assert.NotEmpty(deltas);

            // 中断后连接与会话仍可用（session/list 正常返回已知状态）
            var status = await runtime.GetStatusAsync(handle, timeout.Token);
            Assert.NotEqual(AgentStatus.Unknown, status);
        }
        finally
        {
            try
            {
                await runtime.CloseSessionAsync(handle, CancellationToken.None);
            }
            catch (Exception)
            {
                // 尽力关闭
            }
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
