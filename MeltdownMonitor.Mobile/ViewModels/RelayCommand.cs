using System.Windows.Input;

namespace MeltdownMonitor.Mobile.ViewModels;

public sealed class RelayCommand : ICommand
{
	private readonly Action _execute;
	private readonly Func<bool>? _canExecute;

	public RelayCommand(Action execute, Func<bool>? canExecute = null)
	{
		_execute = execute;
		_canExecute = canExecute;
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

	public void Execute(object? parameter) => _execute();

	public void RaiseCanExecuteChanged() =>
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Parameterised relay command — used where a single command handles a set of
/// choices distinguished by the bound <c>CommandParameter</c> (e.g. the four
/// annotation labels on the Now screen).
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
	private readonly Action<T> _execute;
	private readonly Func<T, bool>? _canExecute;

	public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
	{
		_execute = execute;
		_canExecute = canExecute;
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter)
	{
		if (_canExecute is null)
		{
			return true;
		}

		return parameter is T value && _canExecute(value);
	}

	public void Execute(object? parameter)
	{
		if (parameter is T value)
		{
			_execute(value);
		}
	}

	public void RaiseCanExecuteChanged() =>
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
