using System.Text.Json;
using ZHS.Protocol.Wire;
using Xunit;

namespace ZHS.Client.Tests;

/// <summary>
/// 对本机 zcode app-server 的无头冒烟（只读调用：session/list + 错误信封，不创建会话）。
/// 前置：/opt/ZCode 存在（v3.15.3 deb 安装）；其他环境自动跳过。
/// </summary>
public sealed class AppServerSmokeTests
{
    private const string DefaultBundle = "/opt/ZCode/resources/glm/zcode.cjs";

    [Fact]
    public async Task AppServer_StartupNotification_SessionList_AndErrorEnvelope()
    {
        if (!File.Exists(DefaultBundle))
        {
            return; // 非 Linux/ZCode 环境：跳过
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var connection = ZcodeAppServerConnection.Start(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
        });

        // 1) 启动通知：第一条必须是 startup/storageState，且 phase 走到 ready
        StorageStateParams? ready = null;
        await foreach (var notification in connection.ReadNotificationsAsync(timeout.Token))
        {
            if (notification.Method != StartupNotificationMethods.StorageState)
            {
                continue;
            }

            var state = notification.Params!.Value.Deserialize<StorageStateParams>(ProtocolJson.Options);
            Assert.NotNull(state);
            if (state.Phase == "ready")
            {
                ready = state;
                break;
            }
        }

        Assert.NotNull(ready);
        Assert.True(ready.Sequence >= 1);
        Assert.NotEmpty(ready.DatabaseId);

        // 2) session/list：真实会话库，返回的会话 id 形如 sess_<uuid>
        var result = await connection.SendRequestAsync("session/list", null, timeout.Token);
        Assert.NotNull(result);
        var list = result.Value.Deserialize<SessionListResult>(ProtocolJson.Options);
        Assert.NotNull(list);
        Assert.NotEmpty(list.Sessions);
        Assert.All(list.Sessions, session => Assert.StartsWith("sess_", session.SessionId));

        // 3) 错误信封：未知方法 → JSON-RPC -32601
        var exception = await Assert.ThrowsAsync<ZcodeProtocolException>(
            () => connection.SendRequestAsync("bogus/method", null, timeout.Token));
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, exception.Code);
        Assert.Contains("bogus/method", exception.Message);
    }

    [Fact]
    public async Task AppServer_RequestTimeout_FailsCleanly()
    {
        if (!File.Exists(DefaultBundle))
        {
            return;
        }

        // 把超时压到 1ms：任何真实请求都会超时而非挂死，且连接仍可正常释放
        await using var connection = ZcodeAppServerConnection.Start(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(1),
        });

        // 启动耗时会先吃掉这个窗口，这里只验证超时异常路径与释放不抛
        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => connection.SendRequestAsync("session/list", null, CancellationToken.None));
    }
}
