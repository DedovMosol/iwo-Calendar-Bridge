using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace IwoBridge.Wpf.Mvvm
{
    /// <summary>
    /// Асинхронная команда: пока операция идёт, команда недоступна — двойной щелчок не запустит её дважды.
    /// Ошибки операция обрабатывает сама и показывает пользователю; необработанное исключение уходит в общий
    /// обработчик приложения (App), а не теряется.
    ///
    /// Тесты вызывают <see cref="ExecuteAsync"/> и дожидаются завершения.
    /// </summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isRunning;

        public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return !_isRunning && (_canExecute?.Invoke() ?? true);
        }

        /// <summary>Вызов из окна. <c>async void</c> допустим только здесь — на границе с WPF.</summary>
        public async void Execute(object? parameter)
        {
            await ExecuteAsync().ConfigureAwait(true);
        }

        public async Task ExecuteAsync()
        {
            if (!CanExecute(null))
            {
                return;
            }

            _isRunning = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute().ConfigureAwait(true);
            }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
