using System;
using System.Net;
using System.Net.Security;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Ews.Transport;
using Microsoft.Exchange.WebServices.Data;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Фабрика ExchangeService: схема Exchange2007_SP1, пояс UTC, адрес из профиля; процессные настройки TLS не
    /// меняются (протокол выбирает ОС).
    /// </summary>
    public sealed class ExchangeServiceFactoryTests
    {
        private static readonly ConnectionProfile Profile =
            new ConnectionProfile(new Uri("https://ews.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null);

        [Fact]
        public void Create_ConfiguresSchemaUtcAndUrl()
        {
            ExchangeService service = new ExchangeServiceFactory().Create(Profile, new CredentialSecret("EXAMPLE\\user", "secret"));

            Assert.Equal(ExchangeVersion.Exchange2007_SP1, service.RequestedServerVersion);
            Assert.Equal(TimeZoneInfo.Utc.Id, service.TimeZone.Id);
            Assert.Equal(Profile.EwsUrl, service.Url);
            Assert.True(service.KeepAlive);
        }

        [Fact]
        public void Create_DoesNotChangeProcessWideTlsSettings()
        {
            // Общая проверка сертификатов (TlsTrustRegistry) ставится один раз на процесс — при первой регистрации,
            // возможно, в параллельно идущем тесте. Ставим её заранее, иначе сравнение «до/после» ловит чужую установку
            // (так тест однажды упал при полном прогоне).
            string warmUp = "warm-up-" + Guid.NewGuid().ToString("N");
            TlsTrustRegistry.Register(warmUp, new EwsCertificateValidator(Profile.EwsUrl, null));
            TlsTrustRegistry.Unregister(warmUp);

            SecurityProtocolType protocolBefore = ServicePointManager.SecurityProtocol;
            RemoteCertificateValidationCallback callbackBefore = ServicePointManager.ServerCertificateValidationCallback;

            new ExchangeServiceFactory().Create(Profile, new CredentialSecret("user@example.test", "secret"));

            Assert.Equal(protocolBefore, ServicePointManager.SecurityProtocol);
            Assert.Same(callbackBefore, ServicePointManager.ServerCertificateValidationCallback);
        }
    }
}
