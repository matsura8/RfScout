using System.Windows.Input;

namespace RfScout.ViewModels;

/// <summary>
/// The small ICommand the toolbar buttons and keyboard shortcuts bind to.
///
/// CanExecute is wired to <see cref="System.Windows.Threading.Dispatcher"/>'s requery rather than
/// a manual event, so a button enables and disables itself as soon as the state behind it changes -
/// which is what makes the Space shortcut behave the same as the button it triggers, the way the UI
/// design notes require.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Predicate<object?>? _canRun;
    private EventHandler? _explicitHandlers;

    /// <summary>A command that takes nothing.</summary>
    public RelayCommand(Action run, Predicate<object?>? canRun = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = _ => run();
        _canRun = canRun;
    }

    /// <summary>A command that takes a parameter, used by the preset picker.</summary>
    public RelayCommand(Action<object?> run, Predicate<object?>? canRun = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        _canRun = canRun;
    }

    /// <summary>
    /// Hooked to <see cref="CommandManager.RequerySuggested"/>, so bound buttons re-check
    /// CanExecute after input and whenever the view calls CommandManager.InvalidateRequerySuggested.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            _explicitHandlers += value;
        }

        remove
        {
            CommandManager.RequerySuggested -= value;
            _explicitHandlers -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canRun?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _run(parameter);

    /// <summary>Ask WPF to re-check CanExecute on every control bound to this command.</summary>
    public void RaiseCanExecuteChanged() => _explicitHandlers?.Invoke(this, EventArgs.Empty);
}
