using System;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Ews.Transport
{
    /// <summary>
    /// Создаёт настроенный <see cref="ExchangeService"/>: схема Exchange2007_SP1 (совместима с 2007 SP2…2013),
    /// часовой пояс UTC (сервер отдаёт времена в UTC — нет ловушки пояса при чтении), NTLM-учётные данные с
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

            var service = new ExchangeService(ExchangeVersion.Exchange2007_SP1, TimeZoneInfo.Utc)
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
