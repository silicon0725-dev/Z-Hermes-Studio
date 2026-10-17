using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ZHS.Presentation;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void RaiseChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>异步命令。CanExecute 固定为 true，执行期互斥由 ViewModel 的 IsBusy 控制。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    /// <summary>最近一次执行的 Task（测试与异步等待用）。</summary>
    public Task? LastExecution { get; private set; }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => LastExecution = _execute();
}
