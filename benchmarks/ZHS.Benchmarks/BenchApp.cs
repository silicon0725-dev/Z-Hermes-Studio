using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace ZHS.Benchmarks;

/// <summary>基准专用最小 App：仅 Fluent 主题，不启动任何后端（ADR-0003：Avalonia.Headless）。</summary>
public class BenchApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window(); // 占位，真实窗口由基准流程创建
        }

        base.OnFrameworkInitializationCompleted();
    }
}
