using System.Collections.Concurrent;
using System.Text.Json;
using ZHS.Protocol.Wire;
using Xunit;

namespace ZHS.Client.Tests;

/// <summary>
/// v4 推送订阅冒烟（无模型调用）：
/// session/create → v4/conversation/subscribe → ACK → 初始快照帧以 v4/conversation/frame 通知推送。
/// 实验结论（2026-10-17 活体验证）：裸 stdio 协议上外部客户端可直接订阅，无 clientHello 门槛。
/// 默认跳过；ZHS_E2E=1 启用。
/// </summary>
public sealed class V4SubscriptionSmokeTests
{
    private const string DefaultBundle = "/opt/ZCode/resources/glm/zcode.cjs";

    [Fact]
    public async Task Subscribe_ReceivesAckAndInitialFrame()
    {
        if (Environment.GetEnvironmentVariable("ZHS_E2E") != "1" || !File.Exists(DefaultBundle))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var connection = ZcodeAppServerConnection.Start(new ZcodeAppServerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
        });

        var notifications = new ConcurrentQueue<ProtocolNotification>();
        var listening = Task.Run(async () =>
        {
            try
            {
                await foreach (var notification in connection.ReadNotificationsAsync(timeout.Token))
                {
                    notifications.Enqueue(notification);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var workspace = Path.Combine(Path.GetTempPath(), "zhs-v4-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        var created = await connection.SendRequestAsync(
            "session/create",
            new CreateSessionParams(new WorkspaceRef(workspace, workspace), "yolo"),
            timeout.Token) ?? throw new InvalidOperationException("session/create returned empty result");
        var session = created.Deserialize<CreatedSession>(ProtocolJson.Options)?.ResolveSessionId()
            ?? throw new InvalidOperationException("no sessionId");

        var connectionId = Guid.NewGuid().ToString();
        var subscribe = await connection.SendRequestAsync(
            "v4/conversation/subscribe",
            new { topic = $"conversation/{session}", connectionId, clientMode = "desktop-continuous" },
            timeout.Token);

        Assert.NotNull(subscribe);
        using var document = JsonDocument.Parse(subscribe.Value.GetRawText());
        var ack = document.RootElement.GetProperty("ack");
        var subscriptionId = ack.GetProperty("subscriptionId").GetString();
        Assert.False(string.IsNullOrEmpty(subscriptionId));
        Assert.Equal("snapshot", ack.GetProperty("mode").GetString());

        // 初始快照帧在 ACK 之后以 v4/conversation/frame 通知推送（post-response outbox）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var notification in notifications)
            {
                if (notification.Method != "v4/conversation/frame" || notification.Params is null)
                {
                    continue;
                }

                var frame = notification.Params.Value;
                if (frame.GetProperty("topic").GetString() != $"conversation/{session}")
                {
                    continue;
                }

                Assert.Equal("initial", frame.GetProperty("deliveryKind").GetString());
                Assert.Equal(subscriptionId, frame.GetProperty("subscriptionId").GetString());
                return;
            }

            await Task.Delay(200, timeout.Token);
        }

        throw new TimeoutException("no v4/conversation/frame initial frame received within 20s");
    }
}
