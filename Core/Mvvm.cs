using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FrogLauncher.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);
}

/// <summary>Асинхронная команда: ошибки логируются и показываются пользователю понятным сообщением.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _exec;
    private readonly Func<object?, bool>? _can;

    public AsyncRelayCommand(Func<object?, Task> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;

    public async void Execute(object? p)
    {
        try { await _exec(p); }
        catch (OperationCanceledException) { Log.Info("Операция отменена"); }
        catch (Exception ex)
        {
            Log.Error("Ошибка команды", ex);
            Dialogs.Error(ex.Message);
        }
    }
}

public static class Dialogs
{
    public static void Error(string msg) =>
        System.Windows.MessageBox.Show(msg, "FrogLauncher 🐸", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

    public static void Info(string msg) =>
        System.Windows.MessageBox.Show(msg, "FrogLauncher 🐸", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
}
