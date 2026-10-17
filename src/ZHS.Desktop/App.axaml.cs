using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZHS.Persistence;
using ZHS.Presentation;
using ZHS.Presentation.Chat;
using ZHS.RuntimeAdapter.ZCode;

namespace ZHS.Desktop;

public class App : Application
{
    private MainViewModel? _main;
    private ZhsDatabase? _database;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = OpenSettings();
            var main = new MainViewModel(
                ZcodeChatBackend.LaunchDefault(),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                settings);
            _main = main;
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.Exit += async (_, _) => await DisposeAsync();
            _ = main.InitializeAsync();
            _ = main.LoadSessionsAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>ZHS 自有设置库（~/.zhs/settings.sqlite；打开失败降级为无持久化，不阻塞启动）。</summary>
    private IAppSettings? OpenSettings()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zhs");
            Directory.CreateDirectory(directory);
            var database = ZhsDatabase.OpenAsync(Path.Combine(directory, "settings.sqlite")).GetAwaiter().GetResult();
            _database = database;
            return new SettingsStore(database);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task DisposeAsync()
    {
        if (_main is not null)
        {
            await _main.DisposeAsync();
            _main = null;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }
}
