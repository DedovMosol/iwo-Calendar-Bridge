using System;
using System.Windows.Input;

namespace IwoBridge.Wpf.Mvvm
{
    /// <summary>
    /// Синхронная команда для кнопок. Доступность (<see cref="CanExecute"/>) пересчитывается, когда модель
    /// представления вызывает <see cref="RaiseCanExecuteChanged"/> после смены своего состояния — явно, без
    /// глобального <c>CommandManager</c>: так понятно, что и когда включает кнопку, и нет лишних пересчётов.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _execute();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
