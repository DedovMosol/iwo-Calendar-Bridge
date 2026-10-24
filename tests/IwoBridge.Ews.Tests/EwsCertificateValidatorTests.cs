using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using IwoBridge.Core.Transport;
using IwoBridge.Ews.Transport;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>Проверка валидатора на настоящих (самоподписанных, сгенерированных в тесте) сертификатах.</summary>
    public sealed class EwsCertificateValidatorTests
    {
        private static readonly Uri EwsUrl = new Uri("https://ews.example.test/EWS/Exchange.asmx");
        private static readonly X509Certificate2 ServerCert = CreateSelfSigned("CN=ews.example.test");
        private static readonly X509Certificate2 OtherCert = CreateSelfSigned("CN=other.example.test");
        private static readonly X509Certificate2 InterceptorCert =
            CreateSelfSigned("CN=Kaspersky Anti-Virus Personal Root Certificate");

        private static X509Certificate2 CreateSelfSigned(string subject)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            }
        }

        private static string Sha256Of(X509Certificate2 certificate)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return Thumbprint.ToHex(sha256.ComputeHash(certificate.RawData));
            }
        }

        private static object EwsSender() => WebRequest.Create(EwsUrl);

        [Fact]
        public void OtherHost_UsesStandardValidation_AndDoesNotPin()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);
            object other = WebRequest.Create("https://other.example.test/");

            Assert.True(validator.Validate(other, ServerCert, null, SslPolicyErrors.None));
            Assert.False(validator.Validate(other, ServerCert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Null(validator.LastDecision);
            Assert.Null(validator.PinnedThumbprint);
        }

        [Fact]
        public void SameHostOtherPort_UsesStandardValidation()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);
            object otherPort = WebRequest.Create("https://ews.example.test:8443/EWS/Exchange.asmx");

            Assert.False(validator.Validate(otherPort, ServerCert, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.Null(validator.PinnedThumbprint);
        }

        [Fact]
        public void FirstUse_TrustedChain_NameMismatchIgnored_PinsSha256()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);

            bool accepted = validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.RemoteCertificateNameMismatch);

            Assert.True(accepted);
            Assert.Equal(CertificateDecision.AcceptFirstUse, validator.LastDecision);
            Assert.Equal(Sha256Of(ServerCert), validator.PinnedThumbprint);
        }

        [Fact]
        public void AfterFirstUse_SameCertificate_AcceptedAsPinned()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);
            validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.None);

            Assert.True(validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.None));
            Assert.Equal(CertificateDecision.AcceptPinned, validator.LastDecision);
        }

        [Fact]
        public void AfterFirstUse_DifferentCertificate_Rejected()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);
            validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.None);

            Assert.False(validator.Validate(EwsSender(), OtherCert, null, SslPolicyErrors.None));
            Assert.Equal(CertificateDecision.RejectPinMismatch, validator.LastDecision);
        }

        [Fact]
        public void FirstUse_UntrustedChain_Rejected_NotPinned()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);

            Assert.False(validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Equal(CertificateDecision.RejectUntrusted, validator.LastDecision);
            Assert.Null(validator.PinnedThumbprint);
        }

        [Fact]
        public void PinnedSha1Thumbprint_AcceptedDespiteChainErrors()
        {
            var validator = new EwsCertificateValidator(EwsUrl, ServerCert.Thumbprint);

            Assert.True(validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Equal(CertificateDecision.AcceptPinned, validator.LastDecision);
        }

        [Fact]
        public void PinnedSha256_Accepted()
        {
            var validator = new EwsCertificateValidator(EwsUrl, Sha256Of(ServerCert));

            Assert.True(validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.None));
        }

        [Fact]
        public void InterceptorIssuer_Rejected()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);

            Assert.False(validator.Validate(EwsSender(), InterceptorCert, null, SslPolicyErrors.None));
            Assert.Equal(CertificateDecision.RejectInterception, validator.LastDecision);
            Assert.Null(validator.PinnedThumbprint);
        }

        [Fact]
        public void NullCertificate_Rejected()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);

            Assert.False(validator.Validate(EwsSender(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.Equal(CertificateDecision.RejectUntrusted, validator.LastDecision);
        }

        [Fact]
        public void HostNameSender_NotEwsEndpoint_StandardValidation()
        {
            // Аудит: закрепление по одному имени хоста без порта шире заявленного; EWS всегда передаёт HttpWebRequest.
            var validator = new EwsCertificateValidator(EwsUrl, null);

            Assert.False(validator.Validate("ews.example.test", ServerCert, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Null(validator.LastDecision);
            Assert.Null(validator.PinnedThumbprint);
        }

        [Fact]
        public void RejectedCertificate_StillDescribedForUser()
        {
            // Самоподписанный сертификат без закрепления отклоняется, но окно должно показать его для решения.
            var validator = new EwsCertificateValidator(EwsUrl, null);

            validator.Validate(EwsSender(), ServerCert, null, SslPolicyErrors.RemoteCertificateChainErrors);

            PresentedCertificate? presented = validator.LastPresented;
            Assert.NotNull(presented);
            Assert.Equal("CN=ews.example.test", presented!.Subject);
            Assert.True(presented.IsSelfSigned);
            Assert.Equal(Sha256Of(ServerCert), presented.Fingerprint.Sha256);
            Assert.Equal(ServerCert.Thumbprint, presented.Fingerprint.Sha1);
            Assert.True(presented.IsValidAt(DateTime.UtcNow));
        }

        [Fact]
        public void OtherHost_NotRecordedAsPresented()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);

            validator.Validate(WebRequest.Create("https://other.example.test/"), ServerCert, null, SslPolicyErrors.None);

            Assert.Null(validator.LastPresented);
        }

        [Fact]
        public void InvalidPinnedThumbprint_Throws()
        {
            Assert.Throws<ArgumentException>(() => new EwsCertificateValidator(EwsUrl, "not-a-thumbprint"));
        }

        [Fact]
        public void ConcurrentFirstUse_OnlyOneCertificateWins()
        {
            var validator = new EwsCertificateValidator(EwsUrl, null);
            object sender = EwsSender();
            var certificates = new X509Certificate2[200];
            var accepted = new bool[200];

            Parallel.For(0, certificates.Length, i =>
            {
                X509Certificate2 certificate = i % 2 == 0 ? ServerCert : OtherCert;
                certificates[i] = certificate;
                accepted[i] = validator.Validate(sender, certificate, null, SslPolicyErrors.None);
            });

            string? pinned = validator.PinnedThumbprint;
            Assert.NotNull(pinned);
            X509Certificate2 winner = pinned == Sha256Of(ServerCert) ? ServerCert : OtherCert;
            for (int i = 0; i < certificates.Length; i++)
            {
                Assert.Equal(ReferenceEquals(certificates[i], winner), accepted[i]);
            }
        }
    }
}
