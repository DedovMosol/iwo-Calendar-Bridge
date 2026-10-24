using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Transport;
using IwoBridge.Ews.Transport;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Общий обработчик проверки сертификатов: решение принимает валидатор группы соединений запроса, подключения не
    /// мешают друг другу (проверка в окне настроек не подменяет проверку сеанса), закрытое подключение снимается с
    /// учёта. Сертификаты генерируются в тесте, адреса синтетические.
    /// </summary>
    public sealed class TlsTrustRegistryTests
    {
        private static readonly Uri EwsUrl = new Uri("https://ews.example.test/EWS/Exchange.asmx");
        private static readonly X509Certificate2 Cert = CreateSelfSigned("CN=ews.example.test");

        private static X509Certificate2 CreateSelfSigned(string subject)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            }
        }

        private static string Sha256(X509Certificate2 certificate)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Thumbprint.ToHex(sha.ComputeHash(certificate.RawData));
            }
        }

        private static HttpWebRequest Request(string group)
        {
            var request = (HttpWebRequest)WebRequest.Create(EwsUrl);
            request.ConnectionGroupName = group;
            return request;
        }

        [Fact]
        public void EachGroup_UsesOwnValidator_NoInterference()
        {
            // Сеанс доверяет самоподписанному сертификату (закреплён), проверка в окне настроек — нет.
            string session = "test-" + Guid.NewGuid().ToString("N");
            string check = "test-" + Guid.NewGuid().ToString("N");
            TlsTrustRegistry.Register(session, new EwsCertificateValidator(EwsUrl, Sha256(Cert)));
            TlsTrustRegistry.Register(check, new EwsCertificateValidator(EwsUrl, null));
            try
            {
                Assert.True(TlsTrustRegistry.Validate(Request(session), Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.False(TlsTrustRegistry.Validate(Request(check), Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));

                // И снова сеанс — регистрация второй группы ничего у него не забрала.
                Assert.True(TlsTrustRegistry.Validate(Request(session), Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            }
            finally
            {
                TlsTrustRegistry.Unregister(session);
                TlsTrustRegistry.Unregister(check);
            }
        }

        [Fact]
        public void UnknownGroup_StandardValidation()
        {
            Assert.True(TlsTrustRegistry.Validate(Request("no-such-group"), Cert, null, SslPolicyErrors.None));
            Assert.False(TlsTrustRegistry.Validate(Request("no-such-group"), Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(TlsTrustRegistry.Validate("ews.example.test", Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void Unregistered_FallsBackToStandard()
        {
            string group = "test-" + Guid.NewGuid().ToString("N");
            TlsTrustRegistry.Register(group, new EwsCertificateValidator(EwsUrl, Sha256(Cert)));
            TlsTrustRegistry.Unregister(group);

            Assert.False(TlsTrustRegistry.Validate(Request(group), Cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void DuplicateGroup_Rejected()
        {
            string group = "test-" + Guid.NewGuid().ToString("N");
            TlsTrustRegistry.Register(group, new EwsCertificateValidator(EwsUrl, null));
            try
            {
                Assert.Throws<InvalidOperationException>(() => TlsTrustRegistry.Register(group, new EwsCertificateValidator(EwsUrl, null)));
            }
            finally
            {
                TlsTrustRegistry.Unregister(group);
            }
        }

        [Fact]
        public void EwsTarget_Dispose_Unregisters_NoLeak()
        {
            var profile = new ConnectionProfile(EwsUrl, null, TimeSpan.Zero, null);
            var credentials = new CredentialSecret("DOMAIN\\user", "secret");
            int before = TlsTrustRegistry.Count;

            var first = new EwsTarget(profile, credentials);
            var second = new EwsTarget(profile, credentials);
            Assert.Equal(before + 2, TlsTrustRegistry.Count);

            first.Dispose();
            first.Dispose();
            second.Dispose();

            Assert.Equal(before, TlsTrustRegistry.Count);
        }
    }
}
