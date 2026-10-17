using System.Threading.Channels;

namespace ZHS.Chat.Streaming;

/// <summary>结构化信号：不得被丢弃、不得与文本增量合并（开发文档 §7.3）。</summary>
public enum StreamSignalKind
{
    TurnCompleted,
    TurnFailed,
}

public sealed record StreamSignal(StreamSignalKind Kind, string? Text, string? Error = null);

/// <summary>一次 UI 提交批次：连续文本增量已合并；FinalText/Error 为结构化信号。</summary>
public sealed record StreamBatch(string? DeltaText, string? FinalText, string? Error);

/// <summary>
/// 有界流式事件管道（开发文档 §7.3 数据路径的核心：Runtime → 解码 → 有界队列 → 合并器 → UI 调度）。
/// 策略：文本增量 TryWrite，队列满即丢弃并计数（最终文本由回合结果权威覆盖）；
/// 结构化信号 WriteAsync 阻塞等待，绝不丢失；消费者按批排空，连续增量合并为单个批次。
/// </summary>
public sealed class MessageStreamPipeline : IAsyncDisposable
{
    private readonly Channel<StreamItem> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private long _droppedDeltas;
    private long _enqueuedDeltas;
    private long _mergedDeltas;
    private long _batches;
    private int _maxObservedDepth;

    public MessageStreamPipeline(int capacity = 512)
    {
        _channel = Channel.CreateBounded<StreamItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long DroppedDeltas => _droppedDeltas;

    public long EnqueuedDeltas => _enqueuedDeltas;

    public long MergedDeltas => _mergedDeltas;

    public long Batches => _batches;

    public int MaxObservedDepth => _maxObservedDepth;

    /// <summary>写入文本增量。队列满时丢弃并计数（增量可丢：最终文本权威）。</summary>
    public bool TryWriteDelta(string text)
    {
        if (_channel.Writer.TryWrite(new StreamItem(text)))
        {
            _enqueuedDeltas++;
            return true;
        }

        _droppedDeltas++;
        return false;
    }

    /// <summary>写入结构化信号。队列满时阻塞等待，绝不丢弃。</summary>
    public async Task WriteSignalAsync(StreamSignal signal, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(new StreamItem(null, signal), cancellationToken);
    }

    /// <summary>生产端完成（消费端排空后枚举结束）。</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// 消费端：按批排空并合并。一批 = 当前积压的全部连续文本增量（合并为一段），
    /// 遇结构化信号单独成批。批与批之间让出，天然形成 UI 提交节奏。
    /// </summary>
    public async IAsyncEnumerable<StreamBatch> ConsumeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = new System.Text.StringBuilder();
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            // 一次排空当前积压（非阻塞读尽）
            while (_channel.Reader.TryRead(out var item))
            {
                if (item.Signal is { } signal)
                {
                    if (merged.Length > 0)
                    {
                        _batches++;
                        yield return new StreamBatch(merged.ToString(), null, null);
                        merged.Clear();
                    }

                    _batches++;
                    yield return signal.Kind switch
                    {
                        StreamSignalKind.TurnCompleted => new StreamBatch(null, signal.Text, null),
                        StreamSignalKind.TurnFailed => new StreamBatch(null, null, signal.Error ?? "turn failed"),
                        _ => new StreamBatch(null, null, signal.Error),
                    };
                }
                else
                {
                    _mergedDeltas++;
                    merged.Append(item.Text);
                }
            }

            if (merged.Length > 0)
            {
                _batches++;
                TrackDepth();
                yield return new StreamBatch(merged.ToString(), null, null);
                merged.Clear();
            }
        }
    }

    private void TrackDepth()
    {
        // 深度观测：合并批次数与积压规模相关，用近似值记录峰值（受 Channel 内部实现限制）
        var depth = (int)(_enqueuedDeltas - _droppedDeltas - _mergedDeltas);
        if (depth > _maxObservedDepth)
        {
            _maxObservedDepth = depth;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        _shutdown.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct StreamItem(string? Text, StreamSignal? Signal = null);
}
