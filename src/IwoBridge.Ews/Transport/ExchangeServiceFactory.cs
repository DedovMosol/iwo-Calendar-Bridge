using System;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Ews.Transport
{
    /// <summary>
    /// Создаёт настроенный <see cref="ExchangeService"/>: схема Exchange2007_SP1 (совместима с 2007 SP2…2013),
    /// пояс UTC+0 под кодом, который знает и Windows Server 2003 (<see cref="RequestTimeZone"/>), NTLM-учётные данные с
    /// доменом отдельным полем, своя группа соединений (см. <see cref="TlsTrustRegistry"/>).
    ///
    /// Процессных настроек фабрика не меняет (SoC):
    ///  - версия TLS не задаётся в коде: при цели .NET Framework 4.8 действует SystemDefault, протокол выбирает
    ///    ОС (рекомендация Microsoft «TLS best practices with .NET Framework», правило CA5386). Если сервер
    ///    поддерживает только TLS 1.0, его должна разрешать ОС;
    ///  - проверка сертификата идёт через общий обработчик <see cref="TlsTrustRegistry"/>.
    /// </summary>
    public sealed class ExchangeServiceFactory
    {
        private const int RequestTimeoutMilliseconds = 120000;

        /// <summary>Имя программы в журналах сервера (IIS/EWS) — администратору видно, кто обращается.</summary>
        private const string UserAgent = "iwoCalendarBridge";

        /// <summary>
        /// Пояс запросов: UTC+0 без перехода на летнее время, но под кодом «Greenwich Standard Time», а не «UTC».
        /// Exchange 2007 ищет пояс по коду в реестре Windows сервера, а в Windows Server 2003 кода «UTC» нет: сервер
        /// отвечает ErrorTimeZone («Не удалось найти часовой пояс по коду») на любой запрос — проверено на живом
        /// сервере. «Greenwich Standard Time» есть во всех Windows с XP/2003 и тоже без летнего времени (не путать с
        /// «GMT Standard Time» — это Лондон, с переходом). Времена по-прежнему точные моменты UTC: смещение 0, а
        /// значения без указания зоны ядро трактует как UTC (<c>TimeMapper</c>).
        /// </summary>
        internal static readonly TimeZoneInfo RequestTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Greenwich Standard Time", TimeSpan.Zero, "Greenwich Standard Time", "Greenwich Standard Time");

        /// <param name="connectionGroupName">
        /// Группа соединений подключения: свой пул TLS-соединений и своя проверка сертификата. <c>null</c> — общая
        /// группа (только для тестов фабрики).
        /// </param>
        public ExchangeService Create(ConnectionProfile profile, CredentialSecret credentials, string? connectionGroupName = null)
        {
            if (profile is null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (credentials is null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            WebCredentials webCredentials = credentials.Domain is null
                ? new WebCredentials(credentials.User, credentials.Password)
                : new WebCredentials(credentials.User, credentials.Password, credentials.Domain);

            var service = new ExchangeService(ExchangeVersion.Exchange2007_SP1, RequestTimeZone)
            {
                Url = profile.EwsUrl,
                Credentials = webCredentials,
                KeepAlive = true,
                Timeout = RequestTimeoutMilliseconds,
                UserAgent = UserAgent,
            };

            if (connectionGroupName != null)
            {
                service.ConnectionGroupName = connectionGroupName;
            }

            return service;
        }
    }
}
