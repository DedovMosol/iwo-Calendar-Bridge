using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Transport;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Окно «Подключение»: сервер, ящик, учётная запись, пароль, часовой пояс; проверка подключения с показом
    /// сертификата; сохранение.
    ///
    /// Сервер — одно поле, принимающее любой вариант адреса: внутренний (в сети организации) или внешний (из
    /// интернета), имя, IP, с портом или полный адрес EWS (<see cref="ServerAddress"/>). При смене сервера доверие к
    /// сертификату прежнего сервера сбрасывается — у другого сервера другой сертификат.
    ///
    /// «Сохранить» сначала проверяет подключение. Если сервер недоступен (например, внутренний адрес из дома),
    /// программа спрашивает, сохранить ли настройки без проверки. Если сертификату сервера Windows не доверяет
    /// (обычно для собственного сертификата Exchange), окно показывает его, и пользователь решает, доверять ли.
    ///
    /// Пароль приходит из окна методом <see cref="SetPassword"/> (поле пароля WPF не связывается с данными намеренно)
    /// и хранится только в диспетчере учётных данных Windows. Пустое поле пароля при сохранённом пароле той же
    /// учётной записи означает «не менять».
    /// </summary>
    public sealed class ConnectionViewModel : ObservableObject
    {
        private readonly ISettingsStore _settings;
        private readonly ITargetConnector _connector;
        private readonly IDialogService _dialogs;
        private readonly ConnectionProfile? _savedProfile;
        private readonly CredentialSecret? _savedCredentials;

        private string _server = string.Empty;
        private string _alternateServer = string.Empty;
        private string _mailbox = string.Empty;
        private string _userName = string.Empty;
        private string _password = string.Empty;
        private OffsetOption? _offset;
        private string? _serverError;
        private string? _alternateServerError;
        private string? _mailboxError;
        private string? _userError;
        private string? _passwordError;
        private string? _offsetError;
        private bool _isChecking;
        private string? _statusTitle;
        private string? _statusHint;
        private bool _statusIsSuccess;
        private string? _statusDetails;
        private ConnectionCheckResult? _lastCheck;
        private string? _lastCheckKey;
        private CancellationTokenSource? _cancellation;
        private bool _closed;

        // Доверие к сертификату принадлежит адресу (хост:порт, ServerEndpoint.Key), а не окну и не полю: у основного и
        // резервного адреса оно своё; при смене адреса отпечаток прежнего не теряется и восстанавливается при возврате
        // к нему. Так опечатка в адресе не стирает закрепление, а сертификат одного сервера не навязывается другому.
        private readonly Dictionary<string, string?> _pins = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <param name="loadError">Ошибка чтения прежних настроек — показывается вверху окна.</param>
        public ConnectionViewModel(
            ISettingsStore settings,
            ITargetConnector connector,
            IDialogService dialogs,
            TimeSpan localOffset,
            ConnectionProfile? savedProfile,
            CredentialSecret? savedCredentials,
            string? loadError = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _savedProfile = savedProfile;
            _savedCredentials = savedCredentials;
            LoadError = loadError;

            OffsetOptions = OffsetOption.All(localOffset, savedProfile?.TimeZone.Offset);
            if (savedProfile != null)
            {
                _server = ServerAddress.ToDisplay(savedProfile.EwsUrl);
                _alternateServer = savedProfile.AlternateEwsUrl is null ? string.Empty : ServerAddress.ToDisplay(savedProfile.AlternateEwsUrl);
                _mailbox = savedProfile.MailboxAddress ?? string.Empty;
                _offset = OffsetOptions.First(o => o.Offset == savedProfile.TimeZone.Offset);
                foreach (ServerEndpoint endpoint in savedProfile.Endpoints)
                {
                    _pins[endpoint.Key] = endpoint.PinnedCertificateThumbprint;
                }
            }

            _userName = savedCredentials?.UserName ?? string.Empty;

            CheckCommand = new AsyncCommand(CheckAsync, () => !_isChecking);
            SaveCommand = new AsyncCommand(SaveAsync, () => !_isChecking);
            TrustCertificateCommand = new RelayCommand(TrustCertificate, () => CanTrustCertificate && !_isChecking);
            ForgetCertificateCommand = new RelayCommand(ForgetCertificate, () => HasPinnedCertificate && !_isChecking);
            CancelCheckCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _isChecking);
        }

        /// <summary>Окно закрывается с результатом «сохранено» — подписывается окно.</summary>
        public event EventHandler? Saved;

        public AsyncCommand CheckCommand { get; }

        public AsyncCommand SaveCommand { get; }

        public RelayCommand TrustCertificateCommand { get; }

        public RelayCommand ForgetCertificateCommand { get; }

        public RelayCommand CancelCheckCommand { get; }

        public IReadOnlyList<OffsetOption> OffsetOptions { get; }

        public string? LoadError { get; }

        public bool HasLoadError => LoadError != null;

        /// <summary>Сеанс, открытый после сохранения; <c>null</c> — окно закрыто без сохранения.</summary>
        public ITargetSession? OpenedSession { get; private set; }

        // ---- Поля ----

        public string Server
        {
            get => _server;
            set
            {
                if (SetProperty(ref _server, value ?? string.Empty))
                {
                    ServerError = null;
                    FieldChanged();
                }
            }
        }

        /// <summary>
        /// Необязательный резервный адрес того же сервера — в любом виде, как и основной. Пусто — резерва нет.
        /// Программа переходит на него, только если основной недоступен по сети.
        /// </summary>
        public string AlternateServer
        {
            get => _alternateServer;
            set
            {
                if (SetProperty(ref _alternateServer, value ?? string.Empty))
                {
                    AlternateServerError = null;
                    FieldChanged();
                }
            }
        }

        public string Mailbox
        {
            get => _mailbox;
            set
            {
                if (SetProperty(ref _mailbox, value ?? string.Empty))
                {
                    MailboxError = null;
                    FieldChanged();
                }
            }
        }

        public string UserName
        {
            get => _userName;
            set
            {
                if (SetProperty(ref _userName, value ?? string.Empty))
                {
                    UserError = null;
                    OnPropertyChanged(nameof(PasswordHint));
                    FieldChanged();
                }
            }
        }

        public OffsetOption? Offset
        {
            get => _offset;
            set
            {
                if (SetProperty(ref _offset, value))
                {
                    OffsetError = null;
                }
            }
        }

        /// <summary>Подсказка под полем пароля.</summary>
        public string PasswordHint => KeepsSavedPassword
            ? "Пароль сохранён в Windows. Оставьте поле пустым, чтобы не менять его."
            : "Хранится только в диспетчере учётных данных Windows, не в файлах программы.";

        public string? ServerError
        {
            get => _serverError;
            private set => SetProperty(ref _serverError, value);
        }

        public string? AlternateServerError
        {
            get => _alternateServerError;
            private set => SetProperty(ref _alternateServerError, value);
        }

        public string? MailboxError
        {
            get => _mailboxError;
            private set => SetProperty(ref _mailboxError, value);
        }

        public string? UserError
        {
            get => _userError;
            private set => SetProperty(ref _userError, value);
        }

        public string? PasswordError
        {
            get => _passwordError;
            private set => SetProperty(ref _passwordError, value);
        }

        public string? OffsetError
        {
            get => _offsetError;
            private set => SetProperty(ref _offsetError, value);
        }

        /// <summary>Пароль из поля ввода (вызывается окном при каждом изменении поля).</summary>
        public void SetPassword(string password)
        {
            _password = password ?? string.Empty;
            PasswordError = null;
            FieldChanged();
        }

        // ---- Состояние проверки ----

        public bool IsChecking
        {
            get => _isChecking;
            private set
            {
                if (SetProperty(ref _isChecking, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                    RefreshCommands();
                }
            }
        }

        public bool IsIdle => !_isChecking;

        public string? StatusTitle
        {
            get => _statusTitle;
            private set
            {
                if (SetProperty(ref _statusTitle, value))
                {
                    OnPropertyChanged(nameof(HasStatus));
                }
            }
        }

        public string? StatusHint
        {
            get => _statusHint;
            private set => SetProperty(ref _statusHint, value);
        }

        public string? StatusDetails
        {
            get => _statusDetails;
            private set => SetProperty(ref _statusDetails, value);
        }

        public bool HasStatus => _statusTitle != null;

        public bool StatusIsSuccess
        {
            get => _statusIsSuccess;
            private set => SetProperty(ref _statusIsSuccess, value);
        }

        // ---- Сертификат ----

        public bool HasCertificate => _lastCheck?.Certificate != null;

        public string CertificateSubject => _lastCheck?.Certificate?.Subject ?? string.Empty;

        public string CertificateIssuer => _lastCheck?.Certificate is null
            ? string.Empty
            : _lastCheck.Certificate.IsSelfSigned ? "Самоподписанный (выдан сам себе)" : _lastCheck.Certificate.Issuer;

        public string CertificateValidity => _lastCheck?.Certificate is null
            ? string.Empty
            : "с " + RussianText.Date(_lastCheck.Certificate.NotBeforeUtc.ToLocalTime())
              + " по " + RussianText.Date(_lastCheck.Certificate.NotAfterUtc.ToLocalTime())
              + (_lastCheck.Certificate.IsValidAt(DateTime.UtcNow) ? string.Empty : " — срок действия истёк");

        /// <summary>SHA-256 группами по 4 символа — так его удобно сверять с администратором по телефону.</summary>
        public string CertificateSha256 => _lastCheck?.Certificate is null ? string.Empty : Group(_lastCheck.Certificate.Fingerprint.Sha256);

        public string CertificateSha1 => _lastCheck?.Certificate is null ? string.Empty : Group(_lastCheck.Certificate.Fingerprint.Sha1);

        /// <summary>
        /// Можно ли нажать «Доверять этому сертификату»: Windows ему не доверяет либо он не совпал с прежним.
        /// Перехват антивирусом доверием не лечится — только исключением в антивирусе.
        /// </summary>
        public bool CanTrustCertificate => _lastCheck?.Certificate != null
            && (_lastCheck.Decision == CertificateDecision.RejectUntrusted
                || _lastCheck.Decision == CertificateDecision.RejectPinMismatch);

        /// <summary>Хотя бы у одного из введённых адресов есть запомненный сертификат — показывается «Забыть сертификат».</summary>
        public bool HasPinnedCertificate => CurrentKeys().Any(k => PinOf(k) != null);

        /// <summary>
        /// Состояние доверия под полем «Сертификат»: для одного адреса — одной строкой, для двух — по строке на адрес
        /// (у каждого свой сертификат).
        /// </summary>
        public string TrustText
        {
            get
            {
                string[] keys = CurrentKeys();
                if (keys.Length < 2)
                {
                    string? pin = keys.Length == 0 ? null : PinOf(keys[0]);
                    return pin is null
                        ? "Не задан. При первом подключении программа запомнит сертификат сервера."
                        : "Программа доверяет сертификату с отпечатком " + Group(pin) + ".";
                }

                return "Основной адрес: " + DescribePin(PinOf(keys[0])) + "\nРезервный адрес: " + DescribePin(PinOf(keys[1]));
            }
        }

        // ---- Действия ----

        private async Task CheckAsync()
        {
            await RunCheckAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Окно закрывается (Отмена, крестик): идущая проверка отменяется, и никакое продолжение уже ничего не сохранит —
        /// даже если ответ сервера пришёл в ту же секунду. Повторный вызов безопасен.
        /// </summary>
        public void Close()
        {
            _closed = true;
            _cancellation?.Cancel();
        }

        /// <summary>
        /// Проверка всех адресов и сохранение. Правило:
        ///  - адрес ответил, но с ошибкой (вход, права, сертификат, ящик) — не сохраняем: ошибка видна в окне;
        ///  - ни один адрес не доступен по сети — вопрос «сохранить без проверки?»;
        ///  - иначе сохраняем; недоступный сейчас адрес (например, внутренний вне офиса) этому не мешает.
        /// Принятые при первом подключении сертификаты запоминаются вместе с настройками — каждый для своего адреса.
        /// </summary>
        private async Task SaveAsync()
        {
            IReadOnlyList<EndpointCheck>? checks = await RunCheckAsync().ConfigureAwait(true);
            if (checks is null || _closed)
            {
                return;
            }

            if (checks.Any(c => c.Answered && !c.Result.Succeeded))
            {
                return;
            }

            if (checks.All(c => !c.Answered)
                && (!_dialogs.Ask("Сервер недоступен", "Не удалось подключиться к серверу. Сохранить настройки без проверки?\n\n"
                        + "Так можно сделать, если сервер сейчас недоступен из этой сети (например, внутренний адрес вне "
                        + "офиса).")
                    || _closed))
            {
                return;
            }

            foreach (EndpointCheck check in checks.Where(c => c.Result.Succeeded && c.Result.PinnedThumbprint != null))
            {
                _pins[check.Key] = check.Result.PinnedThumbprint;
            }

            RaiseTrustProperties();
            if (!TryBuild(out ConnectionProfile? profile, out CredentialSecret? credentials))
            {
                return;
            }

            try
            {
                // Сначала пароль, затем файл: если диспетчер учётных данных недоступен, на диске не окажется нового
                // сервера со старым паролем, а сообщение «не сохранено» будет правдой.
                _settings.SaveCredentials(credentials!);
                _settings.SaveProfile(profile!);
            }
#pragma warning disable CA1031 // Нет прав на папку, диск, диспетчер учётных данных — показываем, окно не закрываем.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ShowStatus(false, "Настройки не сохранены.", "Проверьте права на папку программы и свободное место на диске.", ex.Message);
                return;
            }

            OpenedSession = _connector.Open(profile!, credentials!);
            Saved?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Проверка подключения по текущим полям — каждого адреса отдельно (у каждого свой сертификат и своя
        /// доступность). <c>null</c> — поля заполнены неверно или проверка отменена.
        /// </summary>
        private async Task<IReadOnlyList<EndpointCheck>?> RunCheckAsync()
        {
            if (!TryBuild(out ConnectionProfile? profile, out CredentialSecret? credentials))
            {
                return null;
            }

            IsChecking = true;
            ShowStatus(false, "Проверяю подключение…", "Подключаюсь к серверу и открываю календарь.", null);
            using (_cancellation = new CancellationTokenSource())
            {
                try
                {
                    var checks = new List<EndpointCheck>();
                    foreach (ServerEndpoint endpoint in profile!.Endpoints)
                    {
                        ConnectionCheckResult result = await _connector
                            .CheckAsync(profile.ForEndpoint(endpoint), credentials!, _cancellation.Token)
                            .ConfigureAwait(true);
                        checks.Add(new EndpointCheck(endpoint, result));
                    }

                    ShowChecks(checks);
                    return checks;
                }
                catch (OperationCanceledException)
                {
                    ShowStatus(false, "Проверка отменена.", "Настройки не изменены.", null);
                    return null;
                }
#pragma warning disable CA1031 // Любой сбой подключения показывается в окне, окно остаётся рабочим.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    UserMessage message = ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown, ex.Message);
                    ShowStatus(false, message.Title, message.Hint, message.Details);
                    return null;
                }
                finally
                {
                    _cancellation = null;
                    IsChecking = false;
                }
            }
        }

        /// <summary>
        /// Итог проверки. Главное — ошибка адреса, который ответил (её надо исправить); затем — работает ли хоть один
        /// адрес. Карточка сертификата — того адреса, о котором сообщение (кнопка «Доверять» относится к нему).
        /// </summary>
        private void ShowChecks(IReadOnlyList<EndpointCheck> checks)
        {
            bool twoAddresses = checks.Count > 1;
            EndpointCheck? failed = checks.FirstOrDefault(c => c.Answered && !c.Result.Succeeded);
            EndpointCheck? working = checks.FirstOrDefault(c => c.Result.Succeeded);
            EndpointCheck shown = failed ?? working ?? checks[0];
            _lastCheck = shown.Result;
            _lastCheckKey = shown.Key;
            RaiseCertificateProperties();

            if (failed != null || working is null)
            {
                ConnectionCheckResult result = shown.Result;
                UserMessage message = (result.Decision.HasValue ? ErrorMessages.ForCertificate(result.Decision.Value) : null)
                    ?? result.Error
                    ?? ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown);
                string prefix = !twoAddresses ? string.Empty : working is null ? "Оба адреса: " : AddressName(shown) + ": ";
                ShowStatus(false, prefix + message.Title, message.Hint, result.Error?.Details);
                return;
            }

            string title = !twoAddresses
                ? "Подключение работает."
                : checks.All(c => c.Result.Succeeded)
                    ? "Подключение работает по обоим адресам."
                    : "Подключение работает по " + (working.Endpoint.IsAlternate ? "резервному" : "основному") + " адресу.";
            string unreachableNote = twoAddresses && !checks.All(c => c.Result.Succeeded)
                ? " " + (working.Endpoint.IsAlternate ? "Основной" : "Резервный") + " адрес сейчас недоступен — это нормально, если "
                  + "он работает только из другой сети."
                : string.Empty;
            string pinNote = checks.Any(c => c.Result.Decision == CertificateDecision.AcceptFirstUse)
                ? " Сертификат сервера будет запомнен при сохранении."
                : string.Empty;
            ShowStatus(
                true,
                title,
                "В календаре " + RussianText.Count(working.Result.ItemCount, "элемент", "элемента", "элементов") + "."
                    + unreachableNote + pinNote,
                null);
        }

        private static string AddressName(EndpointCheck check) => check.Endpoint.IsAlternate ? "Резервный адрес" : "Основной адрес";

        private void TrustCertificate()
        {
            PresentedCertificate? certificate = _lastCheck?.Certificate;
            if (certificate is null)
            {
                return;
            }

            if (_lastCheck!.Decision == CertificateDecision.RejectPinMismatch
                && !_dialogs.Ask(
                    "Доверять новому сертификату?",
                    "Сервер предъявил не тот сертификат, которому вы доверяли раньше. Продолжайте, только если администратор "
                    + "подтвердил замену сертификата и отпечаток совпадает:\n\n" + Group(certificate.Fingerprint.Sha256)))
            {
                return;
            }

            if (_lastCheckKey != null)
            {
                _pins[_lastCheckKey] = certificate.Fingerprint.Sha256;
            }

            RaiseTrustProperties();
            ShowStatus(
                true,
                "Сертификат будет считаться доверенным.",
                "Нажмите «Проверить подключение» или «Сохранить», чтобы подключиться с ним.",
                null);
            _lastCheck = null;
            RaiseCertificateProperties();
        }

        /// <summary>Забыть сертификаты введённых адресов: при следующем подключении их нужно будет подтвердить заново.</summary>
        private void ForgetCertificate()
        {
            foreach (string key in CurrentKeys())
            {
                _pins[key] = null;
            }

            RaiseTrustProperties();
        }

        private string? PinOf(string key) => _pins.TryGetValue(key, out string? pin) ? pin : null;

        private string DescribePin(string? pin) => pin is null
            ? "сертификат будет запомнен при первом подключении."
            : "доверие сертификату " + Group(pin) + ".";

        /// <summary>Ключи адресов из полей, которые уже разбираются (пока адрес набирается, его нет).</summary>
        private string[] CurrentKeys()
        {
            var keys = new List<string>(2);
            foreach (string text in new[] { _server, _alternateServer })
            {
                if (text.Trim().Length > 0 && ServerAddress.TryParse(text, out Uri? url, out _))
                {
                    keys.Add(ServerEndpoint.KeyOf(url!));
                }
            }

            return keys.ToArray();
        }

        private void RaiseTrustProperties()
        {
            OnPropertyChanged(nameof(TrustText));
            OnPropertyChanged(nameof(HasPinnedCertificate));
            RefreshCommands();
        }

        /// <summary>Собирает профиль и учётные данные из полей; ошибки — под соответствующими полями.</summary>
        private bool TryBuild(out ConnectionProfile? profile, out CredentialSecret? credentials)
        {
            profile = null;
            credentials = null;
            bool ok = true;

            if (!ServerAddress.TryParse(_server, out Uri? url, out string? serverError))
            {
                ServerError = serverError;
                ok = false;
            }

            Uri? alternateUrl = null;
            if (_alternateServer.Trim().Length > 0 && !ServerAddress.TryParse(_alternateServer, out alternateUrl, out string? alternateError))
            {
                AlternateServerError = alternateError;
                ok = false;
            }

            if (_offset is null)
            {
                OffsetError = "Выберите часовой пояс календаря: от него считаются границы периода очистки.";
                ok = false;
            }

            string user = _userName.Trim();
            if (user.Length == 0)
            {
                UserError = "Укажите учётную запись, например ДОМЕН\\пользователь или пользователь@домен.";
                ok = false;
            }

            string? password = _password.Length > 0 ? _password : KeepsSavedPassword ? _savedCredentials!.Password : null;
            if (password is null)
            {
                PasswordError = "Введите пароль.";
                ok = false;
            }

            if (!ok)
            {
                return false;
            }

            try
            {
                profile = new ConnectionProfile(
                    url!,
                    _mailbox,
                    _offset!.Offset,
                    PinOf(ServerEndpoint.KeyOf(url!)),
                    alternateUrl,
                    alternateUrl is null ? null : PinOf(ServerEndpoint.KeyOf(alternateUrl)));
            }
            catch (ArgumentException ex) when (ex.ParamName == "mailboxAddress")
            {
                MailboxError = "Адрес ящика должен иметь вид имя@домен. Оставьте поле пустым, чтобы работать со своим ящиком.";
                return false;
            }
            catch (ArgumentException ex) when (ex.ParamName == "alternateEwsUrl")
            {
                // Правило — в профиле (DRY); здесь только первая строка текста, без «Имя параметра: …».
                string message = ex.Message;
                int end = message.IndexOfAny(new[] { '\r', '\n' });
                AlternateServerError = end < 0 ? message : message.Substring(0, end);
                return false;
            }

            credentials = new CredentialSecret(user, password!);
            return true;
        }

        private bool KeepsSavedPassword => _savedCredentials != null
            && string.Equals(_savedCredentials.UserName, _userName.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Любое изменение полей делает прежний результат проверки устаревшим. Смена сервера сбрасывает доверие к
        /// сертификату: у другого сервера другой сертификат.
        /// </summary>
        private void FieldChanged()
        {
            // Доверие показывается для адресов из полей: при смене адреса подпись следует за ним.
            RaiseTrustProperties();

            if (_lastCheck != null || HasStatus)
            {
                _lastCheck = null;
                _lastCheckKey = null;
                RaiseCertificateProperties();
                ClearStatus();
            }
        }

        private void ShowStatus(bool success, string title, string hint, string? details)
        {
            StatusIsSuccess = success;
            StatusTitle = title;
            StatusHint = hint;
            StatusDetails = details;
        }

        private void ClearStatus()
        {
            StatusTitle = null;
            StatusHint = null;
            StatusDetails = null;
            StatusIsSuccess = false;
        }

        private void RaiseCertificateProperties()
        {
            OnPropertyChanged(nameof(HasCertificate));
            OnPropertyChanged(nameof(CertificateSubject));
            OnPropertyChanged(nameof(CertificateIssuer));
            OnPropertyChanged(nameof(CertificateValidity));
            OnPropertyChanged(nameof(CertificateSha256));
            OnPropertyChanged(nameof(CertificateSha1));
            OnPropertyChanged(nameof(CanTrustCertificate));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            CheckCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            TrustCertificateCommand.RaiseCanExecuteChanged();
            ForgetCertificateCommand.RaiseCanExecuteChanged();
            CancelCheckCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Итог проверки одного адреса.</summary>
        private sealed class EndpointCheck
        {
            public EndpointCheck(ServerEndpoint endpoint, ConnectionCheckResult result)
            {
                Endpoint = endpoint;
                Result = result;
            }

            public ServerEndpoint Endpoint { get; }

            public ConnectionCheckResult Result { get; }

            public string Key => Endpoint.Key;

            /// <summary>
            /// Адрес ответил (дошло до TLS): его ошибку надо исправить. Иначе — недоступен по сети, что для одного из
            /// адресов нормально (внутренний вне офиса).
            /// </summary>
            public bool Answered => Result.Succeeded || Result.Decision != null;
        }

        internal static string Group(string hex)
        {
            var groups = new List<string>();
            for (int i = 0; i < hex.Length; i += 4)
            {
                groups.Add(hex.Substring(i, Math.Min(4, hex.Length - i)));
            }

            return string.Join(" ", groups);
        }
    }
}
