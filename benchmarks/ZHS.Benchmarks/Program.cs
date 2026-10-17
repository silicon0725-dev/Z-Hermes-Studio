using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZHS.Benchmarks;
using ZHS.Chat.Streaming;
using ZHS.Presentation.Chat;

// M2 基准（ADR-0003 口径）：Avalonia.Headless 驱动真实 MainWindow（真实模板 + 虚拟化列表）。
// 场景：A 首屏可交互 / B 批量追加批次布局分布 / C 跳页滚动布局分布 / P 管道吞吐。
// 记录 P50/P95/P99、分配字节、Gen0/1/2 GC 次数；原始样本落盘 JSON（禁止只留平均值）。

internal static class Program
{
    private const int PageSize = 60;

    [STAThread]
    private static void Main()
    {
        var startedAt = DateTime.UtcNow;
        var session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(typeof(BenchApp));

        // 所有 UI 场景在 Dispatch 内于 UI 线程执行；Dispatch 是排队式，需显式等待完成
        var scenarios = new List<Scenario>();
        var done = new ManualResetEventSlim();
        session.Dispatch(() =>
        {
            try
            {
                Warmup();
                scenarios.Add(PipelineThroughput());
                foreach (var size in new[] { 1_000, 10_000, 50_000 })
                {
                    scenarios.AddRange(TimelineScenarios(size));
                }
            }
            finally
            {
                done.Set();
            }
        }, CancellationToken.None);
        if (!done.Wait(TimeSpan.FromMinutes(5)))
        {
            Console.WriteLine("基准 UI 会话 5 分钟未完成，中止。");
            return;
        }

        var report = new Report
        {
            Environment = $"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, .NET {Environment.Version}, Avalonia 11.3.2, headless",
            StartedAtUtc = startedAt,
            TotalSeconds = (DateTime.UtcNow - startedAt).TotalSeconds,
        };
        foreach (var scenario in scenarios)
        {
            report.Scenarios.Add(scenario);
        }

        Print(report);
        Directory.CreateDirectory(Path.Combine(FindRepoRoot(), "benchmarks", "results"));
        var path = Path.Combine(FindRepoRoot(), "benchmarks", "results", $"run-{startedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"\n原始样本已写入：{path}");
    }

    private static Scenario PipelineThroughput()
    {
        const int total = 50_000;
        var pipeline = new MessageStreamPipeline(capacity: 512);
        var consumer = Task.Run(async () =>
        {
            var sink = 0L;
            await foreach (var batch in pipeline.ConsumeAsync())
            {
                sink += batch.DeltaText?.Length ?? 0;
            }

            return sink;
        });

        var gc0 = SnapshotGc();
        var watch = Stopwatch.StartNew();
        var written = 0L;
        var payload = new string('流', 8);
        for (var i = 0; i < total; i++)
        {
            if (pipeline.TryWriteDelta(payload))
            {
                written += payload.Length;
            }
        }

        pipeline.Complete();
        var consumed = consumer.Result;
        watch.Stop();
        var gc1 = SnapshotGc();
        pipeline.DisposeAsync().AsTask().Wait();

        return new Scenario
        {
            Name = $"P 管道吞吐（{total} 增量 / 容量 512）",
            Metrics =
            [
                new Metric("耗时", $"{watch.Elapsed.TotalMilliseconds:0.#} ms"),
                new Metric("吞吐", $"{total / watch.Elapsed.TotalMilliseconds:0.#} 增量/ms"),
                new Metric("写入字符", written.ToString("N0")),
                new Metric("消费字符", consumed.ToString("N0")),
                new Metric("合并后批次数", pipeline.Batches.ToString("N0")),
                new Metric("丢弃增量", pipeline.DroppedDeltas.ToString("N0")),
                new Metric("Gen0/1/2 Δ", gc1.Diff(gc0)),
            ],
            Samples = [watch.Elapsed.TotalMilliseconds],
        };
    }

    /// <summary>预热一轮（JIT、主题、模板实例化），避免污染 A 场景的冷启动测量。</summary>
    private static void Warmup()
    {
        var window = new ZHS.Desktop.MainWindow
        {
            DataContext = new MainViewModel(new NullBackend(), _ => { }),
            Width = 800,
            Height = 600,
            ShowInTaskbar = false,
        };
        var chat = new ChatViewModel(_ => { }, "warmup", (_, _, _) => Task.FromResult(new HistoryPage([], HasMore: false)));
        ((MainViewModel)window.DataContext!).CurrentChat = chat;
        window.Show();
        foreach (var message in MessageDataset.Generate(PageSize))
        {
            chat.Messages.Add(message);
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }

    private static List<Scenario> TimelineScenarios(int size)
    {
        var window = new ZHS.Desktop.MainWindow
        {
            DataContext = new MainViewModel(new NullBackend(), _ => { }),
            Width = 1100,
            Height = 720,
            ShowInTaskbar = false,
        };
        var main = (MainViewModel)window.DataContext!;
        var chat = new ChatViewModel(_ => { }, $"bench_{size}", (_, _, _) => Task.FromResult(new HistoryPage([], HasMore: false)));
        main.CurrentChat = chat;

        var dataset = MessageDataset.Generate(size);
        var scenarios = new List<Scenario>();

        // ── A 首屏可交互：Show + 首页 60 条完成布局（热缓存口径，每档 10 次取分位） ──
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // 消息列表是窗口里第二个 ListBox（第一个是左侧会话列表）
        var messagesList = window.GetVisualDescendants().OfType<ListBox>().ElementAtOrDefault(1);
        var scroller = messagesList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var firstPage = dataset.Take(PageSize).ToList();
        var gcA0 = SnapshotGc();
        var firstScreenSamples = new List<double>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            chat.Messages.Clear();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var watch = Stopwatch.StartNew();
            foreach (var message in firstPage)
            {
                chat.Messages.Add(message);
            }

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            watch.Stop();
            firstScreenSamples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var gcA1 = SnapshotGc();
        scenarios.Add(new Scenario
        {
            Name = $"A 首屏可交互（{size:N0} 数据集，首页 {PageSize} 条，×30）",
            Metrics =
            [
                new Metric("P50 / P95 / P99", Percentiles(firstScreenSamples)),
                new Metric("最差", $"{firstScreenSamples.Max():0.#} ms（目标 P95 ≤ 500）"),
                new Metric("Gen0/1/2 Δ", gcA1.Diff(gcA0)),
                new Metric("分配字节/次", $"{(gcA1.Allocated - gcA0.Allocated) / 10:N0}"),
            ],
            Samples = firstScreenSamples,
        });

        // ── B 批量追加：每批 60 条的布局耗时分布（至 size 条） ──
        var gcB0 = SnapshotGc();
        var batchSamples = new List<double>();
        for (var offset = PageSize; offset < size; offset += PageSize)
        {
            var batchWatch = Stopwatch.StartNew();
            foreach (var message in dataset.Skip(offset).Take(PageSize))
            {
                chat.Messages.Add(message);
            }

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            batchWatch.Stop();
            batchSamples.Add(batchWatch.Elapsed.TotalMilliseconds);
        }

        var gcB1 = SnapshotGc();
        scenarios.Add(new Scenario
        {
            Name = $"B 批量追加（至 {size:N0} 条，批大小 {PageSize}）",
            Metrics =
            [
                new Metric("批次数", batchSamples.Count.ToString()),
                new Metric("P50 / P95 / P99", Percentiles(batchSamples)),
                new Metric("最差批", batchSamples.Count > 0 ? $"{batchSamples.Max():0.#} ms" : "n/a"),
                new Metric("Gen0/1/2 Δ", gcB1.Diff(gcB0)),
            ],
            Samples = batchSamples,
        });

        // ── C 跳页滚动：Extent 中段往返跳转的布局耗时分布 ──
        if (scroller is not null)
        {
            var scrollSamples = new List<double>();
            var midpoint = scroller.Extent.Height / 2;
            for (var jump = 0; jump < 20; jump++)
            {
                var scrollWatch = Stopwatch.StartNew();
                scroller.Offset = new Avalonia.Vector(0, jump % 2 == 0 ? midpoint : scroller.Extent.Height - scroller.Viewport.Height);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                scrollWatch.Stop();
                scrollSamples.Add(scrollWatch.Elapsed.TotalMilliseconds);
            }

            scenarios.Add(new Scenario
            {
                Name = $"C 跳页滚动（{size:N0} 条，中段↔底部 ×20）",
                Metrics =
                [
                    new Metric("P50 / P95 / P99", Percentiles(scrollSamples)),
                    new Metric("最差跳转", $"{scrollSamples.Max():0.#} ms"),
                ],
                Samples = scrollSamples,
            });
        }

        window.Close();
        return scenarios;
    }

    private static GcSnapshot SnapshotGc()
        => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes(precise: false));

    private readonly record struct GcSnapshot(int Gen0, int Gen1, int Gen2, long Allocated)
    {
        public string Diff(GcSnapshot before)
            => $"{Gen0 - before.Gen0}/{Gen1 - before.Gen1}/{Gen2 - before.Gen2}";
    }

    private static string Percentiles(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return "n/a";
        }

        var ordered = samples.OrderBy(value => value).ToList();
        double At(double percentile) => ordered[Math.Min(ordered.Count - 1, (int)Math.Ceiling(percentile / 100.0 * ordered.Count) - 1)];
        return $"{At(50):0.#} / {At(95):0.#} / {At(99):0.#} ms";
    }

    private static void Print(Report report)
    {
        Console.WriteLine($"# ZHS M2 基准首轮 · {report.Environment}");
        Console.WriteLine($"总耗时 {report.TotalSeconds:0.#} s\n");
        foreach (var scenario in report.Scenarios)
        {
            Console.WriteLine($"## {scenario.Name}");
            foreach (var metric in scenario.Metrics)
            {
                Console.WriteLine($"- {metric.Name}: {metric.Value}");
            }

            Console.WriteLine();
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZHS.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed class NullBackend : IChatBackend
    {
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<IChatSession> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("bench");

        public Task<IChatSession> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("bench");

        public Task<HistoryPage> ReadSessionHistoryAsync(string sessionId, long? beforeRowId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new HistoryPage([], HasMore: false));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed record Report
{
    public string Environment { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    public double TotalSeconds { get; set; }

    public List<Scenario> Scenarios { get; } = [];
}

public sealed record Scenario
{
    public string Name { get; init; } = string.Empty;

    public List<Metric> Metrics { get; init; } = [];

    public List<double> Samples { get; init; } = [];
}

public sealed record Metric(string Name, string Value);
