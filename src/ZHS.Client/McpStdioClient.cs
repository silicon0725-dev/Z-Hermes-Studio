using System.Diagnostics;
using System.Text.Json;
using ZHS.Protocol.Wire;

namespace ZHS.Client;

/// <summary>MCP stdio 客户端（JSON-RPC 2.0 over NDJSON）。ZCAC 编排器（ADR-0002 L2）及未来任何 stdio MCP server 通用。</summary>
public interface IMcpClient : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>调用工具并解析 content[0].text（ZCAC 约定 text 为 JSON 字符串；非 JSON 原样返回字符串元素）。</summary>
    Task<JsonElement> CallToolAsync(string toolName, object? arguments, CancellationToken cancellationToken = default);
}

public sealed record McpStdioOptions
{
    public string ExecutablePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string ClientName { get; init; } = "zhs";

    public string ClientVersion { get; init; } = "0.1.0";
}

public sealed class McpStdioClient : IMcpClient
{
    public const string ProtocolVersion = "2024-11-05";

    private readonly McpStdioOptions _options;
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _pendingGate = new();
    private long _nextId;
    private bool _initialized;

    public McpStdioClient(McpStdioOptions options)
    {
        _options = options;
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Path.GetTempPath(),
        };
        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to spawn MCP server: {options.ExecutablePath}");
        _stdin = _process.StandardInput;
        _ = PumpStdoutAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                while (await _process.StandardError.ReadAsync(buffer) > 0)
                {
                    // stderr 仅保活读取，避免子进程阻塞
                }
            }
            catch (Exception)
            {
            }
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = _options.ClientName, version = _options.ClientVersion },
            },
            cancellationToken);
        Notify("notifications/initialized");
        _initialized = true;
        _ = result;
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/list", null, cancellationToken);
        var names = new List<string>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    names.Add(name.GetString()!);
                }
            }
        }

        return names;
    }

    public async Task<JsonElement> CallToolAsync(string toolName, object? arguments, CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("MCP client not initialized");
        }

        var result = await RequestAsync("tools/call", new { name = toolName, arguments }, cancellationToken);
        var text = ExtractText(result);
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.Clone();
        }

        return JsonSerializer.SerializeToElement(text);
    }

    private static string ExtractText(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && item.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        return result.GetRawText();
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending[id] = completion;
        }

        // MCP 严格要求每条消息带 jsonrpc:"2.0"（zcode app-server 宽容，NdjsonCodec 不带该字段）
        await WriteAsync(EncodeMcpRequest(id, method, parameters), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP '{method}' timed out after {_options.RequestTimeout.TotalSeconds:0.#}s");
        }
    }

    private async Task WriteAsync(string frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _stdin.WriteLineAsync(frame.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string EncodeMcpRequest(long id, string method, object? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, ProtocolJson.Options);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Notify(string method)
    {
        try
        {
            _writeGate.Wait();
            try
            {
                _stdin.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }));
                _stdin.Flush();
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception)
        {
            // 通知写失败由断连兜底
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            var buffer = string.Empty;
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                        && root.TryGetProperty("result", out var result))
                    {
                        var id = idElement.GetInt64();
                        TaskCompletionSource<JsonElement>? completion = null;
                        lock (_pendingGate)
                        {
                            if (_pending.Remove(id, out completion) && root.TryGetProperty("error", out var error))
                            {
                                var message = error.TryGetProperty("message", out var m) ? m.GetString() : "mcp error";
                                completion.SetException(new InvalidOperationException($"MCP error: {message}"));
                                completion = null;
                            }
                        }

                        completion?.SetResult(result.Clone());
                    }
                    // MCP 通知与 server→client 请求当前不处理（ZCAC 编排器不发送）
                }
            }
        }
        catch (Exception)
        {
        }

        lock (_pendingGate)
        {
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new ZcodeConnectionClosedException("MCP server stdout closed"));
            }

            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }

        _process.Dispose();
        _writeGate.Dispose();
        await ValueTask.CompletedTask;
    }
}
