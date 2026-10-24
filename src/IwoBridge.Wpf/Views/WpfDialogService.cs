using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Окна для моделей представления: подтверждение удаления, вопросы «да/нет», окно «Подключение», проводник.
    /// Владелец окна — активное окно программы, поэтому диалог всегда поверх него и по центру.
    /// </summary>
    public sealed class WpfDialogService : IDialogService, IConnectionDialog
    {
        public bool ConfirmDeletion(DeletionConfirmationViewModel confirmation)
        {
            var window = new ConfirmDeletionWindow(confirmation) { Owner = ActiveWindow() };
            return window.ShowDialog() == true;
        }

        public bool Ask(string title, string question, string confirm, string cancel = "Отмена")
        {
            Window? owner = ActiveWindow();
            var window = new QuestionWindow(title, question, confirm, cancel);
            if (owner != null)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return window.ShowDialog() == true;
        }

        public void ShowFileInFolder(string path)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(ActiveWindow() ?? Application.Current.MainWindow, "Файл не найден:\n" + path, "Список удаляемого",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Проводник с выделенным файлом; путь в кавычках — в нём бывают пробелы.
            Process.Start("explorer.exe", "/select,\"" + path + "\"")?.Dispose();
        }

        /// <summary>Любое модальное окно потока интерфейса (ShowDialog, системные окна) — признак WPF, без своих счётчиков.</summary>
        public bool IsShowingDialog => System.Windows.Interop.ComponentDispatcher.IsThreadModal;

        /// <summary>Справка — немодальная, рядом с программой: можно читать и делать по шагам одновременно.</summary>
        public void ShowHelp()
        {
            HelpWindow? open = Application.Current?.Windows.OfType<HelpWindow>().FirstOrDefault();
            if (open != null)
            {
                if (open.WindowState == WindowState.Minimized)
                {
                    open.WindowState = WindowState.Normal;
                }

                open.Activate();
                return;
            }

            new HelpWindow { Owner = Application.Current?.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null }.Show();
        }

        public bool Show(ConnectionViewModel connection)
        {
            var window = new ConnectionWindow(connection) { Owner = ActiveWindow() };
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// Владелец нового окна — последнее открытое видимое окно программы: модальное окно всегда открыто позже своего
        /// владельца. Не «активное»: если пользователь переключился в другую программу, активного окна нет, и вопрос
        /// из окна «Подключение» иначе достался бы главному окну, а окно «Подключение» осталось бы доступным.
        /// </summary>
        private static Window? ActiveWindow()
        {
            Application? app = Application.Current;
            return app?.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible);
        }
    }
}
