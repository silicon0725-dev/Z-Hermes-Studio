using System.Text.Json;
using ZHS.Protocol.Wire;
using Xunit;

namespace ZHS.Protocol.Tests;

/// <summary>线协议绑定测试：载荷全部取自 2026-10-17 对本机 v3.15.3 app-server 的探针实测报文。</summary>
public sealed class WireBindingTests
{
    private const string SessionListLivePayload = """
        {"sessions":[{"createdAt":1792211792790,"mode":"build","traceId":"e2c810b4-2be0-4dc4-b5c2-02e210c333a3","sessionId":"sess_f16b3382-98c2-4016-8f12-74360bea4dd9","sessionKind":"interactive","status":"idle","title":"Untitled session","titleSource":"first_input","updatedAt":1792213180859,"workspace":{"workspaceKey":"/home/silicon/other/netease_url_client_linux/Z Hermes Studio","workspacePath":"/home/silicon/other/netease_url_client_linux/Z Hermes Studio"}}]}
        """;

    private const string StorageStateLivePayload = """
        {"schemaVersion":1,"attemptId":"3741ce05-c763-401f-a67f-9afc07629f32","sequence":5,"databaseId":"ffb8bd3ac2f66063a5629a07f969cf28c928414b319ccf2ce0da95433dc517d2","databaseKind":"session","phase":"ready","migration":{"kind":"none","executedCount":0,"committedCount":0,"lastAppliedMigrationId":"0022_backfilled_session_reasoning"},"elapsedMs":10}
        """;

    private static readonly string StartupNotificationFrame =
        "{\"method\":\"startup/storageState\",\"params\":" + StorageStateLivePayload + "}";

    private const string ErrorEnvelopeFrame = """
        {"error":{"code":-32601,"message":"Method not found: bogus/method"},"id":2}
        """;

    [Fact]
    public void SessionListResult_BindsLivePayload()
    {
        var result = JsonSerializer.Deserialize<SessionListResult>(SessionListLivePayload, ProtocolJson.Options);

        Assert.NotNull(result);
        var session = Assert.Single(result.Sessions);
        Assert.Equal("sess_f16b3382-98c2-4016-8f12-74360bea4dd9", session.SessionId);
        Assert.Equal("interactive", session.SessionKind);
        Assert.Equal("idle", session.Status);
        Assert.Equal("build", session.Mode);
        Assert.Equal("first_input", session.TitleSource);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/home/silicon/other/netease_url_client_linux/Z Hermes Studio", session.Workspace.WorkspaceKey);
        Assert.Equal(1792211792790, session.CreatedAt);
    }

    [Fact]
    public void StorageStateParams_BindsLivePayload()
    {
        var parameters = JsonSerializer.Deserialize<StorageStateParams>(StorageStateLivePayload, ProtocolJson.Options);

        Assert.NotNull(parameters);
        Assert.Equal(1, parameters.SchemaVersion);
        Assert.Equal(5, parameters.Sequence);
        Assert.Equal("ready", parameters.Phase);
        Assert.Equal("session", parameters.DatabaseKind);
        Assert.NotNull(parameters.Migration);
        Assert.Equal("0022_backfilled_session_reasoning", parameters.Migration.LastAppliedMigrationId);
    }

    [Fact]
    public void ProtocolNotification_From_ParsesStartupNotification()
    {
        using var document = JsonDocument.Parse(StartupNotificationFrame);

        var notification = ProtocolNotification.From(document.RootElement);

        Assert.False(notification.IsServerRequest);
        Assert.Equal(StartupNotificationMethods.StorageState, notification.Method);
        Assert.NotNull(notification.Params);
        var parameters = notification.Params.Value.Deserialize<StorageStateParams>(ProtocolJson.Options);
        Assert.NotNull(parameters);
        Assert.Equal("ready", parameters.Phase);
    }

    [Fact]
    public void Classify_DistinguishesFrameKinds()
    {
        using var response = JsonDocument.Parse(ErrorEnvelopeFrame);
        using var notification = JsonDocument.Parse(StartupNotificationFrame);
        using var serverRequest = JsonDocument.Parse("""{"id":9,"method":"interaction/requestPermission","params":{}}""");

        Assert.Equal(ProtocolFrameKind.Response, ProtocolFrame.Classify(response.RootElement));
        Assert.Equal(ProtocolFrameKind.Notification, ProtocolFrame.Classify(notification.RootElement));
        Assert.Equal(ProtocolFrameKind.ServerRequest, ProtocolFrame.Classify(serverRequest.RootElement));
    }

    [Theory]
    [InlineData("""{"session":{"sessionId":"sess_nested"}}""", "sess_nested")]
    [InlineData("""{"sessionId":"sess_flat"}""", "sess_flat")]
    public void CreatedSession_ResolvesSessionIdAcrossShapes(string payload, string expected)
    {
        var created = JsonSerializer.Deserialize<CreatedSession>(payload, ProtocolJson.Options);

        Assert.NotNull(created);
        Assert.Equal(expected, created.ResolveSessionId());
    }

    [Fact]
    public void CreatedSession_MissingSessionId_ReturnsNull()
    {
        var created = JsonSerializer.Deserialize<CreatedSession>("""{"other":true}""", ProtocolJson.Options);

        Assert.NotNull(created);
        Assert.Null(created.ResolveSessionId());
    }
}
