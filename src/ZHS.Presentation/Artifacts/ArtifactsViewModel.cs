using System.Collections.ObjectModel;
using ZHS.Domain.Artifacts;
using ZHS.Presentation.Chat;

namespace ZHS.Presentation.Artifacts;

public sealed class ArtifactItemViewModel
{
    public string ArtifactId { get; }

    public string Title { get; }

    public string Type { get; }

    public string Status { get; }

    public int Version { get; }

    public string SourceSummary { get; }

    public ArtifactItemViewModel(Artifact artifact)
    {
        ArtifactId = artifact.ArtifactId;
        Title = artifact.Title;
        Type = artifact.Type;
        Status = artifact.Status;
        Version = artifact.CurrentVersion;
        SourceSummary = artifact.SourceRefs.Count == 0 ? "—" : string.Join(" · ", artifact.SourceRefs.Take(2));
    }

    public string Display => $"{Title}（{Type} · {Status} · v{Version}）";
}

public sealed class ArtifactVersionItem
{
    public int Version { get; }

    public string CreatedAtText { get; }

    public string SourceSummary { get; }

    public ArtifactVersionItem(ArtifactVersion version)
    {
        Version = version.Version;
        CreatedAtText = DateTimeOffset.FromUnixTimeMilliseconds(version.CreatedAt).LocalDateTime.ToString("MM-dd HH:mm:ss");
        SourceSummary = version.SourceRefs.Count == 0 ? "" : string.Join(" · ", version.SourceRefs);
    }
}

/// <summary>M4 产物面板：按当前工作目录的项目列出/检索产物，查看内容与版本历史，恢复旧版。</summary>
public sealed class ArtifactsViewModel : ObservableObject
{
    private readonly ArtifactEngine _engine;
    private readonly UiPost _uiPost;
    private readonly Func<string> _workspaceProvider;
    private ArtifactItemViewModel? _selectedArtifact;
    private ArtifactVersionItem? _selectedVersion;
    private string _searchText = string.Empty;
    private string _contentText = string.Empty;
    private string _statusText = "产物就绪";

    public ObservableCollection<ArtifactItemViewModel> Items { get; } = [];

    public ObservableCollection<ArtifactVersionItem> Versions { get; } = [];

    public ArtifactsViewModel(ArtifactEngine engine, UiPost uiPost, Func<string> workspaceProvider)
    {
        _engine = engine;
        _uiPost = uiPost;
        _workspaceProvider = workspaceProvider;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        RestoreSelectedVersionCommand = new AsyncRelayCommand(RestoreSelectedVersionAsync);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand RestoreSelectedVersionCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public ArtifactItemViewModel? SelectedArtifact
    {
        get => _selectedArtifact;
        set
        {
            if (SetProperty(ref _selectedArtifact, value) && value is not null)
            {
                _ = LoadDetailAsync(value.ArtifactId);
            }
        }
    }

    public ArtifactVersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set => SetProperty(ref _selectedVersion, value);
    }

    public string ContentText
    {
        get => _contentText;
        private set => SetProperty(ref _contentText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var workspace = _workspaceProvider();
            var project = await _engine.GetOrCreateProjectAsync(workspace);
            var artifacts = await _engine.ListByProjectAsync(project.ProjectId);
            _uiPost(() =>
            {
                Items.Clear();
                foreach (var artifact in artifacts)
                {
                    Items.Add(new ArtifactItemViewModel(artifact));
                }

                StatusText = $"项目 {project.Name}：{artifacts.Count} 个产物";
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"产物加载失败：{exception.Message}");
        }
    }

    public async Task SearchAsync()
    {
        var keyword = SearchText?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            await RefreshAsync();
            return;
        }

        try
        {
            var hits = await _engine.SearchAsync(keyword);
            _uiPost(() =>
            {
                Items.Clear();
                foreach (var artifact in hits)
                {
                    Items.Add(new ArtifactItemViewModel(artifact));
                }

                StatusText = $"检索“{keyword}”：{hits.Count} 个命中";
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"检索失败：{exception.Message}");
        }
    }

    private async Task LoadDetailAsync(string artifactId)
    {
        try
        {
            var content = await _engine.ReadAsync(artifactId);
            var versions = await _engine.ListVersionsAsync(artifactId);
            _uiPost(() =>
            {
                ContentText = content.Content;
                Versions.Clear();
                foreach (var version in versions)
                {
                    Versions.Add(new ArtifactVersionItem(version));
                }
            });
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"产物读取失败：{exception.Message}");
        }
    }

    /// <summary>恢复选中版本 = 以旧内容追加新版（历史不可变，留痕 restored-from）。</summary>
    private async Task RestoreSelectedVersionAsync()
    {
        if (SelectedArtifact is null || SelectedVersion is null)
        {
            return;
        }

        try
        {
            var restored = await _engine.RestoreAsync(SelectedArtifact.ArtifactId, SelectedVersion.Version);
            await LoadDetailAsync(restored.ArtifactId);
            await RefreshAsync();
            _uiPost(() => StatusText = $"已恢复 v{SelectedVersion.Version} → 新版本 v{restored.CurrentVersion}");
        }
        catch (Exception exception)
        {
            _uiPost(() => StatusText = $"恢复失败：{exception.Message}");
        }
    }
}
