using System.Text.Json;

namespace ZHS.Protocol.Wire;

/// <summary>
/// JSON-RPC 2.0 形态 over NDJSON —— 上游 <c>zcode app-server --stdio</c> 的线协议。
/// 信封与错误码经 v3.15.3 活体探针验证（见 docs/M0-SOURCE-INVENTORY.md §2.7），
/// schema 定义对应上游 v3.15.1 源码 packages/shared/src/zcode-protocol*。
/// </summary>
public static class JsonRpcErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    /// <summary>实测：未知方法返回此码（"Method not found: bogus/method"）。</summary>
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

public enum ProtocolFrameKind
{
    /// <summary>含 id 与 result/error：对已发请求的响应。</summary>
    Response,

    /// <summary>含 id 与 method：服务端反向请求（如 interaction/requestPermission），需要客户端应答。</summary>
    ServerRequest,

    /// <summary>只含 method：单向通知（如 startup/storageState）。</summary>
    Notification,
}

public static class ProtocolFrame
{
    public static ProtocolFrameKind Classify(JsonElement element)
    {
        bool hasId = element.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        bool hasMethod = element.TryGetProperty("method", out _);
        return (hasId, hasMethod) switch
        {
            (true, false) => ProtocolFrameKind.Response,
            (true, true) => ProtocolFrameKind.ServerRequest,
            _ => ProtocolFrameKind.Notification,
        };
    }
}

/// <summary>非响应帧的统一视图（通知或服务端反向请求）。Params 保留原始 JsonElement，按 Method 惰性解析。</summary>
public sealed record ProtocolNotification(string? Method, JsonElement? Params, JsonElement? Id)
{
    public bool IsServerRequest => Id is not null;

    public static ProtocolNotification From(JsonElement element)
    {
        string? method = null;
        if (element.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
        {
            method = m.GetString();
        }

        JsonElement? parameters = null;
        if (element.TryGetProperty("params", out var p) && p.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            parameters = p.Clone();
        }

        JsonElement? id = null;
        if (element.TryGetProperty("id", out var i) && i.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            id = i.Clone();
        }

        return new ProtocolNotification(method, parameters, id);
    }
}
