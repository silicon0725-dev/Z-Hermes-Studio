namespace ZHS.Client;

/// <summary>zcode app-server 子进程配置。默认路径对应本机 /opt/ZCode（deb 安装，v3.15.3）。</summary>
public sealed record ZcodeAppServerOptions
{
    /// <summary>宿主可执行文件。Electron 分发下用 zcode 二进制 + ELECTRON_RUN_AS_NODE=1 当 Node 用。</summary>
    public string ExecutablePath { get; init; } = "/opt/ZCode/zcode";

    /// <summary>Agent CLI bundle（15MB 的 zcode.cjs）。</summary>
    public string BundlePath { get; init; } = "/opt/ZCode/resources/glm/zcode.cjs";

    /// <summary>固定为 ["app-server", "--stdio"]（上游桌面启动器同款 spawnArgs）。</summary>
    public IReadOnlyList<string> ServerArgs { get; init; } = ["app-server", "--stdio"];

    /// <summary>子进程工作目录。会话 workspace 与此无关；用临时目录避免误读仓库配置。</summary>
    public string? WorkingDirectory { get; init; } = null;

    /// <summary>单请求超时。session/create 冷启动可能到 10s 级，默认放宽。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>附加环境变量（ELECTRON_RUN_AS_NODE=1 由连接自动注入）。</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; } = null;
}
