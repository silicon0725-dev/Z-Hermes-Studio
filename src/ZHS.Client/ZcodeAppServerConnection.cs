using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZHS.Protocol.Wire;

namespace ZHS.Client;

/// <summary>
/// <c>zcode app-server --stdio</c> 子进程连接：NDJSON 帧解析、请求-响应关联、通知流分发。
/// 进程模型与生命周期规则参照 ZCAC ProtocolAgentExecutor 的实测经验（见盘点报告 §3.5）。
/// </summary>
public sealed class ZcodeAppServerConnection : IZcodeProtocolConnection
{
    private readonly ZcodeAppServerOptions _options;
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly Channel<ProtocolNotification> _notifications;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StringBuilder _stderrTail = new(8 * 1024);
    private long _nextId;

    public int ReceivedNotificationCount { get; private set; }

    public string LastStderr => _stderrTail.ToString();

    public bool HasExited => _process.HasExited;

    /// <summary>诊断/测试用：立即杀死子进程（模拟上游崩溃，验证断连处理）。</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    private ZcodeAppServerConnection(ZcodeAppServerOptions options, Process process, StreamWriter stdin, Channel<ProtocolNotification> notifications)
    {
        _options = options;
        _process = process;
        _stdin = stdin;
        _notifications = notifications;
    }

    public static ZcodeAppServerConnection Start(ZcodeAppServerOptions? options = null)
    {
        options ??= new ZcodeAppServerOptions();
        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException($"zcode executable not found: {options.ExecutablePath}", options.ExecutablePath);
        }

        if (!File.Exists(options.BundlePath))
        {
            throw new FileNotFoundException($"zcode CLI bundle not found: {options.BundlePath}", options.BundlePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Path.GetTempPath(),
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(options.BundlePath);
        foreach (var arg in options.ServerArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        if (options.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in options.ExtraEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to spawn zcode app-server");
        var notifications = Channel.CreateBounded<ProtocolNotification>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var connection = new ZcodeAppServerConnection(options, process, process.StandardInput, notifications);
        _ = connection.PumpStdoutAsync();
        _ = connection.PumpStderrAsync();
        return connection;
    }

    public async Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"duplicate request id {id}");
        }

        var frame = NdjsonCodec.EncodeRequest(id, method, parameters);
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"zcode RPC '{method}' timed out after {_options.RequestTimeout.TotalSeconds:0.#}s");
        }
        catch (OperationCanceledException) when (_process.HasExited)
        {
            throw new ZcodeConnectionClosedException($"zcode app-server exited while waiting for '{method}': {LastStderr}");
        }
    }

    public IAsyncEnumerable<ProtocolNotification> ReadNotificationsAsync(CancellationToken cancellationToken = default)
    {
        return _notifications.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // 进程已退出或 Kill 竞态：忽略
        }

        FailPending(new ZcodeConnectionClosedException("connection disposed"));
        _notifications.Writer.TryComplete();
        _process.Dispose();
        _writeGate.Dispose();
        _shutdown.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_shutdown.Token);
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

                Dispatch(document);
            }
        }
        catch (OperationCanceledException)
        {
        }

        FailPending(new ZcodeConnectionClosedException($"zcode app-server stdout closed: {LastStderr}"));
        _notifications.Writer.TryComplete();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_shutdown.Token);
                if (line is null)
                {
                    break;
                }

                if (_stderrTail.Length > 0)
                {
                    _stderrTail.AppendLine();
                }

                _stderrTail.AppendLine(line);
                if (_stderrTail.Length > 8 * 1024)
                {
                    _stderrTail.Remove(0, _stderrTail.Length - 8 * 1024);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Dispatch(JsonDocument document)
    {
        using (document)
        {
            var root = document.RootElement;
            if (ProtocolFrame.Classify(root) == ProtocolFrameKind.Response)
            {
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    var id = idElement.GetInt64();
                    if (_pending.TryRemove(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        {
                            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "unknown error";
                            completion.SetException(new ZcodeProtocolException(code, message ?? "unknown error"));
                        }
                        else
                        {
                            JsonElement? result = null;
                            if (root.TryGetProperty("result", out var r) && r.ValueKind is not JsonValueKind.Undefined)
                            {
                                result = r.Clone();
                            }

                            completion.SetResult(result);
                        }
                    }
                }

                return;
            }

            ReceivedNotificationCount++;
            var notification = ProtocolNotification.From(root);
            if (notification.IsServerRequest)
            {
                // 服务端反向请求必须应答（id 为字符串如 "server-1"，原样回显）。
                // ZCAC 实测：interaction/requestOfficialMcpAuthHeaders 回 -32601 会让 agent 进程退出。
                RespondToServerRequest(notification);
            }

            _notifications.Writer.TryWrite(notification);
        }
    }

    private void RespondToServerRequest(ProtocolNotification request)
    {
        if (request.Id is null || request.Method is null)
        {
            return;
        }

        var answer = AnswerServerRequest(request.Method);
        var frame = answer is not null
            ? NdjsonCodec.EncodeResponse(request.Id.Value, answer, null)
            : NdjsonCodec.EncodeResponse(request.Id.Value, null, (-32601, $"method not handled by ZHS host: {request.Method}"));
        try
        {
            _writeGate.Wait();
            try
            {
                _stdin.WriteLine(frame);
                _stdin.Flush();
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception)
        {
            // 进程已退出：响应写不进去由断连处理兜底
        }
    }

    /// <summary>
    /// 服务端反向请求的默认应答策略（安全默认，与 ZCAC 宿主一致）。
    /// 后续权限审批（interaction/requestPermission）在此接入 Policy Engine。
    /// </summary>
    private static object? AnswerServerRequest(string method)
    {
        return method switch
        {
            "session/requestRuntimePreferences" => new
            {
                nativeSearchEnhancementsEnabled = true,
                memoryEnabled = false,
                askUserQuestionAutoResolutionEnabled = true,
            },
            "interaction/requestOfficialMcpAuthHeaders" => new Dictionary<string, object>
            {
                ["headers"] = new Dictionary<string, string>(),
            },
            _ => null,
        };
    }

    private void FailPending(Exception exception)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }
}
