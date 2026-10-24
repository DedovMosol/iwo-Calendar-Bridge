using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using IwoBridge.Core.Security;
using IwoBridge.Ews.Security;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;

namespace IwoBridge.Wpf
{
    /// <summary>
    /// Точка входа и корень композиции: здесь — и только здесь — создаются реальные реализации портов.
    ///
    /// Запуск:
    ///  - <c>IwoCalendarBridge.exe</c> — обычный режим: настройки из <c>config\connection.json</c> рядом с программой,
    ///    пароль — из диспетчера учётных данных Windows, манифесты удаляемого — в папку <c>manifests</c> рядом с
    ///    программой;
    ///  - <c>IwoCalendarBridge.exe --demo</c> — демонстрационный календарь в памяти, без сервера.
    ///
    /// Программа переносная: устанавливается копированием папки. Папка должна быть доступна на запись (например,
    /// в профиле пользователя, а не в Program Files), иначе настройки и манифесты не сохранятся — окно сообщит об
    /// этом. Одновременно работает один экземпляр: второй запуск только показывает сообщение (T5).
    /// Любая непредвиденная ошибка показывается окном с текстом, а не закрывает программу молча; полный текст
    /// (со стеком вызовов) записывается в <c>logs\crash.log</c> рядом с программой.
    /// </summary>
    public partial class App : Application
    {
        // Имя в пространстве текущего сеанса пользователя: другим пользователям машины запуск не мешает.
        private const string SingleInstanceName = "Local\\IwoCalendarBridge.SingleInstance";

        private Mutex? _singleInstance;
        private IDisposable? _theme;
        private OutlookCalendars? _outlook;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterCrashHandlers();

            // Русские даты, числа («2 441», а не «2,441») и календарь в DatePicker независимо от языка Windows. Язык
            // задаётся и стилем окна (AppWindow): значение из стиля наследуется всеми потомками, включая фрагменты
            // текста (Run), которым значение по умолчанию отсюда не передаётся.
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));

            _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool first);
            if (!first)
            {
                MessageBox.Show("Программа уже запущена. Переключитесь на её окно.", "iwo Calendar Bridge",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Высокая контрастность Windows: цвета окон заменяются системными и следуют за сменой темы на лету.
            _theme = Theme.Attach(Resources, Dispatcher);

            var dialogs = new WpfDialogService();
            MainViewModel viewModel = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase))
                ? new MainViewModel(new DemoSession(DateTime.Today, DemoSession.DemoOffset), dialogs, new DemoSources())
                : CreateMain(dialogs);

            MainWindow = new MainWindow(viewModel, dialogs);
            MainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _theme?.Dispose();
            _outlook?.Dispose();
            if (_singleInstance != null)
            {
                try
                {
                    _singleInstance.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Не владеем (второй экземпляр) — освобождать нечего.
                }

                _singleInstance.Dispose();
            }

            base.OnExit(e);
        }

        private MainViewModel CreateMain(WpfDialogService dialogs)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var credentials = new WindowsCredentialStore();
            var target = new AppSettingsStore(Path.Combine(root, "config", "connection.json"), credentials, CredentialTargets.Ews);
            var source = new AppSettingsStore(Path.Combine(root, "config", "source-connection.json"), credentials, CredentialTargets.EwsSource);
            var connector = new EwsConnector(Path.Combine(root, "manifests"));
            _outlook = new OutlookCalendars();
            return new MainViewModel(target, connector, dialogs, dialogs, LocalOffset, source, _outlook);
        }

        private static TimeSpan LocalOffset()
        {
            return TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        }

        private void RegisterCrashHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                ShowCrash(args.Exception);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                args.SetObserved();
                Dispatcher.BeginInvoke(new Action(() => ShowCrash(args.Exception)));
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                // Процесс завершается: успеть записать журнал и показать причину.
                if (args.ExceptionObject is Exception ex)
                {
                    string? log = WriteCrashLog(ex);
                    MessageBox.Show(
                        ex.GetBaseException().Message + LogNote(log),
                        "iwo Calendar Bridge — критическая ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
        }

        private static void ShowCrash(Exception exception)
        {
            string? log = WriteCrashLog(exception);
            MessageBox.Show(
                "Непредвиденная ошибка. Программа продолжает работу, но последнее действие могло не завершиться.\n\n"
                + exception.GetBaseException().Message + LogNote(log),
                "iwo Calendar Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static string? WriteCrashLog(Exception exception)
        {
            return CrashLog.TryWrite(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"), exception, DateTime.Now);
        }

        private static string LogNote(string? logPath)
        {
            return logPath is null ? string.Empty : "\n\nПодробности для администратора записаны в файл:\n" + logPath;
        }
    }
}
