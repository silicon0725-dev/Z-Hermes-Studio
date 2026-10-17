using System.Text.Json;
using ZHS.Protocol.Wire;

namespace ZHS.Client;

/// <summary>zcode 协议连接抽象，便于用假连接做单元测试。</summary>
public interface IZcodeProtocolConnection : IAsyncDisposable
{
    /// <summary>发送请求并等待响应。错误响应抛 <see cref="ZcodeProtocolException"/>；无 result 字段时返回 null。</summary>
    Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);

    /// <summary>读取下游通知 / 服务端反向请求流。</summary>
    IAsyncEnumerable<ProtocolNotification> ReadNotificationsAsync(CancellationToken cancellationToken = default);
}

public sealed class ZcodeProtocolException : Exception
{
    public int Code { get; }

    public string? Method { get; }

    public ZcodeProtocolException(int code, string message, string? method = null)
        : base($"[{code}] {message}" + (method is null ? "" : $" (method: {method})"))
    {
        Code = code;
        Method = method;
    }
}

public sealed class ZcodeConnectionClosedException : Exception
{
    public ZcodeConnectionClosedException(string message)
        : base(message)
    {
    }
}
