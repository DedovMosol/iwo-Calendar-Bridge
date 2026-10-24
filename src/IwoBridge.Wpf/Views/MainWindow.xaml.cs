using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Главное окно. Логики здесь нет — она в <see cref="MainViewModel"/>; окно только отвечает за закрытие во время
    /// операции: спрашивает, остановить ли её, ждёт завершения текущего пакета и закрывается — так удаление не
    /// обрывается посреди пакета и итог записан в манифест.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IDialogService _dialogs;
        private bool _closeAfterStop;
        private bool _exitRequested;
        private bool _silentExit;

        /// <summary>Окно закрыли, а автокопирование включено — окно спрятано, программа работает из области уведомлений.</summary>
        public event EventHandler? HiddenToTray;

        /// <summary>Выйти совсем (меню значка «Выход», завершение сеанса Windows): закрытие не прячет окно.</summary>
        /// <param name="ask">Спросить, если идёт операция; <c>false</c> — завершение сеанса Windows: остановить молча.</param>
        public void RequestExit(bool ask = true)
        {
            _exitRequested = true;
            _silentExit = !ask;
            Close();
        }

        /// <summary>Показать окно из области уведомлений.</summary>
        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }

        public MainWindow(MainViewModel viewModel, IDialogService dialogs)
        {
            InitializeComponent();
            WindowSizing.FitToWorkArea(this);
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            Loaded += (_, __) => viewModel.Start();
        }

        private MainViewModel ViewModel => (MainViewModel)DataContext;

        protected override void OnClosing(CancelEventArgs e)
        {
            CleanupViewModel cleanup = ViewModel.Cleanup;
            CopyViewModel copy = ViewModel.Copy;

            // Автокопирование включено: крестик прячет окно, расписание продолжается (выход — меню значка).
            if (!_exitRequested && !_closeAfterStop && copy.Auto.IsEnabled && HiddenToTray != null)
            {
                e.Cancel = true;
                Hide();
                HiddenToTray(this, EventArgs.Empty);
                return;
            }

            // Windows завершает сеанс: вопросов не задавать (система ждать не станет) — остановить и выйти. Начатое событие
            // или пакет не дописаны, но копия без отпечатка перезапишется при следующем запуске, а очистка удаляет по
            // списку, сохранённому заранее.
            if (_silentExit)
            {
                copy.Stop();
                cleanup.Stop();
                base.OnClosing(e);
                return;
            }

            if ((cleanup.IsBusy || copy.IsBusy) && !_closeAfterStop)
            {
                e.Cancel = true;
                string question = copy.IsBusy
                    ? "Идёт копирование. Остановить его после текущего события и закрыть программу?\n\nУже записанное останется в приёмнике и будет учтено при следующем копировании."
                    : cleanup.Step == CleanupStep.Deleting
                        ? "Идёт удаление. Остановить его после текущего пакета и закрыть программу?\n\nУже удалённое останется удалённым и перечислено в списке удаляемого."
                        : "Идёт чтение календаря. Прервать его и закрыть программу?";
                if (_dialogs.Ask("Закрыть программу?", question, "Остановить и закрыть", "Продолжить работу"))
                {
                    _closeAfterStop = true;
                    Task operation = copy.IsBusy ? copy.Operation : cleanup.Operation;
                    copy.Stop();
                    cleanup.Stop();
                    CloseWhenStopped(operation);
                }

                return;
            }

            base.OnClosing(e);
        }

        /// <summary>
        /// Закрыть окно, когда операция остановится. Close() вызывается через очередь диспетчера, а не сразу: внутри
        /// OnClosing (если операция уже закончилась, пока пользователь отвечал на вопрос) WPF запрещает Close().
        /// Исход операции не важен — её ошибки уже показаны на экране, здесь они только наблюдаются.
        /// </summary>
        private void CloseWhenStopped(Task operation)
        {
            Title = "Останавливаю…";
            IsEnabled = false;
            operation.ContinueWith(
                _ => Dispatcher.BeginInvoke(new Action(Close)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }
}
