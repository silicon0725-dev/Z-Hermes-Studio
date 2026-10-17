namespace ZHS.Presentation;

/// <summary>应用设置的最小 KV 抽象（Desktop 用 ZHS.Persistence 实现；测试注入内存版）。</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);
}

public sealed class InMemoryAppSettings : IAppSettings
{
    private readonly Dictionary<string, string> _values = [];

    public Task<string?> GetAsync(string key)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}
