using ZHS.Chat.Streaming;
using Xunit;

namespace ZHS.Chat.Tests;

/// <summary>开发文档 §7.3 数据路径核心的验收测试：有界、合并、结构化事件不丢、可取消。</summary>
public sealed class MessageStreamPipelineTests
{
    [Fact]
    public async Task BurstOfDeltas_CoalescesIntoFewBatches_TextPreserved()
    {
        await using var pipeline = new MessageStreamPipeline(capacity: 1024);
        var expected = string.Concat(System.Linq.Enumerable.Range(0, 1000).Select(i => $"d{i};"));

        for (var i = 0; i < 1000; i++)
        {
            pipeline.TryWriteDelta($"d{i};");
        }

        pipeline.Complete();
        var batches = await pipeline.ConsumeAsync().ToListAsync();

        var combined = string.Concat(batches.Select(b => b.DeltaText ?? ""));
        Assert.Equal(expected, combined);                       // 文本零丢失（未过载时）
        Assert.True(batches.Count < 100, $"1000 个增量应合并为远少于 100 批，实际 {batches.Count} 批");
        Assert.True(batches.All(b => b.FinalText is null && b.Error is null));
    }

    [Fact]
    public async Task StructuredSignals_NeverMergedWithDeltas_AndAlwaysDelivered()
    {
        await using var pipeline = new MessageStreamPipeline(capacity: 64);

        pipeline.TryWriteDelta("你好");
        await pipeline.WriteSignalAsync(new StreamSignal(StreamSignalKind.TurnCompleted, "你好"));
        pipeline.Complete();

        var batches = await pipeline.ConsumeAsync().ToListAsync();

        // 信号必须单独成批：前面增量一批 + 完成信号一批
        var completed = Assert.Single(batches, b => b.FinalText is not null);
        Assert.Equal("你好", completed.FinalText);
        var delta = Assert.Single(batches, b => b.DeltaText is not null);
        Assert.Equal("你好", delta.DeltaText);
    }

    [Fact]
    public async Task Overflow_DropsDeltasAndCounts_SignalsStillDelivered()
    {
        await using var pipeline = new MessageStreamPipeline(capacity: 8);

        // 不启动消费者，灌满再继续灌：过载增量被丢弃并计数
        for (var i = 0; i < 100; i++)
        {
            pipeline.TryWriteDelta("x");
        }

        Assert.True(pipeline.DroppedDeltas > 0, "过载时应丢弃增量并计数");
        Assert.Equal(100, pipeline.EnqueuedDeltas + pipeline.DroppedDeltas);

        // 信号绝不丢：队列满时 WriteAsync 挂起，等消费者排空腾出空间后送达
        var signalTask = pipeline.WriteSignalAsync(new StreamSignal(StreamSignalKind.TurnFailed, null, "boom"));
        var consumer = Task.Run(async () =>
        {
            var list = new List<StreamBatch>();
            await foreach (var batch in pipeline.ConsumeAsync())
            {
                list.Add(batch);
            }

            return list;
        });

        await signalTask.WaitAsync(TimeSpan.FromSeconds(10));
        pipeline.Complete();
        var batches = await consumer;

        Assert.Contains(batches, b => b.Error == "boom");
    }

    [Fact]
    public async Task Cancellation_StopsConsumption()
    {
        await using var pipeline = new MessageStreamPipeline(capacity: 16);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        pipeline.TryWriteDelta("tick");
        // 不 Complete，等待取消
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ConsumeAsync(cancellation.Token).ToListAsync().AsTask());
    }

    [Fact]
    public async Task FailedSignal_CarriesError()
    {
        await using var pipeline = new MessageStreamPipeline(capacity: 16);
        await pipeline.WriteSignalAsync(new StreamSignal(StreamSignalKind.TurnFailed, null, "model error"));
        pipeline.Complete();

        var batches = await pipeline.ConsumeAsync().ToListAsync();

        var failure = Assert.Single(batches);
        Assert.Equal("model error", failure.Error);
    }
}
