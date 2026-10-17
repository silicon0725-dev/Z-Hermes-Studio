using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZHS.Presentation.Chat;

namespace ZHS.Desktop;

public partial class MainWindow : Window
{
    private ScrollViewer? _scroller;
    private ChatViewModel? _subscribedChat;
    private bool _anchoringPrepend;
    private double _extentBeforePrepend;

    /// <summary>任务面板 VM（由 App 注入；XAML 内 TasksHost 以此为 DataContext）。</summary>
    public ZHS.Presentation.Tasks.TasksViewModel? Tasks { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs args)
    {
        base.OnOpened(args);
        _scroller = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "TasksHost") is { } tasksHost)
        {
            tasksHost.DataContext = Tasks;
        }
    }

    private ChatViewModel? Chat => (DataContext as MainViewModel)?.CurrentChat;

    private void OnDataContextChanged(object? sender, System.EventArgs args)
    {
        if (DataContext is MainViewModel main)
        {
            main.PropertyChanged += (_, changed) =>
            {
                if (changed.PropertyName == nameof(MainViewModel.CurrentChat))
                {
                    AttachChat(main.CurrentChat);
                }
            };
            AttachChat(main.CurrentChat);
        }
    }

    private void AttachChat(ChatViewModel? chat)
    {
        if (ReferenceEquals(_subscribedChat, chat))
        {
            return;
        }

        if (_subscribedChat is not null)
        {
            _subscribedChat.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _subscribedChat = chat;
        if (chat is not null)
        {
            chat.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_scroller is null)
        {
            return;
        }

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewStartingIndex == 0 && _subscribedChat is { Messages.Count: > 1 }:
                // 顶部前插历史（加载更早）：记录插入前 Extent，布局完成后补偿偏移保持视觉锚点（§7.2）
                if (!_anchoringPrepend)
                {
                    _extentBeforePrepend = _scroller.Extent.Height;
                    _anchoringPrepend = true;
                    LayoutUpdated += OnPrependLayoutUpdated;
                }

                break;

            case NotifyCollectionChangedAction.Add:
                // 新消息追加：贴近底部时跟随滚动
                if (IsNearBottom())
                {
                    ScrollToBottom();
                }

                break;

            case NotifyCollectionChangedAction.Reset:
                ScrollToBottom();
                break;
        }
    }

    private void OnPrependLayoutUpdated(object? sender, System.EventArgs args)
    {
        if (_scroller is null)
        {
            return;
        }

        var delta = _scroller.Extent.Height - _extentBeforePrepend;
        if (delta > 0)
        {
            _scroller.Offset = new Avalonia.Vector(_scroller.Offset.X, _scroller.Offset.Y + delta);
            _anchoringPrepend = false;
            LayoutUpdated -= OnPrependLayoutUpdated;
        }
    }

    private bool IsNearBottom()
    {
        if (_scroller is null)
        {
            return true;
        }

        return _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - 48;
    }

    private void ScrollToBottom()
    {
        if (_scroller is not null)
        {
            _scroller.ScrollToEnd();
        }
    }
}
