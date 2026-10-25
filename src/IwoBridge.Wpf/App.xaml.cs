using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using IwoBridge.App.History;
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
    /// этом. Одновременно работает один экземпляр: второй запуск показывает окно первого (даже если тот работал только
    /// значком в области уведомлений) и завершается.
    /// Любая непредвиденная ошибка показывается окном с текстом, а не закрывает программу молча; полный текст
    /// (со стеком вызовов) записывается в <c>logs\crash.log</c> рядом с программой.
    /// </summary>
    public partial class App : Application
    {
        // Имя в пространстве текущего сеанса пользователя: другим пользователям машины запуск не мешает.
        private const string SingleInstanceName = "Local\\IwoCalendarBridge.SingleInstance";

        // Второй запуск просит первый показать окно (первый мог работать только значком в области уведомлений).
        private const string ShowWindowSignalName = "Local\\IwoCalendarBridge.ShowWindow";

        // Лавина одинаковых ошибок (например, при каждой отрисовке) не должна открывать окна одно за другим.
        private const int CrashLimit = 5;
        private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);

        private Mutex? _singleInstance;
        private EventWaitHandle? _showSignal;
        private RegisteredWaitHandle? _showWait;
        private bool _showingCrash;
        private int _crashCount;
        private DateTime _firstCrash;
        private IDisposable? _theme;
        private OutlookCalendars? _outlook;
        private TrayIcon? _tray;
        private MainViewModel? _main;
        private IOperationLog? _log;
        private TextWriter? _console;

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
                // Первый экземпляр сам покажет своё окно; не получилось подать знак — сказать, где программа.
                if (EventWaitHandle.TryOpenExisting(ShowWindowSignalName, out EventWaitHandle? signal))
                {
                    using (signal)
                    {
                        signal.Set();
                    }
                }
                else
                {
                    MessageBox.Show(
                        "Программа уже запущена. Её окно открывается двойным щелчком по значку в области уведомлений рядом с часами.",
                        "iwo Calendar Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }

            // Высокая контрастность Windows: цвета окон заменяются системными и следуют за сменой темы на лету.
            _theme = Theme.Attach(Resources, Dispatcher);

            var dialogs = new WpfDialogService();
            bool demo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
            bool tray = e.Args.Any(a => string.Equals(a, RegistryAutostart.TrayArgument, StringComparison.OrdinalIgnoreCase));
            IOperationLog log = CreateLog(demo);
            _log = log;
            MainViewModel viewModel = demo
                ? new MainViewModel(
                    new DemoSession(DateTime.Today, DemoSession.DemoOffset),
                    dialogs,
                    new DemoSources(),
                    new CopyServices(new MemoryCopySettingsStore(), new DispatcherOneShotTimer(), autostart: null),
                    log)
                : CreateMain(dialogs, log);
            _main = viewModel;

            var window = new MainWindow(viewModel, dialogs);
            MainWindow = window;
            _tray = new TrayIcon(viewModel, window.ShowFromTray, () => window.RequestExit());
            window.HiddenToTray += (_, __) => _tray.ShowHiddenHint();
            SessionEnding += (_, __) => window.RequestExit(ask: false);

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowSignalName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showSignal, (_, __) => Dispatcher.BeginInvoke(new Action(window.ShowFromTray)), null, Timeout.Infinite, executeOnlyOnce: false);

            // События системы приходят в своём потоке; подписка снимается при выходе (статическое событие держало бы окно).
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;

            // Запуск с Windows (--tray): при включённом автокопировании и настроенном приёмнике окно не показывается —
            // программа сразу работает из области уведомлений. Иначе окно открывается как обычно.
            if (tray && viewModel.Copy.Auto.IsEnabled && viewModel.StartHidden())
            {
                return;
            }

            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
            _showWait?.Unregister(null);
            _showSignal?.Dispose();
            _tray?.Dispose();
            _main?.Dispose();
            _theme?.Dispose();
            _outlook?.Dispose();

            // Консоль: строка о закрытии и дописывание очереди (не дольше двух секунд — вывод может стоять).
            if (_console != null)
            {
                _log?.Info("iwo Calendar Bridge закрыта.");
                _console.Dispose();
            }
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

        /// <summary>
        /// Журнал операций: файл <c>logs\history.jsonl</c> (в демонстрационном режиме — память) и копия в консоль, если
        /// программу запустили из консоли или её вывод перенаправлен в файл.
        /// </summary>
        private IOperationLog CreateLog(bool demo)
        {
            IOperationLog store = demo
                ? new MemoryOperationLog()
                : new JsonLinesOperationLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "history.jsonl"));
            _console = ConsoleOutput.TryOpen();
            if (_console is null)
            {
                return store;
            }

            var mirrored = new MirroredOperationLog(store, _console);
            string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
            mirrored.Info("iwo Calendar Bridge " + version + " запущена" + (demo ? " (демонстрационный режим)" : string.Empty)
                + ". Журнал операций: " + (store.Location ?? "в памяти") + ".");
            return mirrored;
        }

        private MainViewModel CreateMain(WpfDialogService dialogs, IOperationLog log)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var credentials = new WindowsCredentialStore();
            var target = new AppSettingsStore(Path.Combine(root, "config", "connection.json"), credentials, CredentialTargets.Ews);
            var source = new AppSettingsStore(Path.Combine(root, "config", "source-connection.json"), credentials, CredentialTargets.EwsSource);
            var mailboxes = new MailboxStores(Path.Combine(root, "config", "mailboxes"), credentials);
            var connector = new EwsConnector(Path.Combine(root, "manifests"));
            _outlook = new OutlookCalendars();
            var autostart = new RegistryAutostart(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            autostart.Refresh();
            var copy = new CopyServices(new FileCopySettingsStore(Path.Combine(root, "config", "copy.json")), new DispatcherOneShotTimer(), autostart);
            return new MainViewModel(target, connector, dialogs, dialogs, LocalOffset, source, _outlook, copy, mailboxes, log);
        }

        private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Dispatcher.BeginInvoke(new Action(() => _main?.Copy.Auto.SystemResumed()));
            }
        }

        private void OnTimeChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _main?.Copy.Auto.SystemResumed()));
        }

        private static TimeSpan LocalOffset()
        {
            return TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        }

        private void RegisterCrashHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                ShowCrash(args.Exception);
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

        /// <summary>
        /// Окно ошибки — одно за раз (пока оно открыто, новые ошибки только пишутся в журнал) и поверх главного окна, если
        /// оно видно. Больше <see cref="CrashLimit"/> ошибок за минуту — программа сообщает об этом и завершается: работа
        /// в таком состоянии сыпала бы окнами без конца.
        /// </summary>
        private void ShowCrash(Exception exception)
        {
            string? log = WriteCrashLog(exception);
            DateTime now = DateTime.UtcNow;
            if (now - _firstCrash > CrashWindow)
            {
                _firstCrash = now;
                _crashCount = 0;
            }

            _crashCount++;
            if (_showingCrash)
            {
                return;
            }

            _showingCrash = true;
            try
            {
                Window? owner = MainWindow?.IsVisible == true ? MainWindow : null;
                bool tooMany = _crashCount >= CrashLimit;
                string text = (tooMany
                        ? "Непредвиденные ошибки повторяются. Программа будет закрыта — запустите её снова.\n\n"
                        : "Непредвиденная ошибка. Программа продолжает работу, но последнее действие могло не завершиться.\n\n")
                    + exception.GetBaseException().Message + LogNote(log);
                if (owner != null)
                {
                    MessageBox.Show(owner, text, "iwo Calendar Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show(text, "iwo Calendar Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                if (tooMany)
                {
                    Shutdown(1);
                }
            }
            finally
            {
                _showingCrash = false;
            }
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
