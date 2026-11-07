using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AppFlow.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> onError) : ICommand
{
    public AsyncCommand(Func<Task> execute, Action<Exception> onError) : this(_ => execute(), onError) { }
    private bool busy;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !busy;
    public async void Execute(object? parameter)
    {
        if (busy) return;
        busy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (Exception ex) { onError(ex); }
        finally { busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
