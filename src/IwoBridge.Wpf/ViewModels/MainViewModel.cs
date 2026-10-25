using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IwoBridge.App.Configuration;
using IwoBridge.App.History;
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

    /// <summary>Службы вкладки копирования: настройки между запусками, таймер автокопирования, автозапуск с Windows.</summary>
    public sealed class CopyServices
    {
        public CopyServices(ICopySettingsStore settings, IOneShotTimer timer, IAutostart? autostart)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Timer = timer ?? throw new ArgumentNullException(nameof(timer));
            Autostart = autostart;
        }

        public ICopySettingsStore Settings { get; }

        public IOneShotTimer Timer { get; }

        public IAutostart? Autostart { get; }
    }

    /// <summary>Ящик, который можно очистить: приёмник, источник по EWS или добавленная почта.</summary>
    public sealed class CleanupChoice
    {
        internal CleanupChoice(
            string label, ITargetSession? session, bool isAdded = false, ISettingsStore? store = null, ConnectionProfile? profile = null, string? userName = null)
        {
            Label = label;
            Session = session;
            IsAdded = isAdded;
            Store = store;
            Profile = profile;
            UserName = userName;
        }

        public string Label { get; }

        /// <summary>Подключение; <c>null</c> — добавленная почта, пароль которой не сохранён (нужно «Изменить…»).</summary>
        internal ITargetSession? Session { get; }

        /// <summary>Почта, добавленная кнопкой «Добавить почту…»: её можно изменить и убрать из списка.</summary>
        internal bool IsAdded { get; }

        /// <summary>Где запомнена добавленная почта; <c>null</c> — только до закрытия программы.</summary>
        internal ISettingsStore? Store { get; }

        /// <summary>Настройки добавленной почты — чтобы «Изменить…» открыл окно заполненным.</summary>
        internal ConnectionProfile? Profile { get; }

        internal string? UserName { get; }
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
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ISettingsStore? _settings;
        private readonly ISettingsStore? _sourceSettings;
        private readonly IMailboxStores? _mailboxStores;
        private readonly List<CleanupChoice> _added = new List<CleanupChoice>();
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
        private RelayCommand? _helpCommand;
        private RelayCommand? _historyCommand;
        private readonly IOperationLog? _log;

        /// <summary>Обычный режим: настройки, подключение по EWS.</summary>
        /// <param name="sourceSettings">Настройки подключения «Источник по EWS»; <c>null</c> — такого подключения нет.</param>
        /// <param name="outlook">Календари Outlook этого компьютера; <c>null</c> — источник «Outlook» недоступен.</param>
        /// <param name="mailboxes">Добавленная почта для очистки; <c>null</c> — добавлять почту нельзя.</param>
        public MainViewModel(
            ISettingsStore settings,
            ITargetConnector connector,
            IDialogService dialogs,
            IConnectionDialog connectionDialog,
            Func<TimeSpan> localOffset,
            ISettingsStore? sourceSettings = null,
            IOutlookCalendars? outlook = null,
            CopyServices? copyServices = null,
            IMailboxStores? mailboxes = null,
            IOperationLog? log = null)
        {
            _mailboxStores = mailboxes;
            _log = log;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _connectionDialog = connectionDialog ?? throw new ArgumentNullException(nameof(connectionDialog));
            _localOffset = localOffset ?? throw new ArgumentNullException(nameof(localOffset));
            _sourceSettings = sourceSettings;
            Cleanup = NewCleanup(dialogs);
            Copy = NewCopy(dialogs, outlook, copyServices);
            EditConnectionCommand = new RelayCommand(EditTarget, () => !IsBusy);
            EditSourceCommand = new RelayCommand(EditSource, () => !IsBusy && _sourceSettings != null);
            AddMailboxCommand = new RelayCommand(AddMailbox, () => !IsBusy && _mailboxStores != null);
            EditMailboxCommand = new RelayCommand(EditMailbox, () => !IsBusy && CanRemoveMailbox);
            RemoveMailboxCommand = new RelayCommand(RemoveMailbox, () => !IsBusy && CanRemoveMailbox);
            (PreviewCommand, StopCommand) = CurrentCommands();
            WatchBusy();
        }

        /// <summary>Демонстрационный режим: готовый сеанс (он же приёмник и источник по EWS), без настроек и сети.</summary>
        public MainViewModel(
            ITargetSession demoSession, IDialogService dialogs, IOutlookCalendars? outlook = null, CopyServices? copyServices = null, IOperationLog? log = null)
        {
            _log = log;
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _localOffset = () => TimeSpan.Zero; // окна «Подключение» в демонстрационном режиме нет
            Cleanup = NewCleanup(dialogs);
            Copy = NewCopy(dialogs, outlook, copyServices);
            EditConnectionCommand = new RelayCommand(() => { }, () => false);
            EditSourceCommand = new RelayCommand(() => { }, () => false);
            AddMailboxCommand = new RelayCommand(() => { }, () => false);
            EditMailboxCommand = new RelayCommand(() => { }, () => false);
            RemoveMailboxCommand = new RelayCommand(() => { }, () => false);
            (PreviewCommand, StopCommand) = CurrentCommands();
            WatchBusy();
            ApplyTarget(demoSession ?? throw new ArgumentNullException(nameof(demoSession)));
            LoadOutlookCalendars();
        }

        /// <summary>Расписание и сеансы освобождаются при выходе: таймер не держит модель, соединения закрыты.</summary>
        public void Dispose()
        {
            Copy.Auto.Dispose();
            _session?.Dispose();
            _sourceSession?.Dispose();
            foreach (CleanupChoice added in _added)
            {
                added.Session?.Dispose();
            }

            _added.Clear();
        }

        // Одна операция с ящиками за раз на всё окно: очистка не начинается, пока идёт копирование (и автокопирование,
        // запущенное, пока открыта вкладка очистки), — в том числе клавишей F5.
        private CleanupViewModel NewCleanup(IDialogService dialogs) =>
            new CleanupViewModel(dialogs, otherBusy: () => Copy != null && Copy.IsBusy, log: _log);

        // Автокопирование ждёт и очистку: одна операция с ящиками за раз на всё окно.
        private CopyViewModel NewCopy(IDialogService dialogs, IOutlookCalendars? outlook, CopyServices? services) =>
            new CopyViewModel(dialogs, outlook, null, services?.Settings, services?.Timer, services?.Autostart, () => Cleanup.IsBusy, _log);

        /// <summary>Справка (клавиша F1) — доступна всегда, и во время операции.</summary>
        public RelayCommand HelpCommand => _helpCommand ?? (_helpCommand = new RelayCommand(() => _dialogs.ShowHelp()));

        /// <summary>
        /// «История» (Ctrl+H): журнал операций — доступен всегда, и во время операции. Без журнала (тесты экрана) — нет.
        /// </summary>
        public RelayCommand HistoryCommand => _historyCommand ?? (_historyCommand = new RelayCommand(
            () => _dialogs.ShowHistory(() => new HistoryViewModel(_log!, _dialogs)), () => _log != null));

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

        /// <summary>«Добавить почту…»: ещё один ящик в списке очистки.</summary>
        public RelayCommand AddMailboxCommand { get; }

        /// <summary>«Изменить…»: адрес, учётная запись, пароль, «Запомнить» выбранной добавленной почты.</summary>
        public RelayCommand EditMailboxCommand { get; }

        /// <summary>«Убрать из списка»: выбранная добавленная почта.</summary>
        public RelayCommand RemoveMailboxCommand { get; }

        /// <summary>Можно ли добавлять почту (в демонстрационном режиме нельзя).</summary>
        public bool CanAddMailboxes => _mailboxStores != null;

        /// <summary>Выбрана добавленная почта — её можно изменить и убрать из списка.</summary>
        public bool CanRemoveMailbox => _cleanupChoice?.IsAdded == true;

        /// <summary>В списке очистки есть хотя бы один ящик (иначе вкладка предлагает настроить подключение).</summary>
        public bool HasCleanupChoices => _cleanupChoices.Count > 0;

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
            private set
            {
                if (SetProperty(ref _cleanupChoices, value))
                {
                    OnPropertyChanged(nameof(HasCleanupChoices));
                }
            }
        }

        public CleanupChoice? CleanupChoice
        {
            get => _cleanupChoice;
            set
            {
                if (value != null && !IsBusy && SetProperty(ref _cleanupChoice, value))
                {
                    Cleanup.SetSession(value.Session);
                    RaiseMailboxCommands();
                }
            }
        }

        private const string TargetTitle = "Приёмник: подключение по EWS";
        private const string SourceTitle = "Источник: подключение по EWS";
        private const string MailboxTitle = "Почта для очистки: подключение по EWS";

        /// <summary>
        /// Запуск без окна (с Windows, в область уведомлений): только если приёмник уже настроен, а источник по EWS (если
        /// он есть) не ждёт пароля — окно «Подключение» без видимой программы было бы непонятно. Возвращает, удалось ли;
        /// нет — окно нужно показать.
        /// </summary>
        public bool StartHidden()
        {
            if (_settings is null || LoadSaved(_settings, out ConnectionProfile? profile, out CredentialSecret? credentials) != null
                || profile is null || credentials is null)
            {
                return false;
            }

            if (_sourceSettings != null && SourceNeedsPassword(LoadSaved(_sourceSettings, out ConnectionProfile? sourceProfile, out CredentialSecret? sourceCredentials), sourceProfile, sourceCredentials))
            {
                return false;
            }

            Start();
            return true;
        }

        /// <summary>
        /// Запуск: открыть сохранённые подключения. Приёмника нет или его пароль не запомнен — окно «Приёмник»; источник
        /// по EWS запомнен без пароля — окно «Источник» (как обещает отметка «Запомнить»). Добавленная почта — последней:
        /// выбранным в списке очистки остаётся приёмник.
        /// </summary>
        public void Start()
        {
            // Один раз: повторный вызов (окно загружено ещё раз) не должен снова открывать подключения и окна.
            if (_settings is null || _started)
            {
                return;
            }

            _started = true;

            LoadOutlookCalendars();
            ConnectionProfile? sourceProfile = null;
            CredentialSecret? sourceCredentials = null;
            string? sourceError = null;
            if (_sourceSettings != null)
            {
                sourceError = LoadSaved(_sourceSettings, out sourceProfile, out sourceCredentials);
                if (sourceProfile != null && sourceCredentials != null && sourceError is null)
                {
                    ApplySource(_connector!.Open(sourceProfile, sourceCredentials));
                }
            }

            string? loadError = LoadSaved(_settings, out ConnectionProfile? profile, out CredentialSecret? credentials);
            if (profile != null && credentials != null && loadError is null)
            {
                ApplyTarget(_connector!.Open(profile, credentials));
            }
            else
            {
                Edit(_settings, profile, credentials, loadError, TargetTitle, ApplyTarget);
            }

            if (_sourceSettings != null && SourceNeedsPassword(sourceError, sourceProfile, sourceCredentials))
            {
                Edit(_sourceSettings, sourceProfile, sourceCredentials, sourceError, SourceTitle, ApplySource);
            }

            LoadMailboxes();
        }

        /// <summary>Источник по EWS настроен, но открыть его без человека нельзя: пароль не запомнен или настройки не прочитались.</summary>
        private static bool SourceNeedsPassword(string? loadError, ConnectionProfile? profile, CredentialSecret? credentials) =>
            loadError != null || (profile != null && credentials is null);

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
            Edit(_settings!, profile, credentials, loadError, TargetTitle, ApplyTarget);
        }

        private void EditSource()
        {
            string? loadError = LoadSaved(_sourceSettings!, out ConnectionProfile? profile, out CredentialSecret? credentials);
            Edit(_sourceSettings!, profile, credentials, loadError, SourceTitle, ApplySource);
        }

        /// <summary>
        /// Запомненная почта открывается без сети, как приёмник. Почта без пароля (его удалили в Windows, папку программы
        /// перенесли на другой компьютер) остаётся в списке с пометкой «нужен пароль» — её можно изменить или убрать.
        /// Нечитаемый файл пропускается и остаётся на диске.
        /// </summary>
        private void LoadMailboxes()
        {
            IReadOnlyList<ISettingsStore> saved;
            try
            {
                saved = _mailboxStores?.Saved() ?? Array.Empty<ISettingsStore>();
            }
#pragma warning disable CA1031 // Папка недоступна — список очистки без добавленной почты, программа работает.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            foreach (ISettingsStore store in saved)
            {
                string? error = LoadSaved(store, out ConnectionProfile? profile, out CredentialSecret? credentials);
                if (profile is null)
                {
                    continue;
                }

                if (credentials is null || error != null)
                {
                    _added.Add(new CleanupChoice(LockedLabel(profile), null, isAdded: true, store: store, profile: profile));
                    continue;
                }

                ITargetSession session = _connector!.Open(profile, credentials);
                _added.Add(new CleanupChoice(AddedLabel(session), session, isAdded: true, store: store, profile: profile));
            }

            RebuildCleanupChoices();
        }

        private void AddMailbox()
        {
            ISettingsStore store = _mailboxStores!.CreateNew();
            var connection = new ConnectionViewModel(
                store, _connector!, _dialogs, _localOffset(), null, null, null, MailboxTitle, keepProfileWithoutPassword: false);
            if (!_connectionDialog!.Show(connection) || connection.OpenedSession is null)
            {
                return;
            }

            ITargetSession session = connection.OpenedSession;

            // Этот ящик уже в списке — выбирается он, второй такой же не нужен.
            CleanupChoice? same = _cleanupChoices.FirstOrDefault(
                c => c.Session != null && string.Equals(c.Session.TargetLabel, session.TargetLabel, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                session.Dispose();
                if (connection.Remember)
                {
                    TryDelete(store, out _);
                }

                CleanupChoice = same;
                return;
            }

            var choice = new CleanupChoice(
                AddedLabel(session), session, isAdded: true, store: connection.Remember ? store : null, profile: connection.OpenedProfile,
                userName: connection.OpenedUserName);
            _added.Add(choice);
            RebuildCleanupChoices();
            CleanupChoice = choice;
        }

        /// <summary>
        /// «Изменить…» добавленной почты: другой пароль, учётная запись, адрес или отметка «Запомнить». Окно открывается
        /// заполненным; сохранённое заменяет прежнее подключение этой строки списка.
        /// </summary>
        private void EditMailbox()
        {
            CleanupChoice? choice = _cleanupChoice;
            if (choice is null || !choice.IsAdded)
            {
                return;
            }

            ISettingsStore store = choice.Store ?? _mailboxStores!.CreateNew();
            ConnectionProfile? profile = choice.Profile;
            CredentialSecret? credentials = null;
            string? loadError = null;
            if (choice.Store != null)
            {
                loadError = LoadSaved(store, out ConnectionProfile? saved, out credentials);
                profile = saved ?? profile;
            }

            var connection = new ConnectionViewModel(
                store, _connector!, _dialogs, _localOffset(), profile, credentials, loadError, MailboxTitle,
                credentials is null ? choice.UserName : null, keepProfileWithoutPassword: false);
            if (!_connectionDialog!.Show(connection) || connection.OpenedSession is null || IsBusy)
            {
                connection.OpenedSession?.Dispose();
                return;
            }

            ITargetSession session = connection.OpenedSession;
            var replacement = new CleanupChoice(
                AddedLabel(session), session, isAdded: true, store: connection.Remember ? store : null, profile: connection.OpenedProfile,
                userName: connection.OpenedUserName);
            int index = _added.IndexOf(choice);
            if (index < 0)
            {
                session.Dispose();
                return;
            }

            _added[index] = replacement;
            RebuildCleanupChoices();
            CleanupChoice = replacement;
            choice.Session?.Dispose();
        }

        private void RemoveMailbox()
        {
            CleanupChoice? choice = _cleanupChoice;
            if (choice is null || !choice.IsAdded)
            {
                return;
            }

            string name = choice.Session?.TargetLabel ?? MailboxName(choice.Profile);
            string question = "Почта " + name + " больше не будет в списке очистки."
                + (choice.Store is null ? string.Empty : " Её сохранённый пароль будет удалён из Windows.")
                + "\n\nКалендарь ящика не меняется.";
            if (!_dialogs.Ask("Убрать почту из списка?", question, "Убрать") || IsBusy)
            {
                return;
            }

            string? error = null;
            if (choice.Store != null && !TryDelete(choice.Store, out error))
            {
                Cleanup.ShowError(new Messages.UserMessage(
                    "Почта не убрана из списка.",
                    "Не удалось удалить её настройки. Проверьте права на папку программы и попробуйте ещё раз.",
                    error));
                return;
            }

            _added.Remove(choice);
            RebuildCleanupChoices();
            choice.Session?.Dispose();
        }

        private static string AddedLabel(ITargetSession session) => "Почта: " + session.TargetLabel;

        private static string LockedLabel(ConnectionProfile profile) => "Почта: " + MailboxName(profile) + " — нужен пароль («Изменить…»)";

        private static string MailboxName(ConnectionProfile? profile) => profile is null
            ? "без адреса"
            : (profile.MailboxAddress ?? "ящик учётной записи") + " на " + ServerAddress.ToDisplay(profile.EwsUrl);

        private static bool TryDelete(ISettingsStore store, out string? error)
        {
            try
            {
                store.Delete();
                error = null;
                return true;
            }
#pragma warning disable CA1031 // Файл занят, нет прав, диспетчер учётных данных недоступен — сообщается, программа работает.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex.Message;
                return false;
            }
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
                settings, _connector!, _dialogs, _localOffset(), profile, credentials, loadError, title,
                credentials is null ? settings.LoadUserName() : null);
            if (_connectionDialog!.Show(connection) && connection.OpenedSession != null)
            {
                ITargetSession session = connection.OpenedSession;
                try
                {
                    apply(session);
                }
                catch (Exception)
                {
                    // Не применилось (операция успела начаться) — новое подключение не должно остаться открытым.
                    session.Dispose();
                    throw;
                }
            }
        }

        private void ApplyTarget(ITargetSession session)
        {
            // Сначала то, что может отказать (операция идёт), — затем состояние окна: без полупримененного подключения.
            Copy.SetTarget(session);
            ITargetSession? previous = _session;
            bool followed = previous != null && ReferenceEquals(_cleanupChoice?.Session, previous);
            _session = session;
            TargetLabel = session.TargetLabel;
            if (session.IsDemo)
            {
                Copy.SetEwsSource(session);
            }

            RebuildCleanupChoices(followed ? session : null);
            OnPropertyChanged(nameof(IsDemo));

            // Прежний сеанс больше не нужен: его соединения и проверка сертификата освобождаются.
            previous?.Dispose();
        }

        private void ApplySource(ITargetSession session)
        {
            Copy.SetEwsSource(session);
            ITargetSession? previous = _sourceSession;
            bool followed = previous != null && ReferenceEquals(_cleanupChoice?.Session, previous);
            _sourceSession = session;
            RebuildCleanupChoices(followed ? session : null);
            previous?.Dispose();
        }

        /// <summary>
        /// Список очистки заново (подключение сменилось, почту добавили или убрали). Выбор остаётся на том же ящике, а при
        /// замене его подключения (<paramref name="follow"/>) — переходит на новое; тот же ящик не сбрасывает открытый
        /// предпросмотр. Выбранного больше нет — выбирается первый.
        /// </summary>
        private void RebuildCleanupChoices(ITargetSession? follow = null)
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

            choices.AddRange(_added);

            CleanupChoice? previous = _cleanupChoice;
            ITargetSession? keep = follow ?? previous?.Session;
            CleanupChoices = choices;
            CleanupChoice? selected = choices.FirstOrDefault(c => ReferenceEquals(c, previous))
                ?? (keep is null ? null : choices.FirstOrDefault(c => ReferenceEquals(c.Session, keep)))
                ?? choices.FirstOrDefault();

            bool sameMailbox = previous != null && selected != null
                && (ReferenceEquals(selected, previous) || (selected.Session != null && ReferenceEquals(selected.Session, previous.Session)));
            if (sameMailbox)
            {
                _cleanupChoice = selected;
                OnPropertyChanged(nameof(CleanupChoice));
                RaiseMailboxCommands();
                return;
            }

            _cleanupChoice = null;
            if (selected != null)
            {
                CleanupChoice = selected;
            }
            else
            {
                Cleanup.SetSession(null);
                OnPropertyChanged(nameof(CleanupChoice));
                RaiseMailboxCommands();
            }
        }

        private void RaiseMailboxCommands()
        {
            OnPropertyChanged(nameof(CanRemoveMailbox));
            EditMailboxCommand.RaiseCanExecuteChanged();
            RemoveMailboxCommand.RaiseCanExecuteChanged();
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
                    if (ReferenceEquals(sender, Copy))
                    {
                        Cleanup.OtherBusyChanged();
                    }

                    OnPropertyChanged(nameof(IsBusy));
                    EditConnectionCommand.RaiseCanExecuteChanged();
                    EditSourceCommand.RaiseCanExecuteChanged();
                    AddMailboxCommand.RaiseCanExecuteChanged();
                    RemoveMailboxCommand.RaiseCanExecuteChanged();
                }
            }

            Cleanup.PropertyChanged += OnChanged;
            Copy.PropertyChanged += OnChanged;
        }
    }
}
