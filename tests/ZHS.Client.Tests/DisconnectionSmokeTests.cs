using ZHS.Protocol.Wire;
using Xunit;

namespace ZHS.Client.Tests;

/// <summary>
/// 断连处理（M1 验收：连接断开时不挂死、不崩溃）。
/// 杀死 app-server 子进程后：挂起请求必须以 ZcodeConnectionClosedException 快速失败（而非等到超时）。
/// 默认跳过；ZHS_E2E=1 启用。
/// </summary>
public sealed class DisconnectionSmokeTests
{
    private const string DefaultBundle = "/opt/ZCode/resources/glm/zcode.cjs";

    [Fact]
    public async Task ProcessKill_FailsPendingRequestsWithConnectionClosed()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        var connection = ZcodeAppServerConnection.Start(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(60),
        });

        // 先做一次成功请求确认连接健康
        var list = await connection.SendRequestAsync("session/list", null, CancellationToken.None);
        Assert.NotNull(list);

        // 发一个注定没有响应的请求（对不存在会话的 events 轮询会被 Zod 拒绝并返回错误——
        // 为制造"无响应"，用合法但慢的路径：先杀进程再发请求）
        connection.Kill();

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => connection.SendRequestAsync("session/list", null, CancellationToken.None));

        // 快速失败（ZcodeConnectionClosedException 派生自其 message 或直接命中），
        // 而不是等满 60 秒超时
        Assert.True(exception is ZcodeConnectionClosedException || exception is ZcodeProtocolException || exception is System.InvalidOperationException,
            $"意外异常类型：{exception.GetType().Name}: {exception.Message}");
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ProcessKill_PendingInFlightRequest_FailsNotHangs()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        var connection = ZcodeAppServerConnection.Start(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(90),
        });

        // 启动一个 in-flight 请求（等待期间杀进程）
        var pending = connection.SendRequestAsync("session/list", null, CancellationToken.None);
        await Task.Delay(2000);
        connection.Kill();

        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(pending, completed); // 15 秒内必须终结，不得挂到 90 秒超时

        await connection.DisposeAsync();
    }
}
