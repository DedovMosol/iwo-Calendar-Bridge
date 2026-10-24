using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Security;
using IwoBridge.Core.Transport;
using IwoBridge.Ews.Mapping;
using IwoBridge.Ews.Transport;

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
            Calendar = new EwsCalendarGateway(
                _endpoints.Select(e => factory.Create(profile.ForEndpoint(e.Settings), credentials, e.ConnectionGroup)).ToArray(),
                profile.MailboxAddress,
                retry);
            foreach (Endpoint endpoint in _endpoints)
            {
                TlsTrustRegistry.Register(endpoint.ConnectionGroup, endpoint.Validator);
            }
        }

        /// <summary>Основной календарь целевого ящика: порты чтения, данных серий, раскрытия окна и удаления.</summary>
        public EwsCalendarGateway Calendar { get; }

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
