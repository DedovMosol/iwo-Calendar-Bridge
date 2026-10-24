using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Security;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Transport;
using IwoBridge.Ews.Mapping;
using IwoBridge.Ews.Transport;
using Microsoft.Exchange.WebServices.Data;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Подключение к приёмнику по EWS — единственная точка входа в адаптер для приложения: создаёт сервис со своей
    /// группой соединений и своей проверкой сертификата и отдаёт шлюз, реализующий порты ядра. Снаружи не видно ни
    /// одного типа EWS Managed API, поэтому окно и сценарии от него не зависят (проверяется архитектурным тестом).
    ///
    /// Как пользоваться:
    /// <code>
    /// using (var target = new EwsTarget(profile, credentials))   // профиль из настроек, пароль — из диспетчера
    /// {
    ///     int count = await target.CheckAsync(ct);                // проверка подключения
    ///     // target.Calendar — порты чтения и удаления для CleanupRunner
    /// }
    /// </code>
    ///
    /// Подключений в процессе может быть несколько одновременно (сеанс очистки и проверка в окне настроек): у каждого
    /// свой пул TLS-соединений и своё решение о доверии (<see cref="TlsTrustRegistry"/>), друг другу они не мешают.
    /// Если в профиле есть резервный адрес, шлюз переходит на него, когда основной недоступен по сети
    /// (<see cref="EwsCalendarGateway(System.Collections.Generic.IReadOnlyList{Microsoft.Exchange.WebServices.Data.ExchangeService}, string?, RetryPolicy?)"/>).
    /// <see cref="Dispose"/> снимает проверку с учёта и закрывает соединения подключения.
    /// </summary>
    public sealed class EwsTarget : IDisposable
    {
        private readonly Endpoint[] _endpoints;
        private int _disposed;

        /// <summary>
        /// Подключение по адресам профиля: основному и, если задан, резервному (<see cref="ConnectionProfile.Endpoints"/>).
        /// У каждого адреса — своя группа соединений и своя проверка сертификата со своим закреплением.
        /// </summary>
        public EwsTarget(ConnectionProfile profile, CredentialSecret credentials)
            : this(profile, credentials, null)
        {
        }

        /// <summary>
        /// То же подключение, но вместо основного календаря ящика — указанная папка календаря. Для живых проверок:
        /// запись идёт во временную папку, основной календарь не трогается.
        /// </summary>
        internal EwsTarget(ConnectionProfile profile, CredentialSecret credentials, FolderId? calendarFolder)
        {
            if (profile is null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (credentials is null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            var factory = new ExchangeServiceFactory();
            _endpoints = profile.Endpoints.Select(e => new Endpoint(e)).ToArray();
            var retry = new RetryPolicy(EwsErrorMapper.MapException, EwsErrorMapper.ServerBackOff);
            FolderId calendar = calendarFolder ?? EwsCalendarGateway.CalendarOf(profile.MailboxAddress);

            // Чтение — в UTC; запись событий — в поясе календаря ящика (CalendarZones, T31). У обоих каналов те же
            // группы соединений адресов, поэтому и та же проверка сертификата.
            var read = new EwsChannel(
                _endpoints.Select(e => factory.Create(profile.ForEndpoint(e.Settings), credentials, e.ConnectionGroup)).ToArray(), retry);
            TimeZoneInfo calendarZone = CalendarZones.ForOffset(profile.TimeZone.Offset);
            var write = new EwsChannel(
                _endpoints.Select(e => factory.Create(profile.ForEndpoint(e.Settings), credentials, e.ConnectionGroup, calendarZone)).ToArray(), retry);

            ReadChannel = read;
            Calendar = new EwsCalendarGateway(read, calendar);
            CopyTarget = new EwsCopyTarget(read, write, calendar, Calendar, calendarZone);
            string mailbox = profile.MailboxAddress ?? credentials.UserName;
            Source = new EwsSource(mailbox + " на " + ServerAddress.ToDisplay(profile.EwsUrl), read, calendar, Calendar);
            foreach (Endpoint endpoint in _endpoints)
            {
                TlsTrustRegistry.Register(endpoint.ConnectionGroup, endpoint.Validator);
            }
        }

        /// <summary>Канал запросов в UTC (с проверкой сертификата и переходом на резервный адрес) — для живых проверок.</summary>
        internal EwsChannel ReadChannel { get; }

        /// <summary>Основной календарь целевого ящика: порты чтения, данных серий, раскрытия окна и удаления.</summary>
        public EwsCalendarGateway Calendar { get; }

        /// <summary>Этот календарь как приёмник копирования: копии моста, создание, перезапись, удаление.</summary>
        public ICopyTarget CopyTarget { get; }

        /// <summary>Этот календарь как источник копирования (только чтение).</summary>
        public ISourceCalendar Source { get; }

        /// <summary>Ошибка всего подключения — копирование на ней останавливается (<see cref="ErrorClassifier.StopsBatch"/>).</summary>
        public static bool StopsBatch(Exception exception) => ErrorClassifier.StopsBatch(Classify(exception));

        /// <summary>Резервный ли адрес сейчас в работе (основной был недоступен).</summary>
        public bool UsesAlternate => Active.Settings.IsAlternate;

        /// <summary>
        /// Последний предъявленный сертификат текущего адреса — для показа пользователю (в том числе отклонённый).
        /// </summary>
        public PresentedCertificate? PresentedCertificate => Active.Validator.LastPresented;

        /// <summary>Решение по последнему сертификату текущего адреса; <c>null</c> — до TLS дело не дошло.</summary>
        public CertificateDecision? CertificateDecision => Active.Validator.LastDecision;

        /// <summary>
        /// Закреплённый отпечаток текущего адреса: из профиля либо принятый при первом подключении (TOFU). Если он
        /// отличается от профиля, приложение предлагает сохранить его в настройки.
        /// </summary>
        public string? PinnedThumbprint => Active.Validator.PinnedThumbprint;

        private Endpoint Active => _endpoints[Calendar.ActiveEndpointIndex];

        /// <summary>
        /// Проверка подключения; возвращает число элементов в основном календаре. Без повторов: человек ждёт ответа,
        /// и «нет связи» должно появиться сразу, а не через полминуты попыток.
        /// </summary>
        public Task<int> CheckAsync(CancellationToken cancellationToken)
        {
            return Calendar.CountItemsAsync(cancellationToken);
        }

        /// <summary>Вид ошибки EWS для понятного сообщения и политики повторов (<see cref="ErrorClassifier"/>).</summary>
        public static OperationErrorKind Classify(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return EwsErrorMapper.MapException(exception);
        }

        /// <summary>
        /// Подробности ошибки для администратора: вся цепочка исключений (тип и текст), код ответа EWS, состояние
        /// соединения и код HTTP. Верхнее сообщение EWS Managed API часто общее («The request failed»), а причина —
        /// во внутреннем исключении. Пароля в тексте нет: исключения его не содержат.
        /// </summary>
        public static string Explain(Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            var text = new System.Text.StringBuilder();
            int depth = 0;
            for (Exception? current = exception; current != null && depth < 8; current = current.InnerException, depth++)
            {
                if (depth > 0)
                {
                    text.Append("\n← ");
                }

                text.Append(current.GetType().Name).Append(": ").Append(current.Message);
                switch (current)
                {
                    case Microsoft.Exchange.WebServices.Data.ServiceResponseException sre:
                        text.Append(" [код EWS: ").Append(sre.ErrorCode).Append(']');
                        break;
                    case WebException web:
                        text.Append(" [соединение: ").Append(web.Status);
                        if (web.Response is HttpWebResponse response)
                        {
                            try
                            {
                                text.Append(", HTTP ").Append((int)response.StatusCode);
                            }
                            catch (ObjectDisposedException)
                            {
                                // Ответ уже закрыт библиотекой — кода нет.
                            }
                        }

                        text.Append(']');
                        break;
                }
            }

            return text.ToString();
        }

        /// <summary>Снимает проверку сертификата подключения с учёта и закрывает его соединения. Повторный вызов безопасен.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (Endpoint endpoint in _endpoints)
            {
                TlsTrustRegistry.Unregister(endpoint.ConnectionGroup);
                ServicePointManager.FindServicePoint(endpoint.Settings.EwsUrl).CloseConnectionGroup(endpoint.ConnectionGroup);
            }
        }

        /// <summary>Адрес подключения: настройки, своя группа соединений и своя проверка сертификата.</summary>
        private sealed class Endpoint
        {
            public Endpoint(ServerEndpoint settings)
            {
                Settings = settings;
                Validator = new EwsCertificateValidator(settings.EwsUrl, settings.PinnedCertificateThumbprint);
            }

            public ServerEndpoint Settings { get; }

            public EwsCertificateValidator Validator { get; }

            public string ConnectionGroup { get; } = "iwo-" + Guid.NewGuid().ToString("N");
        }
    }
}
