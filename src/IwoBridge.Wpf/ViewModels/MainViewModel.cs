using System;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Главное окно: строка подключения (что очищается, кнопка «Подключение…») и экран очистки.
    ///
    /// При запуске читает сохранённые настройки. Если их нет или они повреждены — сразу открывает окно
    /// «Подключение»; иначе открывает сеанс без обращения к сети (сервер проверяется при первом действии).
    /// Во время операции окно «Подключение» недоступно, чтобы не сменить ящик посреди удаления.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly ISettingsStore? _settings;
        private readonly ITargetConnector? _connector;
        private readonly IDialogService _dialogs;
        private readonly IConnectionDialog? _connectionDialog;
        private readonly Func<TimeSpan> _localOffset;
        private string _targetLabel = "Подключение не настроено";
        private ITargetSession? _session;

        /// <summary>Обычный режим: настройки, подключение по EWS.</summary>
        public MainViewModel(
            ISettingsStore settings,
            ITargetConnector connector,
            IDialogService dialogs,
            IConnectionDialog connectionDialog,
            Func<TimeSpan> localOffset)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _connectionDialog = connectionDialog ?? throw new ArgumentNullException(nameof(connectionDialog));
            _localOffset = localOffset ?? throw new ArgumentNullException(nameof(localOffset));
            Cleanup = new CleanupViewModel(dialogs);
            EditConnectionCommand = new RelayCommand(EditConnection, () => !Cleanup.IsBusy);
            Cleanup.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupViewModel.IsBusy))
                {
                    EditConnectionCommand.RaiseCanExecuteChanged();
                }
            };
        }

        /// <summary>Демонстрационный режим: готовый сеанс, без настроек и сети.</summary>
        public MainViewModel(ITargetSession demoSession, IDialogService dialogs)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _localOffset = () => TimeSpan.Zero; // окна «Подключение» в демонстрационном режиме нет
            Cleanup = new CleanupViewModel(dialogs);
            EditConnectionCommand = new RelayCommand(() => { }, () => false);
            ApplySession(demoSession ?? throw new ArgumentNullException(nameof(demoSession)));
        }

        public CleanupViewModel Cleanup { get; }

        public RelayCommand EditConnectionCommand { get; }

        public bool IsDemo => Cleanup.IsDemo;

        /// <summary>Что очищается — постоянно видно в шапке окна.</summary>
        public string TargetLabel
        {
            get => _targetLabel;
            private set => SetProperty(ref _targetLabel, value);
        }

        /// <summary>Запуск: открыть сохранённое подключение или сразу предложить настроить его.</summary>
        public void Start()
        {
            if (_settings is null)
            {
                return;
            }

            string? loadError = LoadSaved(out ConnectionProfile? profile, out CredentialSecret? credentials);
            if (profile != null && credentials != null && loadError is null)
            {
                ApplySession(_connector!.Open(profile, credentials));
                return;
            }

            EditConnection(profile, credentials, loadError);
        }

        private void EditConnection()
        {
            string? loadError = LoadSaved(out ConnectionProfile? profile, out CredentialSecret? credentials);
            EditConnection(profile, credentials, loadError);
        }

        /// <summary>Сохранённые настройки; возвращает текст ошибки чтения или <c>null</c>.</summary>
        private string? LoadSaved(out ConnectionProfile? profile, out CredentialSecret? credentials)
        {
            profile = null;
            credentials = null;
            try
            {
                profile = _settings!.LoadProfile();
                credentials = _settings.LoadCredentials();
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

        private void EditConnection(ConnectionProfile? profile, CredentialSecret? credentials, string? loadError)
        {
            var connection = new ConnectionViewModel(
                _settings!, _connector!, _dialogs, _localOffset(), profile, credentials, loadError);
            if (_connectionDialog!.Show(connection) && connection.OpenedSession != null)
            {
                ApplySession(connection.OpenedSession);
            }
        }

        private void ApplySession(ITargetSession session)
        {
            ITargetSession? previous = _session;
            Cleanup.SetSession(session);
            _session = session;
            TargetLabel = session.TargetLabel;
            OnPropertyChanged(nameof(IsDemo));

            // Прежний сеанс больше не нужен: его соединения и проверка сертификата освобождаются.
            previous?.Dispose();
        }
    }
}
