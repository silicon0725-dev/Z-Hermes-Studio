using System.Text.Json;
using ZHS.Client;
using ZHS.Protocol.Wire;

namespace ZHS.RuntimeAdapter.ZCode.Tests;

public sealed class FakeProtocolConnection : IZcodeProtocolConnection
{
    private readonly Queue<JsonElement> _scriptedResults = new();
    private readonly Dictionary<string, Queue<JsonElement>> _scriptedByMethod = new();

    public List<(string Method, string ParamsJson)> Requests { get; } = new();

    /// <summary>按需计算响应的兜底处理器（优先级低于按方法编排的队列）。</summary>
    public Func<string, string?, JsonElement?>? Handler { get; set; }

    public FakeProtocolConnection EnqueueResult(object result)
    {
        _scriptedResults.Enqueue(JsonSerializer.SerializeToElement(result, ProtocolJson.Options));
        return this;
    }

    public FakeProtocolConnection EnqueueRaw(string json)
    {
        _scriptedResults.Enqueue(JsonSerializer.SerializeToElement(JsonDocument.Parse(json).RootElement.Clone(), ProtocolJson.Options));
        return this;
    }

    /// <summary>为指定方法编排一个响应（同一方法多次调用按队列顺序返回）。</summary>
    public FakeProtocolConnection EnqueueFor(string method, object result)
    {
        if (!_scriptedByMethod.TryGetValue(method, out var queue))
        {
            queue = new Queue<JsonElement>();
            _scriptedByMethod[method] = queue;
        }

        queue.Enqueue(JsonSerializer.SerializeToElement(result, ProtocolJson.Options));
        return this;
    }

    public Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        Requests.Add((method, JsonSerializer.Serialize(parameters, ProtocolJson.Options)));
        if (_scriptedByMethod.TryGetValue(method, out var queue) && queue.Count > 0)
        {
            return Task.FromResult<JsonElement?>(queue.Dequeue().Clone());
        }

        if (_scriptedResults.Count > 0)
        {
            return Task.FromResult<JsonElement?>(_scriptedResults.Dequeue().Clone());
        }

        if (Handler is not null)
        {
            return Task.FromResult(Handler(method, Requests[^1].ParamsJson));
        }

        throw new InvalidOperationException($"no scripted response for '{method}'");
    }

    public async IAsyncEnumerable<ProtocolNotification> ReadNotificationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
