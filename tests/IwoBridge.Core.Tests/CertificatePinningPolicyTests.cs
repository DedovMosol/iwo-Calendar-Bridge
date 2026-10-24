using System;
using IwoBridge.Core.Transport;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Политика сертификата сервера: совпадение закреплённого SHA-1/SHA-256, первое подключение (TOFU) только при доверенной цепочке, распознавание перехвата HTTPS антивирусом или прокси (T21).
    /// </summary>
    public sealed class CertificatePinningPolicyTests
    {
        // Фиктивные отпечатки, не из какого-либо окружения.
        private const string Sha1 = "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string OtherSha1 = "B1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string Sha256 = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";
        private const string NormalIssuer = "CN=Internal CA, O=Example, C=RU";
        private const string InterceptorIssuer = "CN=Kaspersky Endpoint Security Personal Certification Authority";

        private static readonly CertificateFingerprint Presented = new CertificateFingerprint(Sha1, Sha256);

        [Fact]
        public void PinnedSha1Match_Accepted_EvenIfChainUntrusted()
        {
            CertificateDecision d = CertificatePinningPolicy.Evaluate(Presented, Sha1, "CN=anything", chainTrusted: false);

            Assert.Equal(CertificateDecision.AcceptPinned, d);
            Assert.True(CertificatePinningPolicy.IsAccepted(d));
        }

        [Fact]
        public void PinnedSha256Match_Accepted()
        {
            Assert.Equal(
                CertificateDecision.AcceptPinned,
                CertificatePinningPolicy.Evaluate(Presented, Sha256, NormalIssuer, chainTrusted: false));
        }

        [Fact]
        public void PinnedWithSeparatorsAndInvisibleMark_Accepted()
        {
            // Так отпечаток копируется из диалога сертификатов Windows: с невидимым U+200E и в нижнем регистре.
            string copied = "\u200Ea1:b2:c3:d4:e5:f6:07:18:29:30:3a:4b:5c:6d:7e:8f:90:10:20:30";

            Assert.Equal(
                CertificateDecision.AcceptPinned,
                CertificatePinningPolicy.Evaluate(Presented, copied, NormalIssuer, chainTrusted: true));
        }

        [Fact]
        public void PinnedMismatch_NormalIssuer_RejectedAsMismatch()
        {
            CertificateDecision d = CertificatePinningPolicy.Evaluate(Presented, OtherSha1, NormalIssuer, chainTrusted: true);

            Assert.Equal(CertificateDecision.RejectPinMismatch, d);
            Assert.False(CertificatePinningPolicy.IsAccepted(d));
        }

        [Fact]
        public void PinnedMismatch_InterceptorIssuer_RejectedAsInterception()
        {
            Assert.Equal(
                CertificateDecision.RejectInterception,
                CertificatePinningPolicy.Evaluate(Presented, OtherSha1, InterceptorIssuer, chainTrusted: true));
        }

        [Fact]
        public void FirstUse_ChainTrusted_Accepted()
        {
            Assert.Equal(
                CertificateDecision.AcceptFirstUse,
                CertificatePinningPolicy.Evaluate(Presented, null, NormalIssuer, chainTrusted: true));
        }

        [Fact]
        public void FirstUse_ChainUntrusted_Rejected()
        {
            Assert.Equal(
                CertificateDecision.RejectUntrusted,
                CertificatePinningPolicy.Evaluate(Presented, null, NormalIssuer, chainTrusted: false));
        }

        [Fact]
        public void FirstUse_Interceptor_RejectedRegardlessOfChain()
        {
            Assert.Equal(
                CertificateDecision.RejectInterception,
                CertificatePinningPolicy.Evaluate(Presented, null, InterceptorIssuer, chainTrusted: true));
        }

        [Fact]
        public void NullIssuer_NotTreatedAsInterceptor()
        {
            Assert.Equal(
                CertificateDecision.AcceptFirstUse,
                CertificatePinningPolicy.Evaluate(Presented, null, null, chainTrusted: true));
        }

        [Theory]
        [InlineData("CN=Kaspersky Anti-Virus Personal Root Certificate")]
        [InlineData("CN=ESET SSL Filter CA")]
        [InlineData("CN=Fortinet_CA_SSLProxy, O=Fortinet")]
        [InlineData("CN=avast! Web/Mail Shield Root")]
        [InlineData("CN=Dr.Web SpIDer Gate")]
        [InlineData("CN=Zscaler Root CA")]
        public void InterceptorIssuers_Detected(string issuer)
        {
            Assert.Equal(
                CertificateDecision.RejectInterception,
                CertificatePinningPolicy.Evaluate(Presented, null, issuer, chainTrusted: true));
        }

        [Theory]
        [InlineData("CN=Reset Corp Issuing CA")]
        [InlineData("CN=Preset Systems Root")]
        [InlineData("CN=Symantec Class 3 Secure Server CA - G4")]
        [InlineData("CN=Internal CA, O=Example")]
        public void LegitimateIssuers_NotTreatedAsInterceptor(string issuer)
        {
            Assert.Equal(
                CertificateDecision.AcceptFirstUse,
                CertificatePinningPolicy.Evaluate(Presented, null, issuer, chainTrusted: true));
        }

        [Fact]
        public void NullPresented_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CertificatePinningPolicy.Evaluate(null!, Sha1, NormalIssuer, chainTrusted: true));
        }

        [Fact]
        public void Fingerprint_WrongLengths_Throw()
        {
            Assert.Throws<ArgumentException>(() => new CertificateFingerprint("AB", Sha256));
            Assert.Throws<ArgumentException>(() => new CertificateFingerprint(Sha1, Sha1));
        }

        [Fact]
        public void Fingerprint_Matches_ComparesByPinLength()
        {
            Assert.True(Presented.Matches(Sha1));
            Assert.True(Presented.Matches(Sha256));
            Assert.False(Presented.Matches(OtherSha1));
            Assert.False(Presented.Matches(null));
        }
    }
}
