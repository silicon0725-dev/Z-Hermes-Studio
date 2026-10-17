using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZHS.Domain.Artifacts;
using ZHS.Domain.Tasks;
using ZHS.Persistence;
using ZHS.Presentation.Artifacts;
using ZHS.Presentation;
using ZHS.Presentation.Chat;
using ZHS.Presentation.Tasks;
using ZHS.RuntimeAdapter.ZCode;

namespace ZHS.Desktop;

public class App : Application
{
    private MainViewModel? _main;
    private TasksViewModel? _tasks;
    private ArtifactsViewModel? _artifacts;
    private ZhsDatabase? _database;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var database = OpenDatabase();
            IAppSettings? settings = database is not null ? new SettingsStore(database) : null;
            var backend = ZcodeChatBackend.LaunchDefault();

            var artifactEngine = database is not null ? new ArtifactEngine(new ArtifactSqliteStore(database)) : null;
            var main = new MainViewModel(
                backend,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                settings,
                artifactEngine);
            _main = main;

            if (database is not null)
            {
                var store = new TaskSqliteStore(database);
                var engine = new TaskEngine(store, store, new SingleSessionTaskExecutor(backend.Runtime));
                _tasks = new TasksViewModel(engine, action => Avalonia.Threading.Dispatcher.UIThread.Post(action), () => main.WorkspacePath);
                _ = _tasks.RefreshAsync();
            }

            if (artifactEngine is not null)
            {
                _artifacts = new ArtifactsViewModel(artifactEngine, action => Avalonia.Threading.Dispatcher.UIThread.Post(action), () => main.WorkspacePath);
                _ = _artifacts.RefreshAsync();
            }

            if (_artifacts is not null)
            {
                _artifacts.TypeFilter = main.ArtifactTypeFilter;
                main.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName == nameof(MainViewModel.ArtifactTypeFilter))
                    {
                        _artifacts.TypeFilter = main.ArtifactTypeFilter;
                        _ = _artifacts.RefreshAsync();
                    }
                };
            }

            desktop.MainWindow = new MainWindow { DataContext = main, Tasks = _tasks, Artifacts = _artifacts };
            desktop.Exit += async (_, _) => await DisposeAsync();
            _ = main.InitializeAsync();
            _ = main.LoadSessionsAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>ZHS 自有库（~/.zhs/settings.sqlite）。打开失败降级为无持久化，不阻塞启动。</summary>
    private ZhsDatabase? OpenDatabase()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zhs");
            Directory.CreateDirectory(directory);
            var database = ZhsDatabase.OpenAsync(Path.Combine(directory, "settings.sqlite")).GetAwaiter().GetResult();
            _database = database;
            return database;
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
