using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Что делает главное окно.</summary>
    public enum MainOperation
    {
        /// <summary>Копировать календарь «откуда → куда».</summary>
        Copy,

        /// <summary>Очистить календарь ящика.</summary>
        Cleanup,
    }

    /// <summary>Ящик, который можно очистить (приёмник или источник по EWS).</summary>
    public sealed class CleanupChoice
    {
        internal CleanupChoice(string label, ITargetSession session)
        {
            Label = label;
            Session = session;
        }

        public string Label { get; }

        internal ITargetSession Session { get; }
    }

    /// <summary>
    /// Главное окно: подключения и выбор операции.
    ///  - <b>Подключения</b>: «Приёмник» — куда копировать (обязательное, по EWS); «Источник по EWS» — необязательное,
    ///    если копировать с сервера, а не из Outlook этого компьютера.
    ///  - <b>Операция</b>: копирование «откуда → куда» (<see cref="Copy"/>) или очистка календаря (<see cref="Cleanup"/>)
    ///    любого из подключённых ящиков.
    /// При запуске читаются сохранённые настройки: нет приёмника — сразу окно «Приёмник»; источник по EWS просто
    /// подключается, если настроен. Сеть при этом не трогается. Во время операции подключения и операция не меняются.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly ISettingsStore? _settings;
        private readonly ISettingsStore? _sourceSettings;
        private readonly ITargetConnector? _connector;
        private readonly IDialogService _dialogs;
        private readonly IConnectionDialog? _connectionDialog;
        private readonly Func<TimeSpan> _localOffset;
        private string _targetLabel = "Приёмник не настроен";
        private ITargetSession? _session;
        private ITargetSession? _sourceSession;
        private MainOperation _operation = MainOperation.Copy;
        private IReadOnlyList<CleanupChoice> _cleanupChoices = Array.Empty<CleanupChoice>();
        private CleanupChoice? _cleanupChoice;
        private bool _started;

        /// <summary>Обычный режим: настройки, подключение по EWS.</summary>
        /// <param name="sourceSettings">Настройки подключения «Источник по EWS»; <c>null</c> — такого подключения нет.</param>
        /// <param name="outlook">Календари Outlook этого компьютера; <c>null</c> — источник «Outlook» недоступен.</param>
        public MainViewModel(
            ISettingsStore settings,
            ITargetConnector connector,
            IDialogService dialogs,
            IConnectionDialog connectionDialog,
            Func<TimeSpan> localOffset,
            ISettingsStore? sourceSettings = null,
            IOutlookCalendars? outlook = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _connectionDialog = connectionDialog ?? throw new ArgumentNullException(nameof(connectionDialog));
            _localOffset = localOffset ?? throw new ArgumentNullException(nameof(localOffset));
            _sourceSettings = sourceSettings;
            Cleanup = new CleanupViewModel(dialogs);
            Copy = new CopyViewModel(dialogs, outlook);
            EditConnectionCommand = new RelayCommand(EditTarget, () => !IsBusy);
            EditSourceCommand = new RelayCommand(EditSource, () => !IsBusy && _sourceSettings != null);
            (PreviewCommand, StopCommand) = CurrentCommands();
            WatchBusy();
        }

        /// <summary>Демонстрационный режим: готовый сеанс (он же приёмник и источник по EWS), без настроек и сети.</summary>
        public MainViewModel(ITargetSession demoSession, IDialogService dialogs, IOutlookCalendars? outlook = null)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _localOffset = () => TimeSpan.Zero; // окна «Подключение» в демонстрационном режиме нет
            Cleanup = new CleanupViewModel(dialogs);
            Copy = new CopyViewModel(dialogs, outlook);
            EditConnectionCommand = new RelayCommand(() => { }, () => false);
            EditSourceCommand = new RelayCommand(() => { }, () => false);
            (PreviewCommand, StopCommand) = CurrentCommands();
            WatchBusy();
            ApplyTarget(demoSession ?? throw new ArgumentNullException(nameof(demoSession)));
            LoadOutlookCalendars();
        }

        /// <summary>Предпросмотр текущей операции (клавиша F5).</summary>
        public RelayCommand PreviewCommand { get; }

        /// <summary>Остановить текущую операцию (клавиша Esc).</summary>
        public RelayCommand StopCommand { get; }

        public CleanupViewModel Cleanup { get; }

        public CopyViewModel Copy { get; }

        /// <summary>Подключение «Приёмник».</summary>
        public RelayCommand EditConnectionCommand { get; }

        /// <summary>Подключение «Источник по EWS».</summary>
        public RelayCommand EditSourceCommand { get; }

        public bool IsDemo => Cleanup.IsDemo;

        public bool IsBusy => Cleanup.IsBusy || Copy.IsBusy;

        /// <summary>Куда копировать — постоянно видно в шапке окна.</summary>
        public string TargetLabel
        {
            get => _targetLabel;
            private set => SetProperty(ref _targetLabel, value);
        }

        /// <summary>Выбранная операция (вкладка). Во время работы не меняется.</summary>
        public MainOperation Operation
        {
            get => _operation;
            set
            {
                // Во время работы — отказ: вкладка возвращается на текущую операцию.
                if (IsBusy || !SetProperty(ref _operation, value))
                {
                    OnPropertyChanged(nameof(Operation));
                }

                OnPropertyChanged(nameof(OperationIndex));
            }
        }

        /// <summary>Номер вкладки (0 — копирование, 1 — очистка) — для привязки TabControl.</summary>
        public int OperationIndex
        {
            get => (int)_operation;
            set => Operation = (MainOperation)value;
        }

        /// <summary>Ящики, которые можно очистить.</summary>
        public IReadOnlyList<CleanupChoice> CleanupChoices
        {
            get => _cleanupChoices;
            private set => SetProperty(ref _cleanupChoices, value);
        }

        public CleanupChoice? CleanupChoice
        {
            get => _cleanupChoice;
            set
            {
                if (value != null && !IsBusy && SetProperty(ref _cleanupChoice, value))
                {
                    Cleanup.SetSession(value.Session);
                }
            }
        }

        /// <summary>Запуск: открыть сохранённые подключения или сразу предложить настроить приёмник.</summary>
        public void Start()
        {
            // Один раз: повторный вызов (окно загружено ещё раз) не должен снова открывать подключения и окна.
            if (_settings is null || _started)
            {
                return;
            }

            _started = true;

            LoadOutlookCalendars();
            if (_sourceSettings != null && LoadSaved(_sourceSettings, out ConnectionProfile? sourceProfile, out CredentialSecret? sourceCredentials) is null
                && sourceProfile != null && sourceCredentials != null)
            {
                ApplySource(_connector!.Open(sourceProfile, sourceCredentials));
            }

            string? loadError = LoadSaved(_settings, out ConnectionProfile? profile, out CredentialSecret? credentials);
            if (profile != null && credentials != null && loadError is null)
            {
                ApplyTarget(_connector!.Open(profile, credentials));
                return;
            }

            Edit(_settings, profile, credentials, loadError, "Приёмник: подключение по EWS", ApplyTarget);
        }

        /// <summary>
        /// Список календарей Outlook читается сразу — человеку не нужно искать кнопку «Обновить». Outlook не
        /// запускается: если он закрыт, вкладка покажет это текстом.
        /// </summary>
        private void LoadOutlookCalendars()
        {
            if (Copy.RefreshOutlookCommand.CanExecute(null))
            {
                Copy.RefreshOutlookCommand.Execute(null);
            }
        }

        private void EditTarget()
        {
            string? loadError = LoadSaved(_settings!, out ConnectionProfile? profile, out CredentialSecret? credentials);
            Edit(_settings!, profile, credentials, loadError, "Приёмник: подключение по EWS", ApplyTarget);
        }

        private void EditSource()
        {
            string? loadError = LoadSaved(_sourceSettings!, out ConnectionProfile? profile, out CredentialSecret? credentials);
            Edit(_sourceSettings!, profile, credentials, loadError, "Источник: подключение по EWS", ApplySource);
        }

        /// <summary>Сохранённые настройки; возвращает текст ошибки чтения или <c>null</c>.</summary>
        private static string? LoadSaved(ISettingsStore settings, out ConnectionProfile? profile, out CredentialSecret? credentials)
        {
            profile = null;
            credentials = null;
            try
            {
                profile = settings.LoadProfile();
                credentials = settings.LoadCredentials();
                return null;
            }
            catch (ProfileLoadException ex)
            {
                return ex.Message;
            }
#pragma warning disable CA1031 // Диспетчер учётных данных недоступен — предложим ввести заново, а не упадём.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return "Не удалось прочитать сохранённые учётные данные: " + ex.Message;
            }
        }

        private void Edit(
            ISettingsStore settings, ConnectionProfile? profile, CredentialSecret? credentials, string? loadError, string title, Action<ITargetSession> apply)
        {
            var connection = new ConnectionViewModel(
                settings, _connector!, _dialogs, _localOffset(), profile, credentials, loadError, title);
            if (_connectionDialog!.Show(connection) && connection.OpenedSession != null)
            {
                apply(connection.OpenedSession);
            }
        }

        private void ApplyTarget(ITargetSession session)
        {
            ITargetSession? previous = _session;
            _session = session;
            TargetLabel = session.TargetLabel;
            Copy.SetTarget(session);
            if (session.IsDemo)
            {
                Copy.SetEwsSource(session);
            }

            RebuildCleanupChoices();
            OnPropertyChanged(nameof(IsDemo));

            // Прежний сеанс больше не нужен: его соединения и проверка сертификата освобождаются.
            previous?.Dispose();
        }

        private void ApplySource(ITargetSession session)
        {
            ITargetSession? previous = _sourceSession;
            _sourceSession = session;
            Copy.SetEwsSource(session);
            RebuildCleanupChoices();
            previous?.Dispose();
        }

        private void RebuildCleanupChoices()
        {
            var choices = new List<CleanupChoice>();
            if (_session != null)
            {
                choices.Add(new CleanupChoice("Приёмник: " + _session.TargetLabel, _session));
            }

            if (_sourceSession != null)
            {
                choices.Add(new CleanupChoice("Источник по EWS: " + _sourceSession.TargetLabel, _sourceSession));
            }

            ITargetSession? keep = _cleanupChoice?.Session;
            CleanupChoices = choices;
            CleanupChoice selected = choices.FirstOrDefault(c => ReferenceEquals(c.Session, keep)) ?? choices.FirstOrDefault()!;
            _cleanupChoice = null;
            if (selected != null)
            {
                CleanupChoice = selected;
            }
            else
            {
                Cleanup.SetSession(null);
                OnPropertyChanged(nameof(CleanupChoice));
            }
        }

        /// <summary>
        /// Клавиши окна — для открытой вкладки: F5 строит предпросмотр именно её операции, Esc останавливает идущую.
        /// Доступность следует за командами вкладок.
        /// </summary>
        private (RelayCommand Preview, RelayCommand Stop) CurrentCommands()
        {
            var preview = new RelayCommand(
                () =>
                {
                    if (_operation == MainOperation.Copy)
                    {
                        Copy.PreviewCommand.Execute(null);
                    }
                    else
                    {
                        Cleanup.PreviewCommand.Execute(null);
                    }
                },
                () => _operation == MainOperation.Copy ? Copy.PreviewCommand.CanExecute(null) : Cleanup.PreviewCommand.CanExecute(null));
            var stop = new RelayCommand(
                () =>
                {
                    Copy.Stop();
                    Cleanup.Stop();
                },
                () => Copy.StopCommand.CanExecute(null) || Cleanup.StopCommand.CanExecute(null));
            Copy.PreviewCommand.CanExecuteChanged += (_, __) => preview.RaiseCanExecuteChanged();
            Cleanup.PreviewCommand.CanExecuteChanged += (_, __) => preview.RaiseCanExecuteChanged();
            Copy.StopCommand.CanExecuteChanged += (_, __) => stop.RaiseCanExecuteChanged();
            Cleanup.StopCommand.CanExecuteChanged += (_, __) => stop.RaiseCanExecuteChanged();
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Operation))
                {
                    preview.RaiseCanExecuteChanged();
                }
            };
            return (preview, stop);
        }

        private void WatchBusy()
        {
            void OnChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(CleanupViewModel.IsBusy))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    EditConnectionCommand.RaiseCanExecuteChanged();
                    EditSourceCommand.RaiseCanExecuteChanged();
                }
            }

            Cleanup.PropertyChanged += OnChanged;
            Copy.PropertyChanged += OnChanged;
        }
    }
}
